using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
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
        public string? SelectItemName { get; }
        public byte[]? PidlBytes { get; }

        public LaunchTargetItem(string folderOrAlias, string? selectItemName = null, byte[]? pidlBytes = null)
        {
            FolderOrAlias = folderOrAlias ?? string.Empty;
            SelectItemName = string.IsNullOrWhiteSpace(selectItemName) ? null : selectItemName;
            PidlBytes = pidlBytes;
        }
    }

    public sealed class LaunchActionRequest
    {
        public bool IsSelfTest { get; set; }
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

                    req.Targets.Add(new LaunchTargetItem(parent, leaf));
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
        private const string ProtocolHeader = "LEAFTAB_IPC_V1";
        private readonly string _mutexName;
        private readonly string _pipeName;
        private Mutex? _mutex;
        private bool _ownsMutex;
        private CancellationTokenSource? _listenCts;
        private bool _disposed;

        public SingleInstanceService()
        {
            string userKey = $"{Environment.UserDomainName}\\{Environment.UserName}";
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

        public bool TryForwardToPrimary(LaunchActionRequest request, int timeoutMs = 2500)
        {
            // Grant the primary LeafTab process permission to bring itself to the foreground
            NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    int remaining = Math.Max(150, timeoutMs - (int)sw.ElapsedMilliseconds);
                    using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                    client.Connect(Math.Min(remaining, 500));

                    using var writer = new StreamWriter(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                    {
                        AutoFlush = false
                    };

                    writer.WriteLine(ProtocolHeader);
                    if (request.OpenInNewWindow)
                    {
                        writer.WriteLine("OPEN_NEW_WINDOW");
                    }

                    if (request.IsQuitProcess)
                    {
                        writer.WriteLine("QUIT_PROCESS");
                    }
                    else if (request.IsBackgroundWarmup && request.Targets.Count == 0)
                    {
                        writer.WriteLine("BACKGROUND_NOOP");
                    }
                    else if (request.Targets.Count == 0)
                    {
                        writer.WriteLine("ACTIVATE_NEW_TAB");
                    }
                    else
                    {
                        foreach (LaunchTargetItem item in request.Targets)
                        {
                            string b64Folder = Convert.ToBase64String(Encoding.UTF8.GetBytes(item.FolderOrAlias));
                            string b64Select = item.SelectItemName != null
                                ? Convert.ToBase64String(Encoding.UTF8.GetBytes(item.SelectItemName))
                                : string.Empty;
                            writer.WriteLine($"TARGET|{b64Folder}|{b64Select}");
                        }
                    }

                    writer.Flush();
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
                        server = new NamedPipeServerStream(
                            _pipeName,
                            PipeDirection.In,
                            NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous);

                        await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                        NamedPipeServerStream connectedServer = server;
                        server = null;

                        // Read payload concurrently so the next server instance starts listening immediately
                        _ = ReadClientPayloadAsync(connectedServer, onReceived);
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

        private static async Task ReadClientPayloadAsync(NamedPipeServerStream stream, Action<LaunchActionRequest> onReceived)
        {
            try
            {
                using (stream)
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string? header = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (!string.Equals(header, ProtocolHeader, StringComparison.Ordinal))
                        return;

                    var incoming = new LaunchActionRequest();
                    bool ignoreDispatch = false;
                    while (true)
                    {
                        string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line == null)
                            break;

                        if (string.Equals(line, "QUIT_PROCESS", StringComparison.Ordinal))
                        {
                            incoming.IsQuitProcess = true;
                        }
                        else if (string.Equals(line, "OPEN_NEW_WINDOW", StringComparison.Ordinal))
                        {
                            incoming.OpenInNewWindow = true;
                        }
                        else if (string.Equals(line, "BACKGROUND_NOOP", StringComparison.Ordinal))
                        {
                            ignoreDispatch = true;
                        }
                        else if (line.StartsWith("TARGET|", StringComparison.Ordinal))
                        {
                            string[] parts = line.Split('|');
                            if (parts.Length >= 2)
                            {
                                string folder = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
                                string? select = (parts.Length >= 3 && !string.IsNullOrEmpty(parts[2]))
                                    ? Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]))
                                    : null;
                                if (!string.IsNullOrWhiteSpace(folder))
                                {
                                    incoming.Targets.Add(new LaunchTargetItem(folder, select));
                                }
                            }
                        }
                    }

                    if (ignoreDispatch && !incoming.IsQuitProcess && incoming.Targets.Count == 0)
                        return;

                    Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                    {
                        onReceived(incoming);
                    }));
                }
            }
            catch
            {
            }
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
