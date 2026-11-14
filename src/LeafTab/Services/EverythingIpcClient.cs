using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LeafTab.Interop;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace LeafTab.Services
{
    internal readonly struct EverythingIpcItem
    {
        public string FullPath { get; }
        public bool IsFolder { get; }
        public ulong FileSize { get; }
        public FILETIME LastWriteTime { get; }

        public EverythingIpcItem(string fullPath, bool isFolder, ulong fileSize, FILETIME lastWriteTime)
        {
            FullPath = fullPath;
            IsFolder = isFolder;
            FileSize = fileSize;
            LastWriteTime = lastWriteTime;
        }
    }

    internal sealed class EverythingQueryResponse
    {
        public bool IpcAvailable { get; set; }
        public bool TimedOut { get; set; }
        public uint TotalItems { get; set; }
        public List<EverythingIpcItem> Items { get; } = new List<EverythingIpcItem>();
    }

    /// <summary>
    /// Zero-DLL native Win32 WM_COPYDATA client for voidtools Everything (1.4.1+ QUERY2W and legacy QUERYW fallback).
    /// Runs an isolated background message-only HWND on a dedicated thread so queries never block the WPF UI thread.
    /// </summary>
    internal sealed class EverythingIpcClient : IDisposable
    {
        public static readonly EverythingIpcClient Shared = new EverythingIpcClient();

        internal const string ReplyWindowClassName = "LeafTabEverythingReplyWnd";
        private const uint EverythingIpcMatchPath = 0x00000004u;
        private const uint EverythingIpcCopyDataQueryW = 2u;
        private const uint EverythingIpcCopyDataQuery2W = 18u;
        private const uint EverythingIpcSortNameAscending = 1u;

        private const uint RequestName = 0x00000001u;
        private const uint RequestPath = 0x00000002u;
        private const uint RequestFullPathAndName = 0x00000004u;
        private const uint RequestExtension = 0x00000008u;
        private const uint RequestSize = 0x00000010u;
        private const uint RequestDateCreated = 0x00000020u;
        private const uint RequestDateModified = 0x00000040u;

        // Only request fields indexed in RAM by default Everything configuration (never request ATTRIBUTES)
        private const uint DefaultQuery2RequestFlags = RequestFullPathAndName | RequestSize | RequestDateModified;

        private const uint ItemFlagFolder = 0x00000001u;
        private const uint ItemFlagDrive = 0x00000002u;

        private const uint WmAppShutdown = 0x8001u;
        private const int DefaultIpcTimeoutMs = 600;

        private static readonly string[] DefaultEverythingWindowClasses =
        {
            "EVERYTHING_TASKBAR_NOTIFICATION",
            "EVERYTHING_TASKBAR_NOTIFICATION_(1.5a)"
        };

        private static readonly object s_classLock = new object();
        private static bool s_replyClassRegistered;
        private static readonly WNDPROC s_replyWndProc = StaticReplyWndProc;
        private static readonly ConcurrentDictionary<IntPtr, WeakReference<EverythingIpcClient>> s_instances =
            new ConcurrentDictionary<IntPtr, WeakReference<EverythingIpcClient>>();

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int nExitCode);

        private static void EnsureReplyWindowClassRegistered()
        {
            lock (s_classLock)
            {
                if (s_replyClassRegistered)
                    return;

                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    style = 0,
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_replyWndProc),
                    hInstance = NativeMethods.GetModuleHandleW(null),
                    lpszClassName = ReplyWindowClassName
                };

                if (NativeMethods.RegisterClassExW(ref wc) != 0)
                {
                    s_replyClassRegistered = true;
                }
            }
        }

        private static IntPtr StaticReplyWndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam)
        {
            if (uMsg == NativeMethods.WM_COPYDATA)
            {
                if (s_instances.TryGetValue(hWnd, out WeakReference<EverythingIpcClient>? weak) &&
                    weak.TryGetTarget(out EverythingIpcClient? client))
                {
                    if (client.HandleCopyDataReply(lParam))
                        return new IntPtr(1);
                }
                return IntPtr.Zero;
            }

            if (uMsg == WmAppShutdown)
            {
                NativeMethods.DestroyWindow(hWnd);
                return IntPtr.Zero;
            }

            if (uMsg == 0x0002 /* WM_DESTROY */)
            {
                s_instances.TryRemove(hWnd, out _);
                PostQuitMessage(0);
                return IntPtr.Zero;
            }

            return NativeMethods.DefWindowProcW(hWnd, uMsg, wParam, lParam);
        }

        private readonly string? _windowClassOverride;
        private readonly SemaphoreSlim _queryGate = new SemaphoreSlim(1, 1);
        private readonly object _threadLock = new object();
        private readonly ConcurrentDictionary<uint, PendingReply> _pendingReplies = new ConcurrentDictionary<uint, PendingReply>();

        private sealed class PendingReply
        {
            public bool ExpectV2 { get; }
            public TaskCompletionSource<EverythingQueryResponse> Completion { get; }

            public PendingReply(bool expectV2)
            {
                ExpectV2 = expectV2;
                Completion = new TaskCompletionSource<EverythingQueryResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        private Thread? _messageThread;
        private IntPtr _replyHwnd;
        private ManualResetEventSlim? _windowReadyEvent;
        private int _createdReplyThreadCount;
        private int _nextMessageId = 100;
        private bool _disposed;

        internal int CreatedReplyThreadCount => Volatile.Read(ref _createdReplyThreadCount);

        internal int TrackedReplyWindowCount
        {
            get
            {
                int count = 0;
                foreach (KeyValuePair<IntPtr, WeakReference<EverythingIpcClient>> kv in s_instances)
                {
                    if (kv.Value.TryGetTarget(out EverythingIpcClient? owner) &&
                        ReferenceEquals(owner, this) &&
                        NativeMethods.IsWindow(kv.Key))
                    {
                        count++;
                    }
                }
                return count;
            }
        }

        public EverythingIpcClient(string? windowClassOverride = null)
        {
            _windowClassOverride = windowClassOverride;
        }

        public bool IsAvailable => FindTargetWindow() != IntPtr.Zero;

        internal IntPtr FindTargetWindow()
        {
            if (!string.IsNullOrEmpty(_windowClassOverride))
                return NativeMethods.FindWindowW(_windowClassOverride, null);

            foreach (string cls in DefaultEverythingWindowClasses)
            {
                IntPtr hwnd = NativeMethods.FindWindowW(cls, null);
                if (hwnd != IntPtr.Zero)
                    return hwnd;
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// Probes whether a directory or drive root has at least one indexed entry in Everything.
        /// Used to fall back to native Win32 scanning when a specific drive/USB/UNC/excluded path is not indexed.
        /// </summary>
        public async Task<bool> IsDirectoryIndexedAsync(string directoryPath, CancellationToken cancellationToken)
        {
            if (_disposed || string.IsNullOrWhiteSpace(directoryPath))
                return false;

            string normalized;
            try
            {
                normalized = Path.GetFullPath(directoryPath).Trim('"');
            }
            catch
            {
                return false;
            }

            string probeQuery;
            if (normalized.Length <= 3 && normalized.Length >= 2 && normalized[1] == ':')
            {
                string drivePrefix = normalized.TrimEnd('\\') + "\\";
                probeQuery = $"\"{drivePrefix}\"";
            }
            else
            {
                string trimmed = normalized.TrimEnd('\\');
                probeQuery = $"\"{trimmed}\"";
            }

            EverythingQueryResponse response = await QueryRawAsync(
                probeQuery,
                maxResults: 1,
                matchPath: true,
                timeoutMs: 350,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return response.IpcAvailable && !response.TimedOut && response.TotalItems > 0;
        }

        public Task<EverythingQueryResponse> QueryRawAsync(
            string searchString,
            uint maxResults,
            bool matchPath,
            int timeoutMs,
            CancellationToken cancellationToken,
            uint offset = 0)
        {
            if (_disposed)
                return Task.FromResult(new EverythingQueryResponse { IpcAvailable = false });

            return QueryRawInternalAsync(searchString, offset, maxResults, matchPath, timeoutMs, cancellationToken);
        }

        private async Task<EverythingQueryResponse> QueryRawInternalAsync(
            string searchString,
            uint offset,
            uint maxResults,
            bool matchPath,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            if (_disposed || FindTargetWindow() == IntPtr.Zero)
                return new EverythingQueryResponse { IpcAvailable = false };

            bool entered = false;
            try
            {
                await _queryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                entered = true;

                // Re-probe target window and coordinate lazy reply window creation inside gate
                IntPtr targetHwnd = FindTargetWindow();
                if (targetHwnd == IntPtr.Zero || _disposed)
                    return new EverythingQueryResponse { IpcAvailable = false };

                IntPtr replyHwnd = EnsureReplyWindow();
                if (replyHwnd == IntPtr.Zero || _disposed)
                    return new EverythingQueryResponse { IpcAvailable = false };

                uint searchFlags = matchPath ? EverythingIpcMatchPath : 0u;
                int effectiveTimeout = timeoutMs > 0 ? timeoutMs : DefaultIpcTimeoutMs;

                EverythingQueryResponse? v2Response = await SendSingleQueryAsync(
                    targetHwnd,
                    replyHwnd,
                    useV2Protocol: true,
                    searchString: searchString,
                    searchFlags: searchFlags,
                    offset: offset,
                    maxResults: maxResults,
                    timeoutMs: effectiveTimeout,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (v2Response != null)
                    return v2Response;

                // Fallback to legacy v1 QUERYW if target window rejected QUERY2W (returned 0 / FALSE)
                EverythingQueryResponse? v1Response = await SendSingleQueryAsync(
                    targetHwnd,
                    replyHwnd,
                    useV2Protocol: false,
                    searchString: searchString,
                    searchFlags: searchFlags,
                    offset: offset,
                    maxResults: maxResults,
                    timeoutMs: effectiveTimeout,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                return v1Response ?? new EverythingQueryResponse { IpcAvailable = false };
            }
            catch (ObjectDisposedException)
            {
                return new EverythingQueryResponse { IpcAvailable = false };
            }
            catch (OperationCanceledException)
            {
                return new EverythingQueryResponse { IpcAvailable = true, TimedOut = true };
            }
            finally
            {
                if (entered)
                {
                    try { _queryGate.Release(); } catch { }
                }
            }
        }

        private async Task<EverythingQueryResponse?> SendSingleQueryAsync(
            IntPtr targetHwnd,
            IntPtr replyHwnd,
            bool useV2Protocol,
            string searchString,
            uint searchFlags,
            uint offset,
            uint maxResults,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            uint msgId = unchecked((uint)Interlocked.Increment(ref _nextMessageId));
            var pending = new PendingReply(expectV2: useV2Protocol);
            _pendingReplies[msgId] = pending;

            byte[] packet = useV2Protocol
                ? BuildQuery2WPacket(replyHwnd, msgId, searchFlags, offset, maxResults, DefaultQuery2RequestFlags, EverythingIpcSortNameAscending, searchString)
                : BuildQueryWPacket(replyHwnd, msgId, searchFlags, offset, maxResults, searchString);

            GCHandle pinned = GCHandle.Alloc(packet, GCHandleType.Pinned);
            try
            {
                var cds = new COPYDATASTRUCT
                {
                    dwData = new IntPtr(useV2Protocol ? EverythingIpcCopyDataQuery2W : EverythingIpcCopyDataQueryW),
                    cbData = packet.Length,
                    lpData = pinned.AddrOfPinnedObject()
                };

                IntPtr sent = NativeMethods.SendMessageTimeoutCopyDataW(
                    targetHwnd,
                    NativeMethods.WM_COPYDATA,
                    replyHwnd,
                    ref cds,
                    NativeMethods.SMTO_NORMAL | NativeMethods.SMTO_ABORTIFHUNG,
                    (uint)timeoutMs,
                    out IntPtr procResult);

                if (sent == IntPtr.Zero || procResult == IntPtr.Zero)
                {
                    // Everything returned FALSE or timed out synchronously (e.g. QUERY2W unsupported)
                    _pendingReplies.TryRemove(msgId, out _);
                    return null;
                }
            }
            finally
            {
                pinned.Free();
            }

            try
            {
                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task delayTask = Task.Delay(timeoutMs, linked.Token);
                Task winner = await Task.WhenAny(pending.Completion.Task, delayTask).ConfigureAwait(false);
                if (winner == pending.Completion.Task)
                {
                    linked.Cancel();
                    return await pending.Completion.Task.ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                return new EverythingQueryResponse { IpcAvailable = true, TimedOut = true };
            }
            finally
            {
                _pendingReplies.TryRemove(msgId, out _);
            }
        }

        internal static byte[] BuildQuery2WPacket(
            IntPtr replyHwnd,
            uint replyCopyDataMessage,
            uint searchFlags,
            uint offset,
            uint maxResults,
            uint requestFlags,
            uint sortType,
            string searchString)
        {
            string safeSearch = searchString ?? string.Empty;
            int stringBytes = (safeSearch.Length + 1) * 2;
            byte[] packet = new byte[28 + stringBytes];

            // Note: reply_hwnd is explicitly 32-bit DWORD per everything_ipc.h even on x64
            WriteUInt32(packet, 0, unchecked((uint)replyHwnd.ToInt64()));
            WriteUInt32(packet, 4, replyCopyDataMessage);
            WriteUInt32(packet, 8, searchFlags);
            WriteUInt32(packet, 12, offset);
            WriteUInt32(packet, 16, maxResults);
            WriteUInt32(packet, 20, requestFlags);
            WriteUInt32(packet, 24, sortType);
            Encoding.Unicode.GetBytes(safeSearch, 0, safeSearch.Length, packet, 28);
            return packet;
        }

        internal static byte[] BuildQueryWPacket(
            IntPtr replyHwnd,
            uint replyCopyDataMessage,
            uint searchFlags,
            uint offset,
            uint maxResults,
            string searchString)
        {
            string safeSearch = searchString ?? string.Empty;
            int stringBytes = (safeSearch.Length + 1) * 2;
            byte[] packet = new byte[20 + stringBytes];

            WriteUInt32(packet, 0, unchecked((uint)replyHwnd.ToInt64()));
            WriteUInt32(packet, 4, replyCopyDataMessage);
            WriteUInt32(packet, 8, searchFlags);
            WriteUInt32(packet, 12, offset);
            WriteUInt32(packet, 16, maxResults);
            Encoding.Unicode.GetBytes(safeSearch, 0, safeSearch.Length, packet, 20);
            return packet;
        }

        private bool HandleCopyDataReply(IntPtr lParam)
        {
            if (lParam == IntPtr.Zero)
                return false;

            COPYDATASTRUCT cds = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
            uint replyId = unchecked((uint)cds.dwData.ToInt64());
            if (!_pendingReplies.TryRemove(replyId, out PendingReply? pending))
                return false;

            if (cds.lpData == IntPtr.Zero || cds.cbData <= 0)
            {
                pending.Completion.TrySetResult(new EverythingQueryResponse { IpcAvailable = true });
                return true;
            }

            byte[] buffer = new byte[cds.cbData];
            Marshal.Copy(cds.lpData, buffer, 0, cds.cbData);

            EverythingQueryResponse parsed = pending.ExpectV2
                ? ParseList2Buffer(buffer)
                : ParseList1Buffer(buffer);

            pending.Completion.TrySetResult(parsed);
            return true;
        }

        internal static EverythingQueryResponse ParseList2Buffer(byte[] buffer)
        {
            var response = new EverythingQueryResponse { IpcAvailable = true };
            if (buffer == null || buffer.Length < 20)
                return response;

            uint totItems = ReadUInt32(buffer, 0);
            uint numItems = ReadUInt32(buffer, 4);
            // uint offset = ReadUInt32(buffer, 8);
            uint reqFlags = ReadUInt32(buffer, 12);
            // uint sortType = ReadUInt32(buffer, 16);

            response.TotalItems = totItems;
            if (numItems == 0)
                return response;

            long itemsTableBytes = 20L + (long)numItems * 8L;
            if (itemsTableBytes > buffer.Length)
                return response;

            response.Items.Capacity = (int)Math.Min(numItems, 4096u);

            for (uint i = 0; i < numItems; i++)
            {
                int entryPos = 20 + (int)i * 8;
                uint itemFlags = ReadUInt32(buffer, entryPos);
                uint dataOffset = ReadUInt32(buffer, entryPos + 4);
                if (dataOffset >= buffer.Length)
                    continue;

                int cursor = (int)dataOffset;
                string? nameOnly = null;
                string? dirOnly = null;
                string? fullPath = null;
                ulong fileSize = 0;
                FILETIME writeTime = default;

                // Field order strictly follows everything_ipc.h EVERYTHING_IPC_ITEM2 comments
                if ((reqFlags & RequestName) != 0 && !TryReadLengthPrefixedUnicode(buffer, ref cursor, out nameOnly))
                    continue;
                if ((reqFlags & RequestPath) != 0 && !TryReadLengthPrefixedUnicode(buffer, ref cursor, out dirOnly))
                    continue;
                if ((reqFlags & RequestFullPathAndName) != 0 && !TryReadLengthPrefixedUnicode(buffer, ref cursor, out fullPath))
                    continue;

                if ((reqFlags & RequestSize) != 0)
                {
                    if (cursor + 8 > buffer.Length) continue;
                    long rawSize = BitConverter.ToInt64(buffer, cursor);
                    fileSize = rawSize > 0 ? unchecked((ulong)rawSize) : 0UL;
                    cursor += 8;
                }

                if ((reqFlags & RequestExtension) != 0 && !TryReadLengthPrefixedUnicode(buffer, ref cursor, out _))
                    continue;

                if ((reqFlags & RequestDateCreated) != 0)
                {
                    if (cursor + 8 > buffer.Length) continue;
                    cursor += 8;
                }

                if ((reqFlags & RequestDateModified) != 0)
                {
                    if (cursor + 8 > buffer.Length) continue;
                    writeTime = new FILETIME
                    {
                        dwLowDateTime = BitConverter.ToInt32(buffer, cursor),
                        dwHighDateTime = BitConverter.ToInt32(buffer, cursor + 4)
                    };
                    cursor += 8;
                }

                if (string.IsNullOrEmpty(fullPath))
                {
                    fullPath = CombinePathSafe(dirOnly, nameOnly);
                }

                if (!string.IsNullOrEmpty(fullPath))
                {
                    bool isFolder = (itemFlags & (ItemFlagFolder | ItemFlagDrive)) != 0;
                    response.Items.Add(new EverythingIpcItem(fullPath!, isFolder, fileSize, writeTime));
                }
            }

            return response;
        }

        internal static EverythingQueryResponse ParseList1Buffer(byte[] buffer)
        {
            var response = new EverythingQueryResponse { IpcAvailable = true };
            if (buffer == null || buffer.Length < 28)
                return response;

            uint totItems = ReadUInt32(buffer, 8);
            uint numItems = ReadUInt32(buffer, 20);
            response.TotalItems = totItems;
            if (numItems == 0)
                return response;

            long itemsTableBytes = 28L + (long)numItems * 12L;
            if (itemsTableBytes > buffer.Length)
                return response;

            response.Items.Capacity = (int)Math.Min(numItems, 4096u);

            for (uint i = 0; i < numItems; i++)
            {
                int entryPos = 28 + (int)i * 12;
                uint itemFlags = ReadUInt32(buffer, entryPos);
                uint nameOffset = ReadUInt32(buffer, entryPos + 4);
                uint pathOffset = ReadUInt32(buffer, entryPos + 8);

                string fileName = ReadNullTerminatedUnicode(buffer, (int)nameOffset);
                string parentPath = ReadNullTerminatedUnicode(buffer, (int)pathOffset);
                string fullPath = CombinePathSafe(parentPath, fileName);

                if (!string.IsNullOrEmpty(fullPath))
                {
                    bool isFolder = (itemFlags & (ItemFlagFolder | ItemFlagDrive)) != 0;
                    response.Items.Add(new EverythingIpcItem(fullPath, isFolder, 0UL, default));
                }
            }

            return response;
        }

        internal IntPtr EnsureReplyWindow()
        {
            ManualResetEventSlim? waitEvt;
            lock (_threadLock)
            {
                if (_disposed)
                    return IntPtr.Zero;

                if (_replyHwnd != IntPtr.Zero && NativeMethods.IsWindow(_replyHwnd))
                    return _replyHwnd;

                if (_messageThread == null || !_messageThread.IsAlive || _windowReadyEvent == null)
                {
                    _replyHwnd = IntPtr.Zero;
                    var readyEvt = new ManualResetEventSlim(false);
                    _windowReadyEvent = readyEvt;

                    var thread = new Thread(() => RunMessageLoop(readyEvt))
                    {
                        IsBackground = true,
                        Name = "LeafTab.EverythingIpc"
                    };
                    _messageThread = thread;
                    Interlocked.Increment(ref _createdReplyThreadCount);
                    thread.Start();
                }

                waitEvt = _windowReadyEvent;
            }

            try
            {
                waitEvt?.Wait(1500);
            }
            catch (ObjectDisposedException)
            {
                return IntPtr.Zero;
            }

            lock (_threadLock)
            {
                if (_disposed || _replyHwnd == IntPtr.Zero || !NativeMethods.IsWindow(_replyHwnd))
                    return IntPtr.Zero;
                return _replyHwnd;
            }
        }

        private void RunMessageLoop(ManualResetEventSlim readyEvt)
        {
            IntPtr hwnd = IntPtr.Zero;
            bool abortDisposed = false;
            try
            {
                EnsureReplyWindowClassRegistered();
                hwnd = NativeMethods.CreateWindowExW(
                    0,
                    ReplyWindowClassName,
                    string.Empty,
                    0,
                    0,
                    0,
                    0,
                    0,
                    NativeMethods.HWND_MESSAGE,
                    IntPtr.Zero,
                    NativeMethods.GetModuleHandleW(null),
                    IntPtr.Zero);

                lock (_threadLock)
                {
                    if (_disposed)
                    {
                        abortDisposed = true;
                    }
                    else if (hwnd != IntPtr.Zero)
                    {
                        s_instances[hwnd] = new WeakReference<EverythingIpcClient>(this);
                        _replyHwnd = hwnd;
                    }
                }
            }
            catch
            {
                lock (_threadLock)
                {
                    _replyHwnd = IntPtr.Zero;
                }
            }
            finally
            {
                readyEvt.Set();
            }

            if (hwnd == IntPtr.Zero)
                return;

            if (abortDisposed)
            {
                if (NativeMethods.IsWindow(hwnd))
                    NativeMethods.DestroyWindow(hwnd);
                return;
            }

            try
            {
                while (NativeMethods.GetMessageW(out NATIVE_MSG msg, IntPtr.Zero, 0, 0) > 0)
                {
                    NativeMethods.TranslateMessage(ref msg);
                    NativeMethods.DispatchMessageW(ref msg);
                }
            }
            finally
            {
                s_instances.TryRemove(hwnd, out _);
                if (NativeMethods.IsWindow(hwnd))
                {
                    NativeMethods.DestroyWindow(hwnd);
                }
                lock (_threadLock)
                {
                    if (_replyHwnd == hwnd)
                        _replyHwnd = IntPtr.Zero;
                }
            }
        }

        private static bool TryReadLengthPrefixedUnicode(byte[] buffer, ref int cursor, out string text)
        {
            text = string.Empty;
            if (cursor < 0 || cursor + 4 > buffer.Length)
                return false;

            uint charLen = ReadUInt32(buffer, cursor);
            long totalBytes = 4L + ((long)charLen + 1L) * 2L;
            if (charLen > 32768 || cursor + totalBytes > buffer.Length)
                return false;

            text = charLen == 0
                ? string.Empty
                : Encoding.Unicode.GetString(buffer, cursor + 4, (int)charLen * 2);

            cursor += (int)totalBytes;
            return true;
        }

        private static string ReadNullTerminatedUnicode(byte[] buffer, int offset)
        {
            if (offset < 0 || offset + 2 > buffer.Length)
                return string.Empty;

            int end = offset;
            while (end + 1 < buffer.Length)
            {
                if (buffer[end] == 0 && buffer[end + 1] == 0)
                    break;
                end += 2;
            }

            return end > offset ? Encoding.Unicode.GetString(buffer, offset, end - offset) : string.Empty;
        }

        private static string CombinePathSafe(string? parentPath, string? fileName)
        {
            if (string.IsNullOrEmpty(parentPath))
                return fileName ?? string.Empty;
            if (string.IsNullOrEmpty(fileName))
                return parentPath!;
            if (parentPath!.EndsWith("\\", StringComparison.Ordinal))
                return parentPath + fileName;
            return parentPath + "\\" + fileName;
        }

        private static void WriteUInt32(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
            buf[offset + 2] = (byte)((value >> 16) & 0xFF);
            buf[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static uint ReadUInt32(byte[] buf, int offset)
        {
            return (uint)(buf[offset] |
                          (buf[offset + 1] << 8) |
                          (buf[offset + 2] << 16) |
                          (buf[offset + 3] << 24));
        }

        public void Dispose()
        {
            Thread? threadToJoin;
            ManualResetEventSlim? readyEvt;

            lock (_threadLock)
            {
                if (_disposed)
                    return;
                _disposed = true;
                threadToJoin = _messageThread;
                readyEvt = _windowReadyEvent;
            }

            foreach (KeyValuePair<uint, PendingReply> kv in _pendingReplies)
            {
                kv.Value.Completion.TrySetCanceled();
            }
            _pendingReplies.Clear();

            if (threadToJoin != null && threadToJoin != Thread.CurrentThread && threadToJoin.IsAlive)
            {
                try { readyEvt?.Wait(1000); } catch { }
            }

            IntPtr hwndToStop;
            lock (_threadLock)
            {
                hwndToStop = _replyHwnd;
            }

            if (hwndToStop != IntPtr.Zero && NativeMethods.IsWindow(hwndToStop))
            {
                NativeMethods.PostMessageW(hwndToStop, WmAppShutdown, IntPtr.Zero, IntPtr.Zero);
            }

            if (threadToJoin != null && threadToJoin != Thread.CurrentThread && threadToJoin.IsAlive)
            {
                threadToJoin.Join(1500);
            }

            try { readyEvt?.Dispose(); } catch { }
            try { _queryGate.Dispose(); } catch { }
        }
    }
}
