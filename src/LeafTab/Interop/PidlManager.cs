using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LeafTab.Interop
{
    internal sealed class FolderDisplayInfo
    {
        public string Title { get; set; } = "文件夹";
        public string AddressText { get; set; } = string.Empty;
        public string ParsingName { get; set; } = string.Empty;
        public string? FileSystemPath { get; set; }
        public ImageSource? Icon { get; set; }
    }

    internal sealed class BreadcrumbNode
    {
        public string Title { get; set; } = string.Empty;
        public string ParsingName { get; set; } = string.Empty;
        public byte[] PidlBytes { get; set; } = Array.Empty<byte>();
        public ImageSource? Icon { get; set; }
    }

    internal sealed class BreadcrumbChildrenResult
    {
        public List<BreadcrumbNode> Children { get; }
        public bool IsTruncated { get; }

        public BreadcrumbChildrenResult(List<BreadcrumbNode> children, bool isTruncated)
        {
            Children = children;
            IsTruncated = isTruncated;
        }
    }

    internal static class PidlManager
    {
        private static readonly Guid BHID_SFObject = new Guid("3981e224-f559-11d3-8e3a-00c04f6837d5");
        private static readonly object s_iconCacheLock = new object();
        private static readonly Dictionary<string, ImageSource> s_smallIconCache =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, (ImageSource? Icon, string TypeName)> s_largeTypeCache =
            new Dictionary<string, (ImageSource? Icon, string TypeName)>(StringComparer.OrdinalIgnoreCase);

        public static IntPtr CreateKnownFolderPidl(Guid knownFolderId)
        {
            Guid copy = knownFolderId;
            int hr = NativeMethods.SHGetKnownFolderIDList(ref copy, 0, IntPtr.Zero, out IntPtr pidl);
            return hr == ShellConstants.S_OK ? pidl : IntPtr.Zero;
        }

        public static IntPtr CreateThisPcPidl() =>
            CreateKnownFolderPidl(NativeMethods.FOLDERID_ComputerFolder);

        public static IntPtr CreateDesktopPidl() =>
            CreateKnownFolderPidl(NativeMethods.FOLDERID_Desktop);

        public static bool IsThisPcPidl(IntPtr pidl)
        {
            if (pidl == IntPtr.Zero)
                return false;

            IntPtr thisPc = CreateThisPcPidl();
            if (thisPc == IntPtr.Zero)
                return false;

            try
            {
                return AreEqual(pidl, thisPc);
            }
            finally
            {
                Free(ref thisPc);
            }
        }

        public static IntPtr Clone(IntPtr pidl) =>
            pidl == IntPtr.Zero ? IntPtr.Zero : NativeMethods.ILClone(pidl);

        public static IntPtr Combine(IntPtr parentPidl, IntPtr childPidl)
        {
            if (parentPidl == IntPtr.Zero || childPidl == IntPtr.Zero)
                return IntPtr.Zero;
            return NativeMethods.ILCombine(parentPidl, childPidl);
        }

        public static void Free(ref IntPtr pidl)
        {
            if (pidl != IntPtr.Zero)
            {
                NativeMethods.ILFree(pidl);
                pidl = IntPtr.Zero;
            }
        }

        public static bool AreEqual(IntPtr a, IntPtr b)
        {
            if (a == b) return true;
            if (a == IntPtr.Zero || b == IntPtr.Zero) return false;
            return NativeMethods.ILIsEqual(a, b);
        }

        /// <summary>
        /// Copies an unmanaged PIDL into a GC-managed byte[] so UI models (breadcrumbs, history, closed tabs)
        /// never hold unmanaged CoTaskMem pointers.
        /// </summary>
        public static byte[] ToBytes(IntPtr pidl)
        {
            if (pidl == IntPtr.Zero)
                return Array.Empty<byte>();

            int offset = 0;
            const int MaxSafePidlBytes = 65536;
            while (offset < MaxSafePidlBytes)
            {
                ushort cb = (ushort)Marshal.ReadInt16(pidl, offset);
                if (cb == 0)
                {
                    int total = offset + 2;
                    byte[] data = new byte[total];
                    Marshal.Copy(pidl, data, 0, total);
                    return data;
                }
                if (cb < 2)
                {
                    return Array.Empty<byte>();
                }
                offset += cb;
            }

            return Array.Empty<byte>();
        }

        public static IntPtr FromBytes(byte[]? data)
        {
            if (!IsValidPidlBytes(data))
                return IntPtr.Zero;

            IntPtr temp = Marshal.AllocHGlobal(data!.Length);
            try
            {
                Marshal.Copy(data, 0, temp, data.Length);
                return NativeMethods.ILClone(temp);
            }
            finally
            {
                Marshal.FreeHGlobal(temp);
            }
        }

        internal static bool IsValidPidlBytes(byte[]? data)
        {
            if (data == null || data.Length < 2 || data.Length > 65536) return false;
            int offset = 0;
            while (offset + 2 <= data.Length)
            {
                int size = data[offset] | (data[offset + 1] << 8);
                if (size == 0) return offset + 2 == data.Length;
                if (size < 2 || size > data.Length - offset - 2) return false;
                offset += size;
            }
            return false;
        }

        public static bool CanNavigateUp(IntPtr pidl)
        {
            if (pidl == IntPtr.Zero)
                return false;

            // Empty root IDList (Desktop namespace root) has cb == 0 at offset 0
            ushort firstCb = (ushort)Marshal.ReadInt16(pidl, 0);
            return firstCb >= 2;
        }

        public static string? GetShellItemString(IShellItem item, SIGDN sigdn)
        {
            int hr = item.GetDisplayName(sigdn, out IntPtr ptr);
            if (hr != ShellConstants.S_OK || ptr == IntPtr.Zero)
                return null;
            try
            {
                return Marshal.PtrToStringUni(ptr);
            }
            finally
            {
                Marshal.FreeCoTaskMem(ptr);
            }
        }

        public static FolderDisplayInfo GetDisplayInfo(IntPtr pidl)
        {
            var info = new FolderDisplayInfo();
            if (pidl == IntPtr.Zero)
                return info;

            Guid iidShellItem = ShellConstants.IID_IShellItem;
            if (NativeMethods.SHCreateItemFromIDList(pidl, ref iidShellItem, out IShellItem item) == ShellConstants.S_OK && item != null)
            {
                try
                {
                    string? title = GetShellItemString(item, SIGDN.SIGDN_NORMALDISPLAY);
                    string? fsPath = GetShellItemString(item, SIGDN.SIGDN_FILESYSPATH);
                    string? editPath = GetShellItemString(item, SIGDN.SIGDN_DESKTOPABSOLUTEEDITING);
                    string? parsing = GetShellItemString(item, SIGDN.SIGDN_DESKTOPABSOLUTEPARSING);

                    if (!string.IsNullOrWhiteSpace(title))
                        info.Title = title!;

                    info.FileSystemPath = !string.IsNullOrWhiteSpace(fsPath) ? fsPath : null;

                    info.AddressText = !string.IsNullOrWhiteSpace(fsPath)
                        ? fsPath!
                        : (!string.IsNullOrWhiteSpace(editPath) ? editPath! : info.Title);

                    info.ParsingName = !string.IsNullOrWhiteSpace(parsing) ? parsing! : info.AddressText;
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }

            if (!string.IsNullOrWhiteSpace(info.ParsingName))
            {
                lock (s_iconCacheLock)
                {
                    if (s_smallIconCache.TryGetValue(info.ParsingName, out ImageSource? cached))
                    {
                        info.Icon = cached;
                        return info;
                    }
                }
            }

            info.Icon = ExtractSmallIcon(pidl);
            if (info.Icon != null && !string.IsNullOrWhiteSpace(info.ParsingName))
            {
                CacheIcon(info.ParsingName, info.Icon);
            }
            return info;
        }

        private static void CacheIcon(string key, ImageSource icon)
        {
            lock (s_iconCacheLock)
            {
                if (s_smallIconCache.Count >= 256)
                {
                    s_smallIconCache.Clear();
                }
                s_smallIconCache[key] = icon;
            }
        }

        private static ImageSource? GetOrExtractSmallIcon(IntPtr pidl, string? cacheKey)
        {
            if (!string.IsNullOrWhiteSpace(cacheKey))
            {
                lock (s_iconCacheLock)
                {
                    if (s_smallIconCache.TryGetValue(cacheKey!, out ImageSource? cached))
                        return cached;
                }
            }

            ImageSource? extracted = ExtractSmallIcon(pidl);
            if (extracted != null && !string.IsNullOrWhiteSpace(cacheKey))
            {
                CacheIcon(cacheKey!, extracted);
            }
            return extracted;
        }

        public static ImageSource? ExtractTypeSmallIcon(string dummyNameOrExt, bool isDirectory)
        {
            string cacheKey = isDirectory ? "__DIR_ICON__" : $"__EXT_{dummyNameOrExt}";
            lock (s_iconCacheLock)
            {
                if (s_smallIconCache.TryGetValue(cacheKey, out ImageSource? cached))
                    return cached;
            }

            var shfi = new SHFILEINFOW();
            uint attrs = isDirectory ? NativeMethods.FILE_ATTRIBUTE_DIRECTORY : NativeMethods.FILE_ATTRIBUTE_NORMAL;
            IntPtr res = NativeMethods.SHGetFileInfoByNameW(
                isDirectory ? "folder" : dummyNameOrExt,
                attrs,
                ref shfi,
                (uint)Marshal.SizeOf<SHFILEINFOW>(),
                NativeMethods.SHGFI_USEFILEATTRIBUTES | NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_SMALLICON);

            if (res == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
                return null;

            try
            {
                BitmapSource bmp = Imaging.CreateBitmapSourceFromHIcon(
                    shfi.hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                CacheIcon(cacheKey, bmp);
                return bmp;
            }
            catch
            {
                return null;
            }
            finally
            {
                NativeMethods.User32DestroyIcon(shfi.hIcon);
            }
        }

        public static (ImageSource? Icon, string TypeName) GetLargeIconAndTypeName(string fullPathOrName, bool isDirectory)
        {
            string input = fullPathOrName ?? string.Empty;
            string ext = string.Empty;
            if (!isDirectory && !string.IsNullOrWhiteSpace(input))
            {
                try
                {
                    ext = Path.GetExtension(input);
                }
                catch
                {
                    ext = string.Empty;
                }
            }

            bool isDriveRoot = isDirectory &&
                               input.Length >= 2 &&
                               input[1] == ':' &&
                               (input.Length == 2 || (input.Length == 3 && (input[2] == '\\' || input[2] == '/')));

            bool isRemoteUnc = input.StartsWith(@"\\", StringComparison.Ordinal) &&
                               !input.StartsWith(@"\\wsl", StringComparison.OrdinalIgnoreCase);

            bool perInstanceIcon = !isDirectory &&
                                   !isRemoteUnc &&
                                   (string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(ext, ".ico", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(ext, ".lnk", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(ext, ".cur", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(ext, ".ani", StringComparison.OrdinalIgnoreCase)) &&
                                   File.Exists(input);

            string cacheKey;
            if (isDriveRoot)
            {
                string driveNorm = input.Length == 2 ? input + "\\" : input.Substring(0, 2) + "\\";
                cacheKey = "__DRV_LARGE_" + driveNorm.ToUpperInvariant();
                input = driveNorm;
            }
            else if (perInstanceIcon)
            {
                cacheKey = "__INST_LARGE_" + input;
            }
            else if (isDirectory)
            {
                cacheKey = "__DIR_LARGE__";
            }
            else
            {
                cacheKey = string.IsNullOrEmpty(ext)
                    ? "__EXT_LARGE_NONE__"
                    : "__EXT_LARGE_" + ext.ToUpperInvariant();
            }

            lock (s_iconCacheLock)
            {
                if (s_largeTypeCache.TryGetValue(cacheKey, out var cached))
                    return cached;
            }

            var shfi = new SHFILEINFOW();
            uint flags = NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON | NativeMethods.SHGFI_TYPENAME;
            string queryPath;
            uint attrs = 0;

            if (isDriveRoot || perInstanceIcon)
            {
                queryPath = input;
            }
            else
            {
                flags |= NativeMethods.SHGFI_USEFILEATTRIBUTES;
                attrs = isDirectory ? NativeMethods.FILE_ATTRIBUTE_DIRECTORY : NativeMethods.FILE_ATTRIBUTE_NORMAL;
                queryPath = isDirectory
                    ? "folder"
                    : (string.IsNullOrEmpty(ext) ? "file" : "file" + ext);
            }

            IntPtr res = NativeMethods.SHGetFileInfoByNameW(
                queryPath,
                attrs,
                ref shfi,
                (uint)Marshal.SizeOf<SHFILEINFOW>(),
                flags);

            ImageSource? icon = null;
            try
            {
                if (res != IntPtr.Zero && shfi.hIcon != IntPtr.Zero)
                {
                    BitmapSource bmp = Imaging.CreateBitmapSourceFromHIcon(
                        shfi.hIcon,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    bmp.Freeze();
                    icon = bmp;
                }
            }
            catch
            {
                icon = null;
            }
            finally
            {
                if (shfi.hIcon != IntPtr.Zero)
                {
                    NativeMethods.User32DestroyIcon(shfi.hIcon);
                }
            }

            string fallbackType = isDirectory
                ? "文件夹"
                : (string.IsNullOrEmpty(ext)
                    ? "文件"
                    : $"{ext.TrimStart('.').ToUpperInvariant()} 文件");

            string typeName = (res != IntPtr.Zero && !string.IsNullOrWhiteSpace(shfi.szTypeName))
                ? shfi.szTypeName.Trim()
                : fallbackType;

            var entry = (icon, typeName);
            lock (s_iconCacheLock)
            {
                if (s_largeTypeCache.Count >= 128)
                {
                    s_largeTypeCache.Clear();
                }
                s_largeTypeCache[cacheKey] = entry;
            }

            return entry;
        }

        /// <summary>
        /// Builds the root-to-leaf breadcrumb node chain matching Windows 11 File Explorer
        /// (e.g. [此电脑] > [新加卷 (D:)] > [project] > [trunk] > [Client]).
        /// </summary>
        public static List<BreadcrumbNode> BuildBreadcrumbChain(IntPtr folderPidl)
        {
            var reversed = new List<(BreadcrumbNode Node, IntPtr RawPidl)>();
            if (folderPidl == IntPtr.Zero)
                return new List<BreadcrumbNode>();

            Guid iidShellItem = ShellConstants.IID_IShellItem;
            if (NativeMethods.SHCreateItemFromIDList(folderPidl, ref iidShellItem, out IShellItem? current) != ShellConstants.S_OK || current == null)
                return new List<BreadcrumbNode>();

            try
            {
                for (int depth = 0; depth < 32 && current != null; depth++)
                {
                    if (NativeMethods.SHGetIDListFromObject(current, out IntPtr itemPidl) == ShellConstants.S_OK && itemPidl != IntPtr.Zero)
                    {
                        string title = GetShellItemString(current, SIGDN.SIGDN_NORMALDISPLAY) ?? "文件夹";
                        string parsing = GetShellItemString(current, SIGDN.SIGDN_DESKTOPABSOLUTEPARSING) ?? title;

                        reversed.Add((new BreadcrumbNode
                        {
                            Title = title,
                            ParsingName = parsing,
                            PidlBytes = ToBytes(itemPidl),
                            Icon = null
                        }, itemPidl));
                    }

                    int hrParent = current.GetParent(out IShellItem? parent);
                    Marshal.ReleaseComObject(current);
                    current = (hrParent == ShellConstants.S_OK) ? parent : null;
                }
                current = null;

                reversed.Reverse();

                // Match Win11 Explorer: omit top-level "桌面" when browsing under "此电脑" / "回收站" / "网络" / "Linux"
                int startIndex = 0;
                if (reversed.Count >= 2)
                {
                    ushort rootCb = (ushort)Marshal.ReadInt16(reversed[0].RawPidl, 0);
                    if (rootCb == 0)
                    {
                        startIndex = 1;
                    }
                }

                var result = new List<BreadcrumbNode>(reversed.Count - startIndex);
                for (int i = startIndex; i < reversed.Count; i++)
                {
                    if (i == startIndex && reversed[i].Node.Icon == null)
                    {
                        reversed[i].Node.Icon = GetOrExtractSmallIcon(reversed[i].RawPidl, reversed[i].Node.ParsingName);
                    }
                    result.Add(reversed[i].Node);
                }

                return result;
            }
            finally
            {
                if (current != null)
                    Marshal.ReleaseComObject(current);

                for (int i = 0; i < reversed.Count; i++)
                {
                    IntPtr p = reversed[i].RawPidl;
                    Free(ref p);
                }
            }
        }

        internal const int MaxBreadcrumbChildFolders = 2000;
        internal static int BreadcrumbOperationTimeoutMs = 8000;

        private static readonly ShellStaLane s_breadcrumbFolderLane = new ShellStaLane("LeafTab breadcrumb folders");
        private static readonly ShellStaLane s_breadcrumbIconLane = new ShellStaLane("LeafTab breadcrumb icons");
        private static readonly ShellStaLane s_virtualSearchLane = new ShellStaLane("LeafTab virtual folder search");

        /// <summary>
        /// 带 WPF 消息泵的常驻后台 STA 通道。
        /// 平时复用单个 STA 线程与 Dispatcher，避免反复创建并遗弃 HwndWrapper；
        /// 当阻塞于底层 COM/Win32 的任务被调用方取消或超时放弃时，自动将该线程标为退休并启用新线程，
        /// 防止慢速或离线网络共享饿死后续本地请求。
        /// </summary>
        internal sealed class ShellStaLane
        {
            private const int MaxRetiredWorkers = 4;

            internal sealed class LaneWorker
            {
                private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);
                private Dispatcher? _dispatcher;
                private int _retired;
                private int _exited;

                public bool IsRetired => Volatile.Read(ref _retired) != 0;
                public bool HasExited => Volatile.Read(ref _exited) != 0;

                public LaneWorker(string threadName, Action<LaneWorker> onThreadExit)
                {
                    var thread = new Thread(() =>
                    {
                        try
                        {
                            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                            Volatile.Write(ref _dispatcher, dispatcher);
                            _ready.Set();
                            Dispatcher.Run();
                        }
                        finally
                        {
                            Volatile.Write(ref _exited, 1);
                            _ready.Set();
                            onThreadExit(this);
                        }
                    })
                    {
                        IsBackground = true,
                        Name = threadName
                    };
                    thread.SetApartmentState(ApartmentState.STA);
                    thread.Start();
                }

                public Dispatcher EnsureDispatcher()
                {
                    _ready.Wait();
                    return Volatile.Read(ref _dispatcher) ?? throw new InvalidOperationException("STA 通道初始化失败。");
                }

                public bool TryRetireAfter(Task finished)
                {
                    if (HasExited || Interlocked.CompareExchange(ref _retired, 1, 0) != 0)
                        return false;

                    // Completion may already have raced past the lane's retirement decision.
                    // In either order, only the retiring request schedules shutdown.
                    _ = finished.ContinueWith(
                        _ => EnsureDispatcher().BeginInvokeShutdown(DispatcherPriority.Normal),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    return true;
                }
            }

            private sealed class ActiveExecution
            {
                private int _abandoned;
                public LaneWorker Worker { get; }
                public TaskCompletionSource<bool> Finished { get; } =
                    new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                public TaskCompletionSource<bool> AbandonedSignal { get; } =
                    new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                public ActiveExecution(LaneWorker worker) => Worker = worker;
                public bool IsAbandoned => Volatile.Read(ref _abandoned) != 0;

                public void MarkAbandoned()
                {
                    if (Interlocked.Exchange(ref _abandoned, 1) == 0)
                        AbandonedSignal.TrySetResult(true);
                }

                public void MarkFinished()
                {
                    Finished.TrySetResult(true);
                }
            }

            private readonly object _gate = new object();
            private readonly string _threadName;
            private LaneWorker? _currentWorker;
            private ActiveExecution? _currentExecution;
            private int _retiredWorkers;
            private TaskCompletionSource<bool> _retiredExitedSignal =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public ShellStaLane(string threadName) => _threadName = threadName;

            private LaneWorker CreateWorkerLocked() => new LaneWorker(_threadName, OnWorkerExited);

            private void OnWorkerExited(LaneWorker worker)
            {
                TaskCompletionSource<bool>? signal = null;
                lock (_gate)
                {
                    if (ReferenceEquals(_currentWorker, worker))
                        _currentWorker = null;

                    if (worker.IsRetired)
                    {
                        if (_retiredWorkers > 0)
                            _retiredWorkers--;
                        signal = _retiredExitedSignal;
                        _retiredExitedSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                }
                signal?.TrySetResult(true);
            }

            public async Task<T> RunAsync<T>(Func<CancellationToken, T> work, CancellationToken token, int timeoutMs)
            {
                token.ThrowIfCancellationRequested();
                int effectiveTimeout = timeoutMs > 0 ? timeoutMs : Timeout.Infinite;
                long deadlineTicks = effectiveTimeout == Timeout.Infinite
                    ? long.MaxValue
                    : Stopwatch.GetTimestamp() + (long)(effectiveTimeout * (Stopwatch.Frequency / 1000.0));

                ActiveExecution? acquired = null;
                while (acquired == null)
                {
                    Task? waitTask = null;
                    lock (_gate)
                    {
                        token.ThrowIfCancellationRequested();
                        if (_currentExecution == null || _currentExecution.Finished.Task.IsCompleted)
                        {
                            LaneWorker worker = _currentWorker != null && !_currentWorker.IsRetired && !_currentWorker.HasExited
                                ? _currentWorker
                                : CreateWorkerLocked();
                            _currentWorker = worker;
                            acquired = new ActiveExecution(worker);
                            _currentExecution = acquired;
                        }
                        else if (_currentExecution.IsAbandoned && _retiredWorkers < MaxRetiredWorkers)
                        {
                            if (_currentExecution.Worker.TryRetireAfter(_currentExecution.Finished.Task))
                                _retiredWorkers++;
                            LaneWorker worker = CreateWorkerLocked();
                            _currentWorker = worker;
                            acquired = new ActiveExecution(worker);
                            _currentExecution = acquired;
                        }
                        else
                        {
                            waitTask = _currentExecution.IsAbandoned
                                ? Task.WhenAny(_currentExecution.Finished.Task, _retiredExitedSignal.Task)
                                : Task.WhenAny(_currentExecution.Finished.Task, _currentExecution.AbandonedSignal.Task);
                        }
                    }

                    if (acquired == null && waitTask != null)
                    {
                        await WaitWithTokenAndDeadlineAsync(waitTask, token, deadlineTicks).ConfigureAwait(false);
                    }
                }

                var resultTcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                CancellationTokenSource opCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                CancellationToken opToken = opCts.Token;
                bool handedOffToSta = false;
                try
                {
                    Dispatcher dispatcher = acquired.Worker.EnsureDispatcher();
                    _ = dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            opToken.ThrowIfCancellationRequested();
                            resultTcs.TrySetResult(work(opToken));
                        }
                        catch (OperationCanceledException)
                        {
                            resultTcs.TrySetCanceled();
                        }
                        catch (Exception ex)
                        {
                            resultTcs.TrySetException(ex);
                        }
                        finally
                        {
                            acquired.MarkFinished();
                        }
                    }));
                    handedOffToSta = true;

                    return await AwaitResultWithTokenAndDeadlineAsync(resultTcs.Task, acquired, opCts, token, deadlineTicks)
                        .ConfigureAwait(false);
                }
                catch
                {
                    if (!handedOffToSta)
                        acquired.MarkFinished();
                    throw;
                }
                finally
                {
                    if (acquired.Finished.Task.IsCompleted)
                    {
                        opCts.Dispose();
                    }
                    else
                    {
                        // STA 回调仍在运行（例如阻塞在 OS COM 调用），推迟到回调退出后再释放链接 CTS
                        _ = acquired.Finished.Task.ContinueWith(
                            _ => opCts.Dispose(),
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }
                }
            }

            private static int ComputeRemainingMs(long deadlineTicks)
            {
                if (deadlineTicks == long.MaxValue)
                    return Timeout.Infinite;
                double remainingMs = (deadlineTicks - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
                if (remainingMs <= 0)
                    return 0;
                return remainingMs >= int.MaxValue ? int.MaxValue - 1 : Math.Max(1, (int)Math.Ceiling(remainingMs));
            }

            private static async Task WaitWithTokenAndDeadlineAsync(Task waitTask, CancellationToken token, long deadlineTicks)
            {
                token.ThrowIfCancellationRequested();
                int remainingMs = ComputeRemainingMs(deadlineTicks);
                if (remainingMs == 0)
                    throw new TimeoutException("后台 Shell 操作等待超时。");

                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                Task delayTask = Task.Delay(remainingMs, waitCts.Token);
                Task completed = await Task.WhenAny(waitTask, delayTask).ConfigureAwait(false);
                if (ReferenceEquals(completed, waitTask))
                {
                    waitCts.Cancel();
                    return;
                }

                token.ThrowIfCancellationRequested();
                throw new TimeoutException("后台 Shell 操作等待超时。");
            }

            private static async Task<T> AwaitResultWithTokenAndDeadlineAsync<T>(
                Task<T> resultTask,
                ActiveExecution execution,
                CancellationTokenSource opCts,
                CancellationToken callerToken,
                long deadlineTicks)
            {
                int remainingMs = ComputeRemainingMs(deadlineTicks);
                if (remainingMs == 0)
                {
                    try { opCts.Cancel(); } catch { }
                    execution.MarkAbandoned();
                    callerToken.ThrowIfCancellationRequested();
                    throw new TimeoutException("后台 Shell 操作执行超时。");
                }

                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
                Task delayTask = Task.Delay(remainingMs, waitCts.Token);
                Task completed = await Task.WhenAny(resultTask, delayTask).ConfigureAwait(false);
                if (ReferenceEquals(completed, resultTask))
                {
                    waitCts.Cancel();
                    return await resultTask.ConfigureAwait(false);
                }

                try { opCts.Cancel(); } catch { }
                execution.MarkAbandoned();
                callerToken.ThrowIfCancellationRequested();
                throw new TimeoutException("后台 Shell 操作执行超时。");
            }
        }

        internal static Task<T> RunBreadcrumbFolderLaneAsync<T>(
            Func<CancellationToken, T> work,
            CancellationToken token,
            int? timeoutMs = null) =>
            s_breadcrumbFolderLane.RunAsync(work, token, timeoutMs ?? BreadcrumbOperationTimeoutMs);

        internal static Task<T> RunVirtualSearchAsync<T>(
            Func<CancellationToken, T> work,
            CancellationToken token,
            int timeoutMs) => s_virtualSearchLane.RunAsync(work, token, timeoutMs);

        internal static Task<BreadcrumbChildrenResult> GetChildFoldersResultAsync(
            byte[] parent,
            CancellationToken token,
            int maxCount = MaxBreadcrumbChildFolders)
        {
            return s_breadcrumbFolderLane.RunAsync(staToken =>
            {
                IntPtr pidl = FromBytes(parent);
                try { return EnumerateChildFolders(pidl, maxCount, staToken); }
                finally { Free(ref pidl); }
            }, token, BreadcrumbOperationTimeoutMs);
        }

        internal static async Task<List<BreadcrumbNode>> GetChildFoldersAsync(byte[] parent, CancellationToken token)
        {
            BreadcrumbChildrenResult result = await GetChildFoldersResultAsync(parent, token, MaxBreadcrumbChildFolders)
                .ConfigureAwait(false);
            return result.Children;
        }

        internal static Task<ImageSource?[]> GetChildFolderIconsAsync(List<BreadcrumbNode> page, CancellationToken token)
        {
            return s_breadcrumbIconLane.RunAsync(staToken =>
            {
                var icons = new ImageSource?[page.Count];
                for (int i = 0; i < page.Count; i++)
                {
                    staToken.ThrowIfCancellationRequested();
                    IntPtr pidl = FromBytes(page[i].PidlBytes);
                    try { icons[i] = GetOrExtractSmallIcon(pidl, page[i].ParsingName); }
                    finally { Free(ref pidl); }
                }
                return icons;
            }, token, BreadcrumbOperationTimeoutMs);
        }

        private static BreadcrumbChildrenResult EnumerateChildFolders(IntPtr parentPidl, int maxCount, CancellationToken token)
        {
            var children = new List<BreadcrumbNode>();
            if (parentPidl == IntPtr.Zero)
                return new BreadcrumbChildrenResult(children, false);

            Guid iidShellItem = ShellConstants.IID_IShellItem;
            int hrCreate = NativeMethods.SHCreateItemFromIDList(parentPidl, ref iidShellItem, out IShellItem? parentItem);
            if (hrCreate < 0)
                Marshal.ThrowExceptionForHR(hrCreate, new IntPtr(-1));
            if (parentItem == null)
                return new BreadcrumbChildrenResult(children, false);

            IntPtr pFolder = IntPtr.Zero;
            IShellFolder? shellFolder = null;
            IntPtr pEnum = IntPtr.Zero;
            IEnumIDList? enumIdList = null;

            try
            {
                Guid bhid = BHID_SFObject;
                Guid iidFolder = ShellConstants.IID_IShellFolder;
                int hrBind = parentItem.BindToHandler(IntPtr.Zero, ref bhid, ref iidFolder, out pFolder);
                if (hrBind < 0)
                    Marshal.ThrowExceptionForHR(hrBind, new IntPtr(-1));
                if (pFolder == IntPtr.Zero)
                    return new BreadcrumbChildrenResult(children, false);

                shellFolder = (IShellFolder)Marshal.GetObjectForIUnknown(pFolder);
                (bool _, bool showHidden) = GetShellVisibilityFlags();
                uint flags = ShellConstants.SHCONTF_FOLDERS | (showHidden ? ShellConstants.SHCONTF_INCLUDEHIDDEN : 0u);

                int hrEnum = shellFolder.EnumObjects(IntPtr.Zero, flags, out pEnum);
                if (hrEnum == ShellConstants.S_FALSE)
                    return new BreadcrumbChildrenResult(children, false);
                if (hrEnum < 0)
                    Marshal.ThrowExceptionForHR(hrEnum, new IntPtr(-1));
                if (pEnum == IntPtr.Zero)
                    return new BreadcrumbChildrenResult(children, false);

                enumIdList = (IEnumIDList)Marshal.GetObjectForIUnknown(pEnum);
                bool truncated = false;
                int effectiveLimit = Math.Max(1, maxCount);

                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    IntPtr childRelPidl = NextChildPidl(enumIdList);
                    if (childRelPidl == IntPtr.Zero)
                        break;

                    IntPtr fullChildPidl = IntPtr.Zero;
                    try
                    {
                        if (NativeMethods.SHCreateItemWithParent(parentPidl, shellFolder, childRelPidl, ref iidShellItem, out IShellItem? childItem) == ShellConstants.S_OK && childItem != null)
                        {
                            try
                            {
                                uint attrMask = ShellConstants.SFGAO_FOLDER | ShellConstants.SFGAO_STREAM;
                                if (childItem.GetAttributes(attrMask, out uint attrs) >= 0 &&
                                    (attrs & ShellConstants.SFGAO_FOLDER) != 0 &&
                                    (attrs & ShellConstants.SFGAO_STREAM) == 0)
                                {
                                    if (children.Count >= effectiveLimit)
                                    {
                                        truncated = true;
                                        break;
                                    }

                                    fullChildPidl = Combine(parentPidl, childRelPidl);
                                    if (fullChildPidl != IntPtr.Zero)
                                    {
                                        string title = GetShellItemString(childItem, SIGDN.SIGDN_NORMALDISPLAY) ?? "文件夹";
                                        string parsing = GetShellItemString(childItem, SIGDN.SIGDN_DESKTOPABSOLUTEPARSING) ?? title;
                                        children.Add(new BreadcrumbNode
                                        {
                                            Title = title,
                                            ParsingName = parsing,
                                            PidlBytes = ToBytes(fullChildPidl)
                                        });
                                    }
                                }
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(childItem);
                            }
                        }
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        // 单个损坏或无权限子项不应中断整级目录枚举
                    }
                    finally
                    {
                        Free(ref fullChildPidl);
                        Free(ref childRelPidl);
                    }
                }

                children.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
                return new BreadcrumbChildrenResult(children, truncated);
            }
            finally
            {
                if (enumIdList != null) Marshal.ReleaseComObject(enumIdList);
                if (pEnum != IntPtr.Zero) Marshal.Release(pEnum);
                if (shellFolder != null) Marshal.ReleaseComObject(shellFolder);
                if (pFolder != IntPtr.Zero) Marshal.Release(pFolder);
                Marshal.ReleaseComObject(parentItem);
            }
        }

        internal static IntPtr NextChildPidl(IEnumIDList enumerator)
        {
            int hr = enumerator.Next(1, out IntPtr child, out uint fetched);
            if (hr == ShellConstants.S_FALSE)
                return IntPtr.Zero;
            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr, new IntPtr(-1));
            if (hr != ShellConstants.S_OK || fetched != 1 || child == IntPtr.Zero)
                throw new InvalidDataException("Shell 子文件夹枚举器返回了无效的结果。");
            return child;
        }

        public static (bool ShowExtensions, bool ShowHidden) GetShellVisibilityFlags()
        {
            var state = new SHELLSTATE();
            NativeMethods.SHGetSetSettings(
                ref state,
                NativeMethods.SSF_SHOWEXTENSIONS | NativeMethods.SSF_SHOWALLOBJECTS,
                false);
            return (state.ShowExtensions, state.ShowAllObjects);
        }

        public static void SetShellVisibilityFlags(bool? showExtensions, bool? showHidden)
        {
            var state = new SHELLSTATE();
            uint mask = 0;

            if (showExtensions.HasValue)
            {
                state.ShowExtensions = showExtensions.Value;
                mask |= NativeMethods.SSF_SHOWEXTENSIONS;
            }

            if (showHidden.HasValue)
            {
                state.ShowAllObjects = showHidden.Value;
                mask |= NativeMethods.SSF_SHOWALLOBJECTS;
            }

            if (mask != 0)
            {
                NativeMethods.SHGetSetSettings(ref state, mask, true);
                // This changes ShellState, not file associations or the global icon cache.
                _ = Task.Run(() => NativeMethods.SendMessageTimeoutW(new IntPtr(0xffff), 0x001A,
                    IntPtr.Zero, "ShellState", 0x0002, 100, out _));
            }
        }

        public static string FormatByteSize(ulong bytes)
        {
            if (bytes < 1024UL)
                return $"{bytes} 字节";
            double kb = bytes / 1024.0;
            if (kb < 1024.0)
                return kb >= 100 ? $"{kb:F0} KB" : $"{kb:F1} KB";
            double mb = kb / 1024.0;
            if (mb < 1024.0)
                return mb >= 100 ? $"{mb:F0} MB" : $"{mb:F2} MB";
            double gb = mb / 1024.0;
            if (gb < 1024.0)
                return $"{gb:F2} GB";
            double tb = gb / 1024.0;
            return $"{tb:F2} TB";
        }

        public static ImageSource? ExtractSmallIcon(IntPtr pidl)
        {
            if (pidl == IntPtr.Zero)
                return null;

            var shfi = new SHFILEINFOW();
            IntPtr res = NativeMethods.SHGetFileInfoW(
                pidl,
                0,
                ref shfi,
                (uint)Marshal.SizeOf<SHFILEINFOW>(),
                NativeMethods.SHGFI_PIDL | NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_SMALLICON);

            if (res == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
                return null;

            try
            {
                BitmapSource bmp = Imaging.CreateBitmapSourceFromHIcon(
                    shfi.hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
            finally
            {
                NativeMethods.User32DestroyIcon(shfi.hIcon);
            }
        }

        public static bool IsExplorerLauncherTarget(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            string s = raw!.Trim().Trim('"').Trim();
            if (s.Length == 0)
                return false;

            // Windows 10/11 File Explorer launcher stub CLSID (invoked by Taskbar/Start Menu File Explorer shortcut & Win+E)
            string namespaceName = s.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ? s.Substring(6) : s;
            if (string.Equals(namespaceName, "::{52205fd8-5dfb-447d-801a-d0b52f2e83e1}", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(namespaceName, "{52205fd8-5dfb-447d-801a-d0b52f2e83e1}", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(s, "文件资源管理器", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "File Explorer", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "Windows 资源管理器", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "Windows Explorer", StringComparison.OrdinalIgnoreCase))
            {
                return !Directory.Exists(s);
            }

            if (s.EndsWith(@"\File Explorer.lnk", StringComparison.OrdinalIgnoreCase) ||
                s.EndsWith(@"\文件资源管理器.lnk", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "File Explorer.lnk", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "文件资源管理器.lnk", StringComparison.OrdinalIgnoreCase))
            {
                return IsExplorerLauncherShortcut(s);
            }

            return false;
        }

        private static bool IsExplorerLauncherShortcut(string path)
        {
            // Alias detection also runs on the UI thread before the bounded
            // asynchronous UNC parser. Never probe a network shortcut here.
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            if (!File.Exists(path)) return false;
            object? instance = null;
            try
            {
                Type? linkType = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), false);
                if (linkType == null) return false;
                instance = Activator.CreateInstance(linkType);
                if (instance is not IShellLinkW link || instance is not System.Runtime.InteropServices.ComTypes.IPersistFile file) return false;
                file.Load(path, 0);
                var target = new System.Text.StringBuilder(32768);
                var arguments = new System.Text.StringBuilder(32768);
                if (link.GetPath(target, target.Capacity, IntPtr.Zero, 0) < 0 ||
                    link.GetArguments(arguments, arguments.Capacity) < 0) return false;
                return LeafTab.Shared.ShellAssociationRecovery.PathsEqual(target.ToString(),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) &&
                    string.IsNullOrWhiteSpace(arguments.ToString());
            }
            catch { return false; }
            finally { if (instance != null && Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance); }
        }

        public static IntPtr NormalizeToBrowsablePidl(IntPtr pidl)
        {
            if (pidl == IntPtr.Zero)
                return IntPtr.Zero;

            Guid iidShellItem = ShellConstants.IID_IShellItem;
            if (NativeMethods.SHCreateItemFromIDList(pidl, ref iidShellItem, out IShellItem? item) == ShellConstants.S_OK && item != null)
            {
                try
                {
                    string? parsing = GetShellItemString(item, SIGDN.SIGDN_DESKTOPABSOLUTEPARSING);
                    string? fileSystemPath = GetShellItemString(item, SIGDN.SIGDN_FILESYSPATH);
                    if (string.IsNullOrWhiteSpace(fileSystemPath) && IsExplorerLauncherTarget(parsing))
                    {
                        Free(ref pidl);
                        return CreateThisPcPidl();
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }

            return pidl;
        }

        /// <summary>
        /// Synchronous local/shell parsing helper used during startup session restoration for known local/virtual paths.
        /// Skips blocking UNC network paths unless allowUnc is true.
        /// </summary>
        public static IntPtr TryParseLocalOrShellFast(string? rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                return IntPtr.Zero;

            string expanded = Environment.ExpandEnvironmentVariables(rawPath!.Trim().Trim('"'));

            IntPtr known = TryParseKnownAlias(expanded);
            if (known != IntPtr.Zero)
                return known;

            bool isUnc = expanded.StartsWith(@"\\", StringComparison.Ordinal) &&
                         !expanded.StartsWith(@"\\wsl", StringComparison.OrdinalIgnoreCase);
            if (isUnc)
                return IntPtr.Zero;

            if (expanded.Length == 2 && char.IsLetter(expanded[0]) && expanded[1] == ':')
                expanded += @"\";

            if (NativeMethods.SHParseDisplayName(expanded, IntPtr.Zero, out IntPtr pidl, 0, out _) == ShellConstants.S_OK && pidl != IntPtr.Zero)
                return NormalizeToBrowsablePidl(pidl);

            return IntPtr.Zero;
        }

        /// <summary>
        /// Parses user address bar input (local path, environment variables, friendly aliases, shell: commands, or UNC paths).
        /// For UNC paths (\\server\share), performs a bounded async pre-check on a ThreadPool worker so dead network hosts
        /// time out cleanly instead of freezing the UI thread for 20-30s during synchronous SHParseDisplayName.
        /// </summary>
        public static async Task<(IntPtr Pidl, string? ErrorMessage)> TryParseAddressAsync(
            string rawInput,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(rawInput))
                return (IntPtr.Zero, "请输入有效路径。");

            string expanded = Environment.ExpandEnvironmentVariables(rawInput.Trim().Trim('"'));

            IntPtr fastKnown = TryParseKnownAlias(expanded);
            if (fastKnown != IntPtr.Zero)
                return (fastKnown, null);

            bool isUnc = expanded.StartsWith(@"\\", StringComparison.Ordinal) &&
                         !expanded.StartsWith(@"\\wsl", StringComparison.OrdinalIgnoreCase);

            if (isUnc)
            {
                Task<IntPtr> uncParseTask = Task.Run(() =>
                {
                    int hr = NativeMethods.SHParseDisplayName(expanded, IntPtr.Zero, out IntPtr parsedPidl, 0, out _);
                    return hr == ShellConstants.S_OK ? parsedPidl : IntPtr.Zero;
                });

                Task completed = await Task.WhenAny(uncParseTask, Task.Delay(2500, cancellationToken)).ConfigureAwait(true);
                if (completed != uncParseTask)
                {
                    _ = uncParseTask.ContinueWith(t =>
                    {
                        if (t.Status == TaskStatus.RanToCompletion && t.Result != IntPtr.Zero)
                        {
                            IntPtr late = t.Result;
                            Free(ref late);
                        }
                    }, TaskScheduler.Default);
                    return (IntPtr.Zero, $"网络路径响应超时（>2.5秒）：{expanded}");
                }

                IntPtr uncPidl = await uncParseTask.ConfigureAwait(true);
                return uncPidl != IntPtr.Zero
                    ? (uncPidl, null)
                    : (IntPtr.Zero, $"无法访问网络路径：{expanded}");
            }

            if (expanded.Length == 2 && char.IsLetter(expanded[0]) && expanded[1] == ':')
                expanded += @"\";

            int parseHr = NativeMethods.SHParseDisplayName(expanded, IntPtr.Zero, out IntPtr resultPidl, 0, out _);
            if (parseHr == ShellConstants.S_OK && resultPidl != IntPtr.Zero)
                return (NormalizeToBrowsablePidl(resultPidl), null);

            if (Directory.Exists(expanded))
            {
                string full = Path.GetFullPath(expanded);
                if (NativeMethods.SHParseDisplayName(full, IntPtr.Zero, out resultPidl, 0, out _) == ShellConstants.S_OK)
                    return (NormalizeToBrowsablePidl(resultPidl), null);
            }

            return (IntPtr.Zero, $"找不到路径：{expanded}");
        }

        private static IntPtr TryParseKnownAlias(string expanded)
        {
            if (IsExplorerLauncherTarget(expanded) ||
                string.Equals(expanded, "此电脑", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(expanded, "This PC", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(expanded, "计算机", StringComparison.OrdinalIgnoreCase))
            {
                return CreateThisPcPidl();
            }

            if (string.Equals(expanded, "主文件夹", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(expanded, "快速访问", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(expanded, "Quick access", StringComparison.OrdinalIgnoreCase))
            {
                if (NativeMethods.SHParseDisplayName("shell:::{f874310e-b6b7-47dc-bc84-b9e6b38f5903}", IntPtr.Zero, out IntPtr homePidl, 0, out _) == ShellConstants.S_OK && homePidl != IntPtr.Zero)
                    return homePidl;
                return CreateThisPcPidl();
            }

            if (string.Equals(expanded, "桌面", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(expanded, "Desktop", StringComparison.OrdinalIgnoreCase))
            {
                return CreateDesktopPidl();
            }

            if (string.Equals(expanded, "回收站", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(expanded, "Recycle Bin", StringComparison.OrdinalIgnoreCase))
            {
                return CreateKnownFolderPidl(NativeMethods.FOLDERID_RecycleBinFolder);
            }

            if (string.Equals(expanded, "下载", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(expanded, "Downloads", StringComparison.OrdinalIgnoreCase))
            {
                return CreateKnownFolderPidl(NativeMethods.FOLDERID_Downloads);
            }

            if (string.Equals(expanded, "文档", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(expanded, "Documents", StringComparison.OrdinalIgnoreCase))
            {
                return CreateKnownFolderPidl(NativeMethods.FOLDERID_Documents);
            }

            if (string.Equals(expanded, "图片", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(expanded, "Pictures", StringComparison.OrdinalIgnoreCase))
            {
                return CreateKnownFolderPidl(NativeMethods.FOLDERID_Pictures);
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// Uses an OLE IBindCtx populated with STR_FILE_SYS_BIND_DATA (IFileSystemBindData) so
        /// SHParseDisplayName constructs file/folder PIDLs from already-known metadata without
        /// issuing synchronous disk stat syscalls on the STA UI thread. Not thread-safe across
        /// concurrent callers; create one instance per worker task and Dispose when finished.
        /// </summary>
        internal sealed class FastPidlBinder : IDisposable
        {
            [ComVisible(true)]
            private sealed class ManagedFileSystemBindData : IFileSystemBindData
            {
                private WIN32_FIND_DATAW _data;

                public void SetData(in WIN32_FIND_DATAW data) => _data = data;

                public int SetFindData(ref WIN32_FIND_DATAW pfd)
                {
                    _data = pfd;
                    return ShellConstants.S_OK;
                }

                public int GetFindData(out WIN32_FIND_DATAW pfd)
                {
                    pfd = _data;
                    return ShellConstants.S_OK;
                }
            }

            private readonly ManagedFileSystemBindData _fsData = new ManagedFileSystemBindData();
            private System.Runtime.InteropServices.ComTypes.IBindCtx? _bindCtx;
            private IntPtr _bindCtxPtr;
            private bool _disposed;

            public FastPidlBinder()
            {
                if (NativeMethods.CreateBindCtx(0, out System.Runtime.InteropServices.ComTypes.IBindCtx? ctx) == ShellConstants.S_OK && ctx != null)
                {
                    try
                    {
                        ctx.RegisterObjectParam(ShellConstants.STR_FILE_SYS_BIND_DATA, _fsData);
                        _bindCtxPtr = Marshal.GetIUnknownForObject(ctx);
                        _bindCtx = ctx;
                    }
                    catch
                    {
                        try { Marshal.ReleaseComObject(ctx); } catch { }
                        if (_bindCtxPtr != IntPtr.Zero)
                        {
                            Marshal.Release(_bindCtxPtr);
                            _bindCtxPtr = IntPtr.Zero;
                        }
                        _bindCtx = null;
                    }
                }
            }

            public IntPtr CreatePidlFromMetadata(
                string fullPath,
                uint fileAttributes,
                ulong fileSize,
                System.Runtime.InteropServices.ComTypes.FILETIME lastWriteTime)
            {
                if (_disposed || string.IsNullOrWhiteSpace(fullPath))
                    return IntPtr.Zero;

                string trimmed = fullPath.TrimEnd('\\');
                int sep = trimmed.LastIndexOf('\\');
                string fileName = sep >= 0 ? trimmed.Substring(sep + 1) : trimmed;

                // Drive roots (e.g. "C:") or long filenames >255 chars fall back directly
                if (_bindCtxPtr == IntPtr.Zero ||
                    string.IsNullOrEmpty(fileName) ||
                    (fileName.Length == 2 && fileName[1] == ':') ||
                    fileName.Length > 255)
                {
                    return NativeMethods.SHParseDisplayName(fullPath, IntPtr.Zero, out IntPtr directPidl, 0, out _) == ShellConstants.S_OK
                        ? directPidl
                        : IntPtr.Zero;
                }

                var fd = new WIN32_FIND_DATAW
                {
                    dwFileAttributes = fileAttributes != 0 ? fileAttributes : NativeMethods.FILE_ATTRIBUTE_NORMAL,
                    ftCreationTime = lastWriteTime,
                    ftLastAccessTime = lastWriteTime,
                    ftLastWriteTime = lastWriteTime,
                    nFileSizeHigh = (uint)(fileSize >> 32),
                    nFileSizeLow = (uint)(fileSize & 0xFFFFFFFFUL),
                    dwReserved0 = 0,
                    dwReserved1 = 0,
                    cFileName = fileName,
                    cAlternateFileName = string.Empty
                };

                _fsData.SetData(in fd);

                if (NativeMethods.SHParseDisplayName(fullPath, _bindCtxPtr, out IntPtr pidl, 0, out _) == ShellConstants.S_OK &&
                    pidl != IntPtr.Zero)
                {
                    return pidl;
                }

                return NativeMethods.SHParseDisplayName(fullPath, IntPtr.Zero, out IntPtr fallbackPidl, 0, out _) == ShellConstants.S_OK
                    ? fallbackPidl
                    : IntPtr.Zero;
            }

            public void Dispose()
            {
                if (_disposed)
                    return;

                _disposed = true;
                if (_bindCtx != null)
                {
                    try { _bindCtx.RevokeObjectParam(ShellConstants.STR_FILE_SYS_BIND_DATA); } catch { }
                    if (_bindCtxPtr != IntPtr.Zero)
                    {
                        Marshal.Release(_bindCtxPtr);
                        _bindCtxPtr = IntPtr.Zero;
                    }
                    try { Marshal.ReleaseComObject(_bindCtx); } catch { }
                    _bindCtx = null;
                }
            }
        }
    }
}
