using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using LeafTab.Shared;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LeafTab.Interop;

namespace LeafTab.Services
{
    public sealed class LaunchTargetItem
    {
        public string FolderOrAlias { get; }
        public string? SelectItemName => SelectItemNames.Count == 0 ? null : SelectItemNames[0];
        public IReadOnlyList<string> SelectItemNames { get; }
        public byte[]? PidlBytes { get; }

        public LaunchTargetItem(string folderOrAlias, string? selectItemName = null, byte[]? pidlBytes = null,
            IEnumerable<string>? selectItemNames = null)
        {
            FolderOrAlias = folderOrAlias ?? string.Empty;
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(selectItemName) && seen.Add(selectItemName!)) names.Add(selectItemName!);
            if (selectItemNames != null)
                foreach (string name in selectItemNames)
                    if (!string.IsNullOrWhiteSpace(name) && seen.Add(name)) names.Add(name);
            SelectItemNames = names.AsReadOnly();
            PidlBytes = pidlBytes;
        }
    }

    public sealed class LaunchActionRequest
    {
        public bool IsSelfTest { get; set; }
        public bool IsUiPreview { get; set; }
        public string? SelfTestReportPath { get; set; }
        public bool IsRegisterDefaultExplorer { get; set; }
        public bool IsRegisterContextMenu { get; set; }
        public bool IsUnregisterShell { get; set; }
        public bool IsQuitProcess { get; set; }
        public bool IsBackgroundWarmup { get; set; }
        public bool IsComServerLaunch { get; set; }
        public bool OpenInNewWindow { get; set; }
        public bool WaitMutexHandoff { get; set; }
        public List<LaunchTargetItem> Targets { get; } = new List<LaunchTargetItem>();

        // Starting infrastructure is not a request to open a user-facing window.
        internal bool IsPassiveStartup => !IsQuitProcess && Targets.Count == 0 &&
                                          (IsBackgroundWarmup || IsComServerLaunch);

        // Multiple /select arguments and V1 IPC target frames for one directory
        // represent one selection, rather than repeatedly clearing earlier items.
        internal void AddOrMergeSelectionTarget(LaunchTargetItem target)
        {
            if (target.SelectItemNames.Count > 0)
            {
                for (int i = 0; i < Targets.Count; i++)
                {
                    LaunchTargetItem existing = Targets[i];
                    if (existing.SelectItemNames.Count == 0 ||
                        !string.Equals(existing.FolderOrAlias, target.FolderOrAlias, StringComparison.OrdinalIgnoreCase) ||
                        !SamePidl(existing.PidlBytes, target.PidlBytes)) continue;
                    var names = new List<string>(existing.SelectItemNames);
                    names.AddRange(target.SelectItemNames);
                    Targets[i] = new LaunchTargetItem(existing.FolderOrAlias, pidlBytes: existing.PidlBytes, selectItemNames: names);
                    return;
                }
            }
            Targets.Add(target);
        }

        private static bool SamePidl(byte[]? first, byte[]? second)
        {
            int length = first?.Length ?? 0;
            if (length != (second?.Length ?? 0)) return false;
            for (int i = 0; i < length; i++) if (first![i] != second![i]) return false;
            return true;
        }
    }

    internal static class LaunchRequestParser
    {
        public static LaunchActionRequest Parse(string[]? args)
        {
            var req = new LaunchActionRequest();
            if (args == null || args.Length == 0)
                return req;

            bool pendingSelectNextArg = false;

            for (int i = 0; i < args.Length; i++)
            {
                string rawArg = args[i] ?? string.Empty;
                if (string.IsNullOrWhiteSpace(rawArg))
                    continue;

                string trimmed = rawArg.Trim();

                if (string.Equals(trimmed, "--self-test-ui", StringComparison.OrdinalIgnoreCase))
                {
                    req.IsSelfTest = true;
                    req.IsUiPreview = true;
                    continue;
                }

                if (string.Equals(trimmed, "--self-test", StringComparison.OrdinalIgnoreCase))
                {
                    req.IsSelfTest = true;
                    req.SelfTestReportPath = (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        ? args[++i]
                        : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest_report.md");
                    continue;
                }

                if (string.Equals(trimmed, "--unregister-shell", StringComparison.OrdinalIgnoreCase))
                {
                    req.IsUnregisterShell = true;
                    continue;
                }

                if (string.Equals(trimmed, "--register-default", StringComparison.OrdinalIgnoreCase))
                {
                    req.IsRegisterDefaultExplorer = true;
                    continue;
                }

                if (string.Equals(trimmed, "--register-context-menu", StringComparison.OrdinalIgnoreCase))
                {
                    req.IsRegisterContextMenu = true;
                    continue;
                }

                if (string.Equals(trimmed, "--quit", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(trimmed, "--exit", StringComparison.OrdinalIgnoreCase))
                {
                    req.IsQuitProcess = true;
                    continue;
                }

                if (string.Equals(trimmed, "--background", StringComparison.OrdinalIgnoreCase))
                {
                    req.IsBackgroundWarmup = true;
                    continue;
                }

                if (string.Equals(trimmed, "--new-window", StringComparison.OrdinalIgnoreCase))
                {
                    req.OpenInNewWindow = true;
                    continue;
                }

                if (string.Equals(trimmed, "--handoff", StringComparison.OrdinalIgnoreCase))
                {
                    req.WaitMutexHandoff = true;
                    continue;
                }

                if (string.Equals(trimmed, "--com-server", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(trimmed, "-embedding", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(trimmed, "/embedding", StringComparison.OrdinalIgnoreCase))
                {
                    req.IsComServerLaunch = true;
                    continue;
                }

                if (trimmed.StartsWith("--", StringComparison.Ordinal))
                    continue;

                bool selectThisItem = pendingSelectNextArg;
                pendingSelectNextArg = false;

                string working = trimmed;

                // Peel off standard Explorer command-line switches (/n, /e, /root, /select) without breaking commas inside filenames
                bool skipWholeToken = false;
                while (working.StartsWith("/", StringComparison.Ordinal) && !working.StartsWith("//", StringComparison.Ordinal))
                {
                    if (string.Equals(working, "/n", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(working, "/e", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(working, "/root", StringComparison.OrdinalIgnoreCase))
                    {
                        skipWholeToken = true;
                        break;
                    }

                    if (string.Equals(working, "/select", StringComparison.OrdinalIgnoreCase))
                    {
                        pendingSelectNextArg = true;
                        skipWholeToken = true;
                        break;
                    }

                    if (working.StartsWith("/n,", StringComparison.OrdinalIgnoreCase))
                    {
                        working = working.Substring(3).TrimStart();
                        continue;
                    }

                    if (working.StartsWith("/e,", StringComparison.OrdinalIgnoreCase))
                    {
                        working = working.Substring(3).TrimStart();
                        continue;
                    }

                    if (working.StartsWith("/root,", StringComparison.OrdinalIgnoreCase))
                    {
                        working = working.Substring(6).TrimStart();
                        continue;
                    }

                    if (working.StartsWith("/select,", StringComparison.OrdinalIgnoreCase))
                    {
                        selectThisItem = true;
                        working = working.Substring(8).TrimStart();
                        if (working.Length == 0) pendingSelectNextArg = true;
                        continue;
                    }

                    break;
                }

                if (skipWholeToken)
                    continue;

                string cleaned = NormalizePathToken(working);
                if (string.IsNullOrWhiteSpace(cleaned))
                    continue;

                if (selectThisItem)
                {
                    AddSelectTarget(req, cleaned);
                }
                else if (PidlManager.IsExplorerLauncherTarget(cleaned))
                {
                    req.Targets.Add(new LaunchTargetItem("此电脑"));
                }
                else if (!cleaned.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                         !Directory.Exists(cleaned) &&
                         File.Exists(cleaned))
                {
                    // Non-archive file passed directly: open its parent directory and select the file
                    AddSelectTarget(req, cleaned);
                }
                else
                {
                    req.Targets.Add(new LaunchTargetItem(cleaned));
                }
            }

            return req;
        }

        internal static string NormalizePathToken(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return string.Empty;

            string s = raw.Trim();

            // Strip any stray quotes caused by Windows "%V" trailing-backslash quote escaping (e.g. "D:\" -> D:")
            while (s.Length > 0 && s[0] == '"')
                s = s.Substring(1);
            while (s.Length > 0 && s[s.Length - 1] == '"')
                s = s.Substring(0, s.Length - 1);

            s = s.Trim();

            // Normalize bare drive designator "D:" -> "D:\"
            if (s.Length == 2 && char.IsLetter(s[0]) && s[1] == ':')
            {
                return s + "\\";
            }

            return s;
        }

        private static void AddSelectTarget(LaunchActionRequest req, string itemPath)
        {
            string normalized = itemPath;
            bool isDriveRoot = normalized.Length == 3 &&
                               char.IsLetter(normalized[0]) &&
                               normalized[1] == ':' &&
                               (normalized[2] == '\\' || normalized[2] == '/');

            if (!isDriveRoot)
            {
                normalized = normalized.TrimEnd('\\', '/');
            }

            try
            {
                string? parent = Path.GetDirectoryName(normalized);
                string? leaf = Path.GetFileName(normalized);
                if (!string.IsNullOrWhiteSpace(parent) && !string.IsNullOrWhiteSpace(leaf))
                {
                    if (parent!.Length == 2 && char.IsLetter(parent[0]) && parent[1] == ':')
                        parent += "\\";

                    req.AddOrMergeSelectionTarget(new LaunchTargetItem(parent, leaf));
                    return;
                }
            }
            catch
            {
            }

            req.Targets.Add(new LaunchTargetItem(itemPath));
        }
    }

    internal sealed class SingleInstanceService : IDisposable
    {

        private readonly string _mutexName;
        private readonly string _pipeName;
        private Mutex? _mutex;
        private bool _ownsMutex;
        private CancellationTokenSource? _listenCts;
        private bool _disposed;

        public SingleInstanceService() : this(WindowsIdentity.GetCurrent().User!.Value + ":" + System.Diagnostics.Process.GetCurrentProcess().SessionId)
        {
        }

        internal SingleInstanceService(string userKey)
        {
            string hashHex = ComputeShortSha256Hex(userKey);
            _mutexName = $"Local\\LeafTab.SingleInstance.{hashHex}";
            _pipeName = $"LeafTab.SingleInstance.Pipe.{hashHex}";
        }

        public bool TryAcquirePrimaryInstance(int waitMs = 0)
        {
            try
            {
                _mutex = new Mutex(initiallyOwned: true, _mutexName, out bool createdNew);
                if (createdNew)
                {
                    _ownsMutex = true;
                    return true;
                }

                try
                {
                    if (_mutex.WaitOne(Math.Max(0, waitMs)))
                    {
                        _ownsMutex = true;
                        return true;
                    }
                }
                catch (AbandonedMutexException)
                {
                    _ownsMutex = true;
                    return true;
                }

                return false;
            }
            catch
            {
                return true;
            }
        }

        public bool TryForwardToPrimary(LaunchActionRequest request, int timeoutMs = 2500, string? expectedExecutablePath = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    int remaining = Math.Max(150, timeoutMs - (int)sw.ElapsedMilliseconds);
                    using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                    client.Connect(Math.Min(remaining, 500));

                    if (expectedExecutablePath != null)
                    {
                        if (!NativeMethods.GetNamedPipeServerProcessId(client.SafePipeHandle, out uint serverPid)) return false;
                        string? serverExe = NativeMethods.TryGetProcessImagePath(serverPid);
                        if (!ShellAssociationRecovery.PathsEqual(serverExe, expectedExecutablePath)) return false;
                    }

                    // Only an actual open request may grant foreground rights, and only to its receiver.
                    if (!request.IsPassiveStartup && !request.IsQuitProcess &&
                        NativeMethods.GetNamedPipeServerProcessId(client.SafePipeHandle, out uint foregroundPid))
                    {
                        NativeMethods.AllowSetForegroundWindow(unchecked((int)foregroundPid));
                    }

                    byte[] payload = LaunchRequestCodec.Encode(request);
                    Task write = client.WriteAsync(payload, 0, payload.Length);
                    if (!write.Wait(remaining)) return false;
                    return true;
                }
                catch
                {
                    Thread.Sleep(40);
                }
            }

            return false;
        }

        public void StartListening(Action<LaunchActionRequest> onReceived)
        {
            if (_listenCts != null)
                return;

            _listenCts = new CancellationTokenSource();
            CancellationToken token = _listenCts.Token;

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    NamedPipeServerStream? server = null;
                    try
                    {
                        server = CreatePipeServerStream(_pipeName);

                        await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                        NamedPipeServerStream connectedServer = server;
                        server = null;

                        // Read payload concurrently so the next server instance starts listening immediately
                        _ = ReadClientPayloadAsync(connectedServer, onReceived, token);
                    }
                    catch (OperationCanceledException)
                    {
                        server?.Dispose();
                        break;
                    }
                    catch
                    {
                        server?.Dispose();
                        if (token.IsCancellationRequested)
                            break;

                        try { await Task.Delay(60, token).ConfigureAwait(false); }
                        catch { break; }
                    }
                }
            }, token);
        }

        internal static NamedPipeServerStream CreatePipeServerStream(string pipeName)
        {
            string userSid = WindowsIdentity.GetCurrent().User!.Value;
            Microsoft.Win32.SafeHandles.SafePipeHandle? nativeHandle =
                NativeMethods.TryCreateCurrentUserMediumIntegrityInboundPipe(pipeName, userSid);
            if (nativeHandle != null)
            {
                try
                {
                    return new NamedPipeServerStream(PipeDirection.In, isAsync: true, isConnected: false, safePipeHandle: nativeHandle);
                }
                catch
                {
                    nativeHandle.Dispose();
                }
            }

            return new NamedPipeServerStream(
                pipeName,
                PipeDirection.In,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                4096,
                4096,
                CreateCurrentUserPipeSecurity());
        }

        internal static PipeSecurity CreateCurrentUserPipeSecurity()
        {
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
            return security;
        }

        private static async Task ReadClientPayloadAsync(NamedPipeServerStream stream, Action<LaunchActionRequest> onReceived, CancellationToken token)
        {
            try
            {
                using (stream)
                {
                    byte[] payload = await LaunchRequestCodec.ReadBoundedAsync(stream, 3000, token).ConfigureAwait(false);
                    var incoming = LaunchRequestCodec.Decode(payload);
                    if (incoming != null && !token.IsCancellationRequested)
                    {
                        DiagnosticLog.Write(incoming.IsQuitProcess ? "ipc-quit-request" : "ipc-open-request");
                        Application.Current?.Dispatcher?.BeginInvoke(new Action(() => onReceived(incoming)));
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { DiagnosticLog.Write("ipc-request-rejected", ex); }
        }

        private static string ComputeShortSha256Hex(string input)
        {
            using var sha = SHA256.Create();
            byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input.ToUpperInvariant()));
            var sb = new StringBuilder(16);
            for (int i = 0; i < 8 && i < bytes.Length; i++)
            {
                sb.Append(bytes[i].ToString("x2"));
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            try
            {
                _listenCts?.Cancel();
                _listenCts?.Dispose();
            }
            catch { }

            if (_mutex != null)
            {
                try
                {
                    if (_ownsMutex)
                        _mutex.ReleaseMutex();
                }
                catch { }

                try
                {
                    _mutex.Dispose();
                }
                catch { }

                _mutex = null;
            }
        }
    }
}
