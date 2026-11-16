using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LeafTab.Interop;
using LeafTab.Shared;

namespace LeafTab.Services
{
    /// <summary>
    /// Adopts newly opened, ordinary Explorer windows that bypass shell verbs (notably
    /// explorer.exe /select). The hook is out of process; no DLL is injected. A dedicated
    /// STA keeps Shell automation calls off the UI thread. Existing windows are never adopted.
    /// </summary>
    internal sealed class ExplorerWindowRedirectService : IDisposable
    {
        private const uint EventObjectDestroy = 0x8001;
        private const uint EventObjectShow = 0x8002;
        private const int MaxPendingWindows = 32;
        private const int DiscoveryTimeoutMs = 20000;
        private readonly Func<LaunchActionRequest, Task<bool>> _onRedirect;
        private readonly Func<bool> _isEnabled;
        private readonly HashSet<IntPtr> _excluded = new HashSet<IntPtr>();
        private readonly Dictionary<IntPtr, Candidate> _pending = new Dictionary<IntPtr, Candidate>();
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim();
        private readonly WinEventDelegate _eventCallback;
        private Thread? _thread;
        private Dispatcher? _dispatcher;
        private DispatcherTimer? _timer;
        private IntPtr _hook;
        private GCHandle _callbackRoot;
        private volatile bool _disposed;
        private volatile bool _started;

        internal ExplorerWindowRedirectService(Func<LaunchActionRequest, Task<bool>> onRedirect, Func<bool> isEnabled)
        {
            _onRedirect = onRedirect ?? throw new ArgumentNullException(nameof(onRedirect));
            _isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
            _eventCallback = OnWinEvent;
        }

        internal bool IsRunning => _started && !_disposed;

        internal bool Start()
        {
            if (_disposed) return false;
            if (_thread != null) return _started;
            _thread = new Thread(Run) { IsBackground = true, Name = "LeafTab Explorer redirect" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            if (!_ready.Wait(2000))
            {
                DiagnosticLog.Write("explorer-redirect-start-timeout");
                Dispose();
            }
            return IsRunning;
        }

        private void Run()
        {
            try
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(_dispatcher));
                _callbackRoot = GCHandle.Alloc(_eventCallback);
                _hook = SetWinEventHook(EventObjectDestroy, EventObjectShow, IntPtr.Zero, _eventCallback, 0, 0, 2);
                if (_hook == IntPtr.Zero)
                {
                    DiagnosticLog.Write("explorer-redirect-hook-failed");
                    return;
                }
                // Capture baseline after installing the hook, before dispatching queued events.
                // This intentionally excludes windows already being created during startup too.
                EnumWindows((hwnd, _) => { if (IsExplorerWindowClass(hwnd)) _excluded.Add(hwnd); return true; }, IntPtr.Zero);
                _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(150)
                };
                _timer.Tick += OnTick;
                _started = true;
                _ready.Set();
                if (!_disposed) Dispatcher.Run();
            }
            catch (Exception ex) { DiagnosticLog.Write("explorer-redirect-service-failed", ex); }
            finally
            {
                _started = false;
                if (_timer != null) { _timer.Stop(); _timer.Tick -= OnTick; }
                if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
                if (_callbackRoot.IsAllocated) _callbackRoot.Free();
                _pending.Clear();
                _excluded.Clear();
                _ready.Set();
            }
        }

        private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint threadId, uint time)
        {
            try
            {
                if (_disposed || hwnd == IntPtr.Zero || objectId != 0 || childId != 0) return;
                if (eventType == EventObjectDestroy)
                {
                    _excluded.Remove(hwnd);
                    _pending.Remove(hwnd);
                    StopTimerWhenIdle();
                    return;
                }
                if (_excluded.Contains(hwnd) || _pending.ContainsKey(hwnd) || !IsExplorerWindowClass(hwnd) ||
                    GetAncestor(hwnd, 2) != hwnd || GetWindow(hwnd, 4) != IntPtr.Zero) return;
                _excluded.Add(hwnd); // Never retry a completed/ignored window after later user activity.
                if (!_isEnabled() || _pending.Count >= MaxPendingWindows || !TryGetExplorerProcess(hwnd, out uint pid)) return;
                _pending.Add(hwnd, new Candidate(hwnd, pid));
                DiagnosticLog.Write("explorer-redirect-candidate");
                _timer?.Start();
            }
            catch (Exception ex) { DiagnosticLog.Write("explorer-redirect-event-failed", ex); }
        }

        private void OnTick(object? sender, EventArgs args)
        {
            if (_disposed) return;
            bool enabled;
            try { enabled = _isEnabled(); }
            catch { enabled = false; }
            foreach (Candidate candidate in _pending.Values.ToArray())
            {
                if (candidate.Routing) continue;
                if (!enabled || !IsWindow(candidate.Hwnd) || candidate.Age.ElapsedMilliseconds > DiscoveryTimeoutMs)
                {
                    if (enabled && IsWindow(candidate.Hwnd)) DiagnosticLog.Write(candidate.WaitedForForeground
                        ? "explorer-redirect-background-preserved" : "explorer-redirect-expired");
                    _pending.Remove(candidate.Hwnd);
                    continue;
                }
                if (IsIconic(candidate.Hwnd))
                {
                    _pending.Remove(candidate.Hwnd);
                    DiagnosticLog.Write("explorer-redirect-minimized-preserved");
                    continue;
                }
                if (candidate.Age.ElapsedMilliseconds < 450 || !IsWindowVisible(candidate.Hwnd)) continue;
                object? browser = null;
                try
                {
                    browser = FindSingleBrowser(candidate.Hwnd);
                    if (browser == null) continue;
                    ExplorerRedirectSnapshot? snapshot = ReadSnapshot(browser);
                    if (snapshot == null) continue;
                    if (candidate.Snapshot == null || !candidate.Snapshot.SameLocationAndSelection(snapshot))
                    {
                        candidate.Snapshot = snapshot;
                        candidate.StableSince = candidate.Age.ElapsedMilliseconds;
                        continue;
                    }
                    // Shell sets selection after displaying the window. Wait for a stable result,
                    // with extra settling time for a plain open that initially has no selection.
                    long stableFor = candidate.Age.ElapsedMilliseconds - candidate.StableSince;
                    int settle = snapshot.SelectedItemNames.Count == 0 ? 650 : 300;
                    if (stableFor < settle) continue;
                    if (GetForegroundWindow() != candidate.Hwnd)
                    {
                        // Explorer may not receive activation until after its initial show.
                        // Wait within the original deadline; never activate it ourselves.
                        candidate.WaitedForForeground = true;
                        continue;
                    }
                    candidate.Routing = true;
                    object ownedBrowser = browser;
                    browser = null;
                    _ = RedirectAsync(candidate, ownedBrowser, snapshot);
                }
                catch (Exception ex) { DiagnosticLog.Write("explorer-redirect-read-failed", ex); }
                finally { ReleaseCom(browser); }
            }
            StopTimerWhenIdle();
        }

        private async Task RedirectAsync(Candidate candidate, object browser, ExplorerRedirectSnapshot snapshot)
        {
            try
            {
                var request = new LaunchActionRequest();
                request.Targets.Add(new LaunchTargetItem(snapshot.FolderPath, selectItemNames: snapshot.SelectedItemNames));
                bool delivered = await _onRedirect(request);
                // A failed target navigation or selection always leaves the original available.
                if (!delivered) { DiagnosticLog.Write("explorer-redirect-delivery-rejected"); return; }
                if (_disposed || !_isEnabled() || !IsWindow(candidate.Hwnd) ||
                    !TryGetExplorerProcess(candidate.Hwnd, out uint pid) || pid != candidate.ProcessId) return;
                ExplorerRedirectSnapshot? current = ReadSnapshot(browser);
                if (current == null || !snapshot.SameLocationAndSelection(current))
                {
                    DiagnosticLog.Write("explorer-redirect-source-changed");
                    return;
                }
                object? stillSingle = FindSingleBrowser(candidate.Hwnd);
                if (stillSingle == null) return;
                ReleaseCom(stillSingle);
                Call(browser, "Quit");
                DiagnosticLog.Write("explorer-window-redirected");
            }
            catch (Exception ex) { DiagnosticLog.Write("explorer-redirect-delivery-failed", ex); }
            finally
            {
                ReleaseCom(browser);
                _pending.Remove(candidate.Hwnd);
                StopTimerWhenIdle();
            }
        }

        private void StopTimerWhenIdle()
        {
            if (_pending.Count == 0 || _pending.Values.All(c => c.Routing)) _timer?.Stop();
        }

        // A Windows 11 window can expose several browser tabs sharing one HWND. Never Quit
        // such a window: doing so could close unrelated tabs that the user added meanwhile.
        private static object? FindSingleBrowser(IntPtr hwnd)
        {
            object? windows = null;
            object? found = null;
            try
            {
                Type? type = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"));
                if (type == null) return null;
                windows = Activator.CreateInstance(type);
                if (windows == null) return null;
                int count = Convert.ToInt32(Get(windows, "Count"), CultureInfo.InvariantCulture);
                for (int i = 0; i < count; i++)
                {
                    object? item = null;
                    try
                    {
                        item = Call(windows, "Item", i);
                        if (item == null || Convert.ToInt64(Get(item, "HWND"), CultureInfo.InvariantCulture) != hwnd.ToInt64()) continue;
                        if (found != null) { ReleaseCom(found); found = null; return null; }
                        found = item;
                        item = null;
                    }
                    catch (COMException) { }
                    catch (TargetInvocationException) { }
                    finally { ReleaseCom(item); }
                }
                object? result = found;
                found = null;
                return result;
            }
            finally { ReleaseCom(found); ReleaseCom(windows); }
        }

        private static ExplorerRedirectSnapshot? ReadSnapshot(object browser)
        {
            object? document = null, folder = null, self = null, selected = null;
            try
            {
                if (Convert.ToBoolean(Get(browser, "Busy"), CultureInfo.InvariantCulture)) return null;
                document = Get(browser, "Document");
                if (document == null) return null;
                folder = Get(document, "Folder");
                if (folder == null) return null;
                self = Get(folder, "Self");
                if (self == null || !Convert.ToBoolean(Get(self, "IsFileSystem"), CultureInfo.InvariantCulture) ||
                    !Convert.ToBoolean(Get(self, "IsFolder"), CultureInfo.InvariantCulture)) return null;
                string? path = Get(self, "Path") as string;
                selected = Call(document, "SelectedItems");
                if (selected == null) return null;
                int count = Convert.ToInt32(Get(selected, "Count"), CultureInfo.InvariantCulture);
                if (count < 0 || count > 128) return null;
                var selectedPaths = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    object? item = null;
                    try
                    {
                        item = Call(selected, "Item", i);
                        if (item == null || !Convert.ToBoolean(Get(item, "IsFileSystem"), CultureInfo.InvariantCulture)) return null;
                        string? selectedPath = Get(item, "Path") as string;
                        if (selectedPath == null) return null;
                        selectedPaths.Add(selectedPath);
                    }
                    finally { ReleaseCom(item); }
                }
                return ExplorerRedirectSnapshot.TryCreate(path, selectedPaths);
            }
            finally { ReleaseCom(selected); ReleaseCom(self); ReleaseCom(folder); ReleaseCom(document); }
        }

        private static object? Get(object target, string name) => target.GetType().InvokeMember(
            name, BindingFlags.GetProperty, null, target, null, CultureInfo.InvariantCulture);

        private static object? Call(object target, string name, params object[] arguments) => target.GetType().InvokeMember(
            name, BindingFlags.InvokeMethod, null, target, arguments, CultureInfo.InvariantCulture);

        private static void ReleaseCom(object? value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                try { Marshal.ReleaseComObject(value); } catch (InvalidComObjectException) { }
            }
        }

        private static bool TryGetExplorerProcess(IntPtr hwnd, out uint pid)
        {
            GetWindowThreadProcessId(hwnd, out pid);
            string? image = NativeMethods.TryGetProcessImagePath(pid);
            return image != null && string.Equals(image,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsExplorerWindowClass(IntPtr hwnd)
        {
            var text = new StringBuilder(64);
            if (GetClassNameW(hwnd, text, text.Capacity) == 0) return false;
            string name = text.ToString();
            return name == "CabinetWClass" || name == "ExploreWClass";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Dispatcher? dispatcher = _dispatcher;
            if (dispatcher != null && !dispatcher.HasShutdownStarted)
            {
                try { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
                catch (InvalidOperationException) { }
            }
            // Never join the worker from the UI: an in-flight navigation callback may need it.
        }

        private sealed class Candidate
        {
            internal readonly IntPtr Hwnd;
            internal readonly uint ProcessId;
            internal readonly Stopwatch Age = Stopwatch.StartNew();
            internal ExplorerRedirectSnapshot? Snapshot;
            internal long StableSince;
            internal bool Routing;
            internal bool WaitedForForeground;
            internal Candidate(IntPtr hwnd, uint processId) { Hwnd = hwnd; ProcessId = processId; }
        }

        private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint threadId, uint time);
        private delegate bool EnumWindowsDelegate(IntPtr hwnd, IntPtr parameter);
        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventDelegate callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsDelegate callback, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder text, int count);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    }

    internal sealed class ExplorerRedirectSnapshot
    {
        internal string FolderPath { get; }
        internal IReadOnlyList<string> SelectedItemNames { get; }
        private ExplorerRedirectSnapshot(string folder, string[] names) { FolderPath = folder; SelectedItemNames = names; }

        internal static ExplorerRedirectSnapshot? TryCreate(string? folder, IEnumerable<string> selectedPaths)
        {
            try
            {
                if (!IsFileSystemPath(folder)) return null;
                string normalizedFolder = Normalize(folder!);
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in selectedPaths)
                {
                    if (!IsFileSystemPath(path)) return null;
                    string fullPath = Path.GetFullPath(path);
                    string? parent = Path.GetDirectoryName(fullPath.TrimEnd('\\'));
                    string name = Path.GetFileName(fullPath.TrimEnd('\\'));
                    if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name) ||
                        !string.Equals(Normalize(parent), normalizedFolder, StringComparison.OrdinalIgnoreCase)) return null;
                    names.Add(name);
                    if (names.Count > 128) return null;
                }
                return new ExplorerRedirectSnapshot(normalizedFolder, names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray());
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return null; }
        }

        internal bool SameLocationAndSelection(ExplorerRedirectSnapshot other) =>
            string.Equals(FolderPath, other.FolderPath, StringComparison.OrdinalIgnoreCase) &&
            SelectedItemNames.SequenceEqual(other.SelectedItemNames, StringComparer.OrdinalIgnoreCase);

        private static string Normalize(string path)
        {
            string full = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(full);
            return string.Equals(full, root, StringComparison.OrdinalIgnoreCase) ? full : full.TrimEnd('\\');
        }

        private static bool IsFileSystemPath(string? path) => !string.IsNullOrWhiteSpace(path) &&
            ((path!.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] == '\\') ||
             (path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal) &&
              !path.StartsWith(@"\\.\", StringComparison.Ordinal)));
    }
}
