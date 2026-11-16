using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using LeafTab.Diagnostics;
using LeafTab.Interop;
using LeafTab.Models;
using LeafTab.Services;
using LeafTab.Shared;
using LeafTab.Views;

namespace LeafTab
{
    public partial class App : Application
    {
        private static readonly uint s_currentPid = (uint)Process.GetCurrentProcess().Id;

        private readonly List<MainWindow> _windows = new List<MainWindow>();
        private MainWindow? _lastActiveWindow;
        private SingleInstanceService? _singleInstance;
        private ComShellExecuteServer? _comServer;
        private bool _isSelfTest;
        private bool _isShuttingDownAll;
        private DispatcherTimer? _comRelayIdleTimer;
        private long _lastComActivityTimestamp;
        private bool _hasHandledExternalOpen;

        internal static App? CurrentApp => Current as App;

        internal IReadOnlyList<MainWindow> ManagedWindows => _windows;

        public App()
        {
            try
            {
                string jitDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LeafTab");
                Directory.CreateDirectory(jitDir);
                ProfileOptimization.SetProfileRoot(jitDir);
                ProfileOptimization.StartProfile("startup.jitprofile");
            }
            catch
            {
            }
        }

        internal static string DiagnosticLogDirectoryPath
        {
            get => DiagnosticLog.DirectoryPath;
            set => DiagnosticLog.DirectoryPath = value;
        }

        internal static bool HandleDispatcherUnhandledExceptionCore(
            Exception exception,
            bool isSelfTest,
            int visibleWindowsCount,
            Action? resetHiddenStandbyHosts = null)
        {
            DiagnosticLog.Write("dispatcher-unhandled", exception);
            if (isSelfTest || visibleWindowsCount <= 0)
            {
                try { resetHiddenStandbyHosts?.Invoke(); } catch { }
                return true; // suppressed modal dialog
            }
            return false; // caller should display user-visible warning dialog
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                DiagnosticLog.Write("appdomain-unhandled", args.ExceptionObject as Exception);
            };

            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                DiagnosticLog.Write("task-unobserved", args.Exception);
                args.SetObserved();
            };

            DispatcherUnhandledException += (_, args) =>
            {
                bool suppressed = HandleDispatcherUnhandledExceptionCore(
                    args.Exception,
                    _isSelfTest,
                    GetVisibleWindowsCount(),
                    () =>
                    {
                        foreach (MainWindow w in _windows.ToArray())
                        {
                            if (!w.IsVisible)
                            {
                                try { w.DiscardPrewarmedHost(); } catch { }
                            }
                        }
                    });

                if (!suppressed)
                {
                    MessageBox.Show(
                        $"LeafTab 遇到未处理异常：\n{args.Exception.Message}\n\n已记录诊断日志至：\n{DiagnosticLog.DirectoryPath}",
                        "LeafTab",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                args.Handled = true;
            };

            LaunchActionRequest req = LaunchRequestParser.Parse(e.Args);

            if (req.IsUnregisterShell)
            {
                bool restored = ShellIntegrationService.UnregisterAllOwned();
                if (restored)
                {
                    using var quitClient = new SingleInstanceService();
                    if (!quitClient.TryAcquirePrimaryInstance())
                    {
                        quitClient.TryForwardToPrimary(new LaunchActionRequest { IsQuitProcess = true }, timeoutMs: 1500,
                            expectedExecutablePath: ShellIntegrationService.GetCurrentExecutablePath());
                    }
                }
                Environment.Exit(restored ? 0 : 1);
                return;
            }

            if (req.IsRegisterDefaultExplorer || req.IsRegisterContextMenu)
            {
                bool ok = true;
                if (req.IsRegisterDefaultExplorer)
                {
                    ok &= ShellIntegrationService.SetDefaultExplorerEnabled(true).Ok;
                }
                if (req.IsRegisterContextMenu)
                {
                    ok &= ShellIntegrationService.SetContextMenuEnabled(true).Ok;
                }
                Environment.Exit(ok ? 0 : 1);
                return;
            }

            if (req.IsQuitProcess)
            {
                using var quitClient = new SingleInstanceService();
                if (!quitClient.TryAcquirePrimaryInstance())
                {
                    quitClient.TryForwardToPrimary(req, timeoutMs: 1500);
                }
                Environment.Exit(0);
                return;
            }

            if (req.IsBackgroundWarmup && req.Targets.Count == 0 && !AppConfigService.Load().EnableWarmStandby)
            {
                Environment.Exit(0);
                return;
            }

            _isSelfTest = req.IsSelfTest;
            var earlyBufferedRequests = new List<LaunchActionRequest>();
            bool initialWindowReady = false;

            Action<LaunchActionRequest> routeIncomingRequest = incoming =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (incoming.IsPassiveStartup)
                        return;
                    _comRelayIdleTimer?.Stop();
                    if (incoming.IsQuitProcess)
                    {
                        ForceExitAllWindows();
                        return;
                    }

                    _hasHandledExternalOpen = true;
                    if (initialWindowReady)
                    {
                        _ = RouteExternalLaunchRequestAsync(incoming);
                    }
                    else
                    {
                        earlyBufferedRequests.Add(incoming);
                    }
                }));
            };

            if (!_isSelfTest)
            {
                _singleInstance = new SingleInstanceService();
                int acquireWaitMs = req.WaitMutexHandoff ? 2000 : 0;
                if (!_singleInstance.TryAcquirePrimaryInstance(acquireWaitMs))
                {
                    if (req.IsComServerLaunch && req.Targets.Count == 0 && !req.IsQuitProcess)
                    {
                        StartHiddenComRelay();
                        return;
                    }

                    bool forwarded = _singleInstance.TryForwardToPrimary(req, timeoutMs: 2500);
                    _singleInstance.Dispose();
                    _singleInstance = null;
                    if (forwarded || req.IsBackgroundWarmup)
                    {
                        Environment.Exit(0);
                        return;
                    }
                }
                else
                {
                    _singleInstance.StartListening(routeIncomingRequest);
                    _comServer = new ComShellExecuteServer(routeIncomingRequest, TouchComActivity);
                    _comServer.Start();
                }
            }

            SessionEnding += (_, _) =>
            {
                MainWindow[] snapshot = _windows.ToArray();
                Views.MainWindow.SaveUnifiedSessionBeforeExit(snapshot, GetPreferredTargetWindow());
                foreach (MainWindow w in snapshot)
                {
                    w.PrepareForForceExit(skipPerWindowSave: true);
                }
            };

            bool isSilentWarmup = !_isSelfTest && req.IsPassiveStartup;
            if (isSilentWarmup)
                DiagnosticLog.Write(req.IsComServerLaunch ? "startup-com-hidden" : "startup-background-hidden");
            bool hasRestorableTabs = !_isSelfTest &&
                                     !req.IsBackgroundWarmup &&
                                     req.Targets.Count == 0 &&
                                     !req.IsComServerLaunch &&
                                     AppConfigStore.Current.Settings.RestorePreviousTabs &&
                                     AppConfigStore.Current.Settings.OpenTabs.Count > 0;
            bool deferFallbackTab = !_isSelfTest &&
                                    (isSilentWarmup || hasRestorableTabs || req.Targets.Count > 0 || req.IsComServerLaunch || earlyBufferedRequests.Count > 0);

            var mainWindow = new MainWindow(
                skipSessionPersistence: _isSelfTest,
                deferInitialFallbackTab: deferFallbackTab);
            RegisterWindow(mainWindow);
            MainWindow = mainWindow;
            if (req.IsUiPreview)
            {
                mainWindow.Title = "LeafTab · 验证副本";
                mainWindow.Width = 1160;
                mainWindow.Height = 760;
            }

            if (isSilentWarmup && earlyBufferedRequests.Count == 0)
            {
                new WindowInteropHelper(mainWindow).EnsureHandle();
                initialWindowReady = true;

                while (earlyBufferedRequests.Count > 0)
                {
                    var batch = earlyBufferedRequests.ToArray();
                    earlyBufferedRequests.Clear();
                    foreach (LaunchActionRequest buffered in batch)
                    {
                        if (buffered.IsQuitProcess)
                        {
                            ForceExitAllWindows();
                            return;
                        }
                        await RouteExternalLaunchRequestAsync(buffered);
                    }
                }

                // Start the idle exit timer ONLY AFTER draining any requests that arrived during EnsureHandle(),
                // and ONLY if this COM server remained purely passive with zero visible windows.
                if (req.IsComServerLaunch && !_hasHandledExternalOpen && GetVisibleWindowsCount() == 0)
                {
                    StartComIdleTimer(isPrimaryInstance: true);
                }
            }
            else
            {
                mainWindow.Show();

                if (!_isSelfTest)
                {
                    IntPtr mainHwnd = new WindowInteropHelper(mainWindow).Handle;
                    if (mainHwnd != IntPtr.Zero && !NativeMethods.IsWindowVisible(mainHwnd))
                    {
                        NativeMethods.ShowWindow(
                            mainHwnd,
                            mainWindow.WindowState == WindowState.Maximized
                                ? NativeMethods.SW_SHOWMAXIMIZED
                                : NativeMethods.SW_SHOW);
                    }
                }

                await mainWindow.WaitUntilLoadedAsync();

                if (req.Targets.Count > 0)
                {
                    await mainWindow.HandleExternalLaunchRequestAsync(req, isInitialLaunch: true);
                }
                while (earlyBufferedRequests.Count > 0)
                {
                    var batch = earlyBufferedRequests.ToArray();
                    earlyBufferedRequests.Clear();
                    foreach (LaunchActionRequest buffered in batch)
                    {
                        if (buffered.IsQuitProcess)
                        {
                            ForceExitAllWindows();
                            return;
                        }
                        await RouteExternalLaunchRequestAsync(buffered);
                    }
                }

                if (hasRestorableTabs && mainWindow.Tabs.Count == 0 && req.Targets.Count == 0 && earlyBufferedRequests.Count == 0)
                {
                    await mainWindow.TryRestoreSavedTabsIfEnabledAsync();
                }

                mainWindow.EnsureAtLeastOneTabOpen(markAsAutoFallback: true);
                initialWindowReady = true;
            }

            if (req.IsUiPreview)
            {
                string? interactiveLogPath = Environment.GetEnvironmentVariable("LEAFTAB_UI_TEST_LOG");
                if (!string.IsNullOrWhiteSpace(interactiveLogPath))
                    InteractiveFocusRecorder.Attach(mainWindow, interactiveLogPath!);
            }

            if (!_isSelfTest)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(800).ConfigureAwait(false);
                    ShellIntegrationService.RepairRegisteredPathsIfMoved();
                });
            }

            if (_isSelfTest && !req.IsUiPreview)
            {
                await SelfTestRunner.RunAndExitAsync(mainWindow, req.SelfTestReportPath!);
            }
        }

        // ==================== MULTI-WINDOW LIFECYCLE & ROUTING ====================

        internal void RegisterWindow(MainWindow win)
        {
            if (_windows.Contains(win))
                return;

            _windows.Add(win);
            if (_lastActiveWindow == null)
            {
                _lastActiveWindow = win;
            }

            win.Activated += (_, _) =>
            {
                _lastActiveWindow = win;
                MainWindow = win;
            };

            win.Closed += (_, _) =>
            {
                _windows.Remove(win);
                if (ReferenceEquals(_lastActiveWindow, win))
                {
                    _lastActiveWindow = _windows.Count > 0 ? _windows[_windows.Count - 1] : null;
                }
                if (ReferenceEquals(MainWindow, win))
                {
                    MainWindow = _lastActiveWindow;
                }

                if (_windows.Count == 0 && !_isShuttingDownAll)
                {
                    Shutdown();
                }
            };
        }

        internal int GetVisibleWindowsCount()
        {
            int count = 0;
            foreach (MainWindow w in _windows)
            {
                if (w.IsVisible && !w.IsClosingOrClosed)
                    count++;
            }
            return count;
        }

        internal bool ShouldWindowEnterWarmStandby(MainWindow closingWindow)
        {
            if (_isSelfTest || _isShuttingDownAll)
                return false;

            // Only the LAST remaining visible window enters Warm Standby; secondary windows close & dispose cleanly!
            int otherVisible = 0;
            foreach (MainWindow w in _windows)
            {
                if (!ReferenceEquals(w, closingWindow) && w.IsVisible && !w.IsClosingOrClosed)
                {
                    otherVisible++;
                }
            }
            return otherVisible == 0;
        }

        internal MainWindow? GetPreferredTargetWindow()
        {
            if (_lastActiveWindow != null && _lastActiveWindow.IsVisible && !_lastActiveWindow.IsClosingOrClosed)
                return _lastActiveWindow;

            for (int i = _windows.Count - 1; i >= 0; i--)
            {
                MainWindow w = _windows[i];
                if (w.IsVisible && !w.IsClosingOrClosed)
                    return w;
            }

            for (int i = _windows.Count - 1; i >= 0; i--)
            {
                MainWindow w = _windows[i];
                if (!w.IsClosingOrClosed)
                    return w;
            }

            return null;
        }

        internal bool IsAnyLeafTabWindowForeground()
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero)
                return false;

            NativeMethods.GetWindowThreadProcessId(fg, out uint pid);
            return pid == s_currentPid;
        }

        internal (MainWindow Window, ExplorerTabItem Tab)? TryFindExistingOpenTab(IntPtr targetPidl, bool skipThisPcWhenForeground)
        {
            if (targetPidl == IntPtr.Zero)
                return null;

            if (skipThisPcWhenForeground && IsAnyLeafTabWindowForeground())
            {
                IntPtr thisPc = PidlManager.CreateThisPcPidl();
                try
                {
                    if (thisPc != IntPtr.Zero && PidlManager.AreEqual(targetPidl, thisPc))
                        return null;
                }
                finally
                {
                    PidlManager.Free(ref thisPc);
                }
            }

            // Check last-active visible window first, then remaining visible windows
            var ordered = new List<MainWindow>(_windows.Count);
            if (_lastActiveWindow != null && _lastActiveWindow.IsVisible && !_lastActiveWindow.IsClosingOrClosed)
            {
                ordered.Add(_lastActiveWindow);
            }
            foreach (MainWindow w in _windows)
            {
                if (!ReferenceEquals(w, _lastActiveWindow) && w.IsVisible && !w.IsClosingOrClosed)
                {
                    ordered.Add(w);
                }
            }

            foreach (MainWindow w in ordered)
            {
                foreach (ExplorerTabItem tab in w.Tabs)
                {
                    if (!tab.BrowserHost.IsShowingSearchResults &&
                        tab.CurrentPidl != IntPtr.Zero &&
                        PidlManager.AreEqual(tab.CurrentPidl, targetPidl))
                    {
                        return (w, tab);
                    }
                }
            }

            return null;
        }

        internal async Task RouteExternalLaunchRequestAsync(LaunchActionRequest incoming)
        {
            if (incoming.IsPassiveStartup)
                return;

            if (incoming.IsQuitProcess)
            {
                ForceExitAllWindows();
                return;
            }

            _hasHandledExternalOpen = true;
            _comRelayIdleTimer?.Stop();

            if (incoming.OpenInNewWindow)
            {
                // If no visible window is currently open (only Warm Standby), wake the standby window!
                if (GetVisibleWindowsCount() == 0)
                {
                    MainWindow? standby = GetPreferredTargetWindow();
                    if (standby != null)
                    {
                        await standby.HandleExternalLaunchRequestAsync(incoming, isInitialLaunch: true);
                        return;
                    }
                }

                MainWindow newWin = await CreateSecondaryWindowCoreAsync(
                    cascadeFrom: GetPreferredTargetWindow(),
                    screenTopLeftDip: null);
                await newWin.HandleExternalLaunchRequestAsync(incoming, isInitialLaunch: true);
                return;
            }

            MainWindow? targetWin = GetPreferredTargetWindow();
            if (targetWin == null)
            {
                targetWin = await CreateSecondaryWindowCoreAsync(cascadeFrom: null, screenTopLeftDip: null);
                await targetWin.HandleExternalLaunchRequestAsync(incoming, isInitialLaunch: true);
                return;
            }

            await targetWin.HandleExternalLaunchRequestAsync(incoming, isInitialLaunch: false);
        }

        internal async Task<MainWindow> OpenNewIndependentWindowAsync(
            IntPtr targetPidl,
            ClosedTabSnapshot? historySnapshot = null,
            MainWindow? cascadeFrom = null,
            Point? screenTopLeftDip = null)
        {
            IntPtr clonedPidl = targetPidl != IntPtr.Zero
                ? PidlManager.Clone(targetPidl)
                : PidlManager.CreateThisPcPidl();

            try
            {
                MainWindow newWin = await CreateSecondaryWindowCoreAsync(cascadeFrom, screenTopLeftDip);
                ExplorerTabItem tab = newWin.AddNewTab(clonedPidl, activate: true, animateOpen: false);
                if (historySnapshot != null)
                {
                    tab.RestoreHistoryFromSnapshot(historySnapshot);
                    newWin.RefreshNavigationChromeState();
                }
                newWin.BringWindowToFront();
                return newWin;
            }
            finally
            {
                PidlManager.Free(ref clonedPidl);
            }
        }

        private async Task<MainWindow> CreateSecondaryWindowCoreAsync(MainWindow? cascadeFrom, Point? screenTopLeftDip)
        {
            var newWin = new MainWindow(skipSessionPersistence: _isSelfTest, deferInitialFallbackTab: true);

            if (screenTopLeftDip.HasValue)
            {
                newWin.WindowState = WindowState.Normal;
                newWin.WindowStartupLocation = WindowStartupLocation.Manual;
                newWin.Left = ClampHorizontalScreen(screenTopLeftDip.Value.X, newWin.Width);
                newWin.Top = ClampVerticalScreen(screenTopLeftDip.Value.Y, newWin.Height);
            }
            else if (cascadeFrom != null)
            {
                Rect baseBounds = cascadeFrom.WindowState == WindowState.Normal
                    ? new Rect(cascadeFrom.Left, cascadeFrom.Top, cascadeFrom.Width, cascadeFrom.Height)
                    : cascadeFrom.RestoreBounds;

                if (baseBounds.Width >= newWin.MinWidth && baseBounds.Height >= newWin.MinHeight)
                {
                    newWin.WindowState = WindowState.Normal;
                    newWin.WindowStartupLocation = WindowStartupLocation.Manual;
                    newWin.Width = baseBounds.Width;
                    newWin.Height = baseBounds.Height;
                    newWin.Left = ClampHorizontalScreen(baseBounds.Left + 28, baseBounds.Width);
                    newWin.Top = ClampVerticalScreen(baseBounds.Top + 28, baseBounds.Height);
                }
            }

            RegisterWindow(newWin);
            newWin.Show();
            await newWin.WaitUntilLoadedAsync();
            return newWin;
        }

        private static double ClampHorizontalScreen(double x, double width)
        {
            double minX = SystemParameters.VirtualScreenLeft;
            double maxX = Math.Max(minX, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Math.Min(width, 320));
            return Math.Max(minX, Math.Min(x, maxX));
        }

        private static double ClampVerticalScreen(double y, double height)
        {
            double minY = SystemParameters.VirtualScreenTop;
            double maxY = Math.Max(minY, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Math.Min(height, 220));
            return Math.Max(minY, Math.Min(y, maxY));
        }

        internal void ForceExitAllWindows()
        {
            if (_isShuttingDownAll)
                return;

            _isShuttingDownAll = true;
            MainWindow[] snapshot = _windows.ToArray();
            Views.MainWindow.SaveUnifiedSessionBeforeExit(snapshot, GetPreferredTargetWindow());
            foreach (MainWindow w in snapshot)
            {
                w.PrepareForForceExit(skipPerWindowSave: true);
                try { w.Close(); } catch { }
            }
            Shutdown();
        }

        internal void HandoffToFixedInstallationAndExit(string installedExePath)
        {
            var argsBuilder = new StringBuilder("--handoff");
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (MainWindow w in _windows)
            {
                if (!w.IsVisible || w.IsClosingOrClosed)
                    continue;

                foreach (ExplorerTabItem tab in w.Tabs)
                {
                    string p = tab.GetPersistablePath();
                    if (!string.IsNullOrWhiteSpace(p) && seenPaths.Add(p))
                    {
                        string escaped = p.EndsWith("\\", StringComparison.Ordinal) ? p + "\\" : p;
                        argsBuilder.Append(" \"").Append(escaped).Append('"');
                    }
                }
            }

            // Release COM class object & single-instance mutex BEFORE spawning installed copy
            try { _comServer?.Dispose(); _comServer = null; } catch { }
            try { _singleInstance?.Dispose(); _singleInstance = null; } catch { }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = installedExePath,
                    Arguments = argsBuilder.ToString(),
                    WorkingDirectory = Path.GetDirectoryName(installedExePath) ?? string.Empty,
                    UseShellExecute = true
                });
            }
            catch
            {
            }

            ForceExitAllWindows();
        }

        private void StartHiddenComRelay()
        {
            // DCOM may start a server at another integrity level while our instance mutex
            // is owned. Keep its COM endpoint alive for Execute, without forwarding the
            // empty bootstrap as a user request to restore the primary window.
            TouchComActivity();
            _comServer = new ComShellExecuteServer(incoming =>
            {
                TouchComActivity();
                if (incoming.IsPassiveStartup)
                    return;
                DiagnosticLog.Write("com-relay-open-request");
                if (_singleInstance?.TryForwardToPrimary(incoming, timeoutMs: 1200) != true)
                {
                    DiagnosticLog.Write("com-relay-forward-failed-fallback-local");
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _hasHandledExternalOpen = true;
                        _comRelayIdleTimer?.Stop();
                        _ = RouteExternalLaunchRequestAsync(incoming);
                    }));
                }
            }, onActivity: TouchComActivity);

            if (!_comServer.Start())
            {
                DiagnosticLog.Write("com-relay-registration-failed");
                Shutdown(1);
                return;
            }

            DiagnosticLog.Write("com-relay-started-hidden");
            StartComIdleTimer(isPrimaryInstance: false);
        }

        private void TouchComActivity() =>
            Interlocked.Exchange(ref _lastComActivityTimestamp, Stopwatch.GetTimestamp());

        internal static bool ShouldExitOnComIdle(
            bool isPrimaryInstance,
            int visibleWindowsCount,
            bool hasHandledExternalOpen,
            long idleStopwatchTicks)
        {
            if (visibleWindowsCount > 0)
                return false;
            if (isPrimaryInstance && hasHandledExternalOpen)
                return false;
            return idleStopwatchTicks >= 30L * Stopwatch.Frequency;
        }

        private void StartComIdleTimer(bool isPrimaryInstance)
        {
            TouchComActivity();
            _comRelayIdleTimer?.Stop();
            _comRelayIdleTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _comRelayIdleTimer.Tick += (_, _) =>
            {
                int visibleCount = GetVisibleWindowsCount();
                if (visibleCount > 0 || (isPrimaryInstance && _hasHandledExternalOpen))
                {
                    _comRelayIdleTimer.Stop();
                    return;
                }

                long idleTicks = Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastComActivityTimestamp);
                if (!ShouldExitOnComIdle(isPrimaryInstance, visibleCount, _hasHandledExternalOpen, idleTicks))
                    return;

                DiagnosticLog.Write("com-server-idle-exit");
                _comRelayIdleTimer.Stop();
                Shutdown();
            };
            _comRelayIdleTimer.Start();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _comRelayIdleTimer?.Stop();
            _comServer?.Dispose();
            _singleInstance?.Dispose();
            UiaGestureDetector.Shared.Dispose();
            base.OnExit(e);
        }
    }
}
