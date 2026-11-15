using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using LeafTab.Controls;
using LeafTab.Interop;
using LeafTab.Models;
using LeafTab.Services;
using LeafTab.Shared;

namespace LeafTab.Views
{
    public partial class MainWindow : Window
    {
        private const double TabStripHeightDip = 40.0;
        private const double NavBarHeightDip = 44.0;
        private const double CommandBarHeightDip = 42.0;
        private const double BookmarkBarHeightDip = 32.0;
        private const double BottomStatusBarHeightDip = 28.0;
        private const int MaxClosedTabSnapshots = 20;
        private const int MaxRecentFolders = 15;

        private static readonly CubicEase s_cubicOutEase = CreateFrozenCubicOutEase();

        private static CubicEase CreateFrozenCubicOutEase()
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            ease.Freeze();
            return ease;
        }

        private readonly ObservableCollection<ExplorerTabItem> _tabs = new ObservableCollection<ExplorerTabItem>();
        private readonly ObservableCollection<BookmarkItemViewModel> _bookmarks;
        private readonly AppConfigStore? _sharedStore;
        private readonly Stack<ClosedTabSnapshot> _closedTabs = new Stack<ClosedTabSnapshot>();
        private readonly List<string> _recentFolders;

        private ExplorerTabItem? _selectedTab;
        private readonly bool _skipSessionPersistence;
        private AppConfig _config = new AppConfig();

        // Single-instance pre-warmed background "此电脑" host for instantaneous (< 2ms) '+' / Ctrl+T tabs
        private ExplorerBrowserHost? _prewarmedThisPcHost;
        private IntPtr _cachedThisPcPidl;
        private bool _enableAutoPrewarm;
        private bool _prewarmScheduled;

        // Cross-tab OLE file drag hover-to-switch (~380ms)
        private readonly DispatcherTimer _dragHoverSwitchTimer;
        private ExplorerTabItem? _pendingDragHoverTab;

        // Debounce timer for live async search input
        private readonly DispatcherTimer _searchDebounceTimer;
        private ExplorerTabItem? _pendingSearchTab;
        private bool _suppressSearchTextChanged;
        private bool _suppressAddressSuggestUpdate;

        // Debounced background session auto-save timer (protects open tabs & bookmarks against unexpected exit)


        // Lightweight periodic UI/selection sync timer
        private readonly DispatcherTimer _statusRefreshTimer;
        private DateTime _transientStatusUntilUtc = DateTime.MinValue;

        // Tab drag-to-reorder & tear-out state
        private ExplorerTabItem? _tabReorderCandidate;
        private Point _tabReorderStartPt;
        private bool _isReorderingTab;
        private bool _isTabTearOutArmed;

        private HwndSource? _windowHwndSource;
        private CancellationTokenSource? _addressParseCts;
        private CancellationTokenSource? _addressSuggestCts;
        private int _addressNavRequestId;
        private readonly TaskCompletionSource<bool> _loadedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _deferInitialFallbackTab;
        private ExplorerTabItem? _initialAutoFallbackTab;
        private DateTime _initialFallbackCreatedUtc = DateTime.MinValue;
        private string _lastRenderedBreadcrumbKey = string.Empty;
        private bool _isCurrentFolderStarred;
        private bool _isGlassActive;
        private bool _showBookmarkBar;
        private bool _showPreviewPane;
        private double _previewPaneWidthDip = 340.0;
        private bool _previewMarkdownRawMode;
        private string _lastPreviewSelectionKey = string.Empty;
        private FilePreviewData? _currentPreviewData;
        private CancellationTokenSource? _previewLoadCts;
        private int _previewRequestVersion;
        private bool _enableWarmStandby = true;
        private bool _restorePreviousTabs;
        private bool _isRestoringSavedTabs;
        private bool _forceExitProcess;
        private bool _skipSaveOnForceClose;
        private bool _isLoadedCompleted;
        private bool _isClosingWindow;

        public ObservableCollection<ExplorerTabItem> Tabs => _tabs;
        public ExplorerTabItem? SelectedTab => _selectedTab;
        internal bool HasPrewarmedHost => _prewarmedThisPcHost != null;
        internal bool IsClosingOrClosed => _isClosingWindow;
        internal bool IsStatusPollingActive => _statusRefreshTimer.IsEnabled;
        internal bool IsPreviewPaneOpen => _showPreviewPane && PreviewSidebarBorder.Visibility == Visibility.Visible;
        internal double PreviewPaneWidthDip => _previewPaneWidthDip;
        internal bool RestorePreviousTabs => _restorePreviousTabs;
        internal void SetRestorePreviousTabs(bool enabled) => _restorePreviousTabs = enabled;
        internal string CurrentPreviewKindName => _currentPreviewData?.Kind.ToString() ?? "None";
        internal string CurrentPreviewTextContent => _currentPreviewData?.TextContent ?? string.Empty;
        internal IReadOnlyList<string> RecentFolders => _recentFolders;
        internal void RefreshNavigationChromeState() => UpdateNavigationButtonsState();

        public MainWindow() : this(skipSessionPersistence: false, deferInitialFallbackTab: false)
        {
        }

        public MainWindow(bool skipSessionPersistence, bool deferInitialFallbackTab = false)
            : this(skipSessionPersistence, deferInitialFallbackTab, null) { }

        internal MainWindow(AppConfigStore store) : this(false, true, store) { }

        private MainWindow(bool skipSessionPersistence, bool deferInitialFallbackTab, AppConfigStore? store)
        {
            _sharedStore = skipSessionPersistence ? null : store ?? AppConfigStore.Current;
            _bookmarks = _sharedStore?.Bookmarks ?? new ObservableCollection<BookmarkItemViewModel>();
            _recentFolders = _sharedStore?.RecentFolders ?? new List<string>();
            _skipSessionPersistence = skipSessionPersistence;
            _deferInitialFallbackTab = deferInitialFallbackTab;
            _enableAutoPrewarm = !skipSessionPersistence;

            InitializeComponent();

            TabsItemsControl.ItemsSource = _tabs;
            BookmarksItemsControl.ItemsSource = _bookmarks;

            _dragHoverSwitchTimer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(380)
            };
            _dragHoverSwitchTimer.Tick += DragHoverSwitchTimer_Tick;

            _searchDebounceTimer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(110)
            };
            _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;

            _statusRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(350)
            };
            _statusRefreshTimer.Tick += (_, _) => UpdateCommandBarAndStatus(computeBytes: true);

            LoadConfigurationAndWindowPlacement();
            if (_sharedStore != null) _sharedStore.Changed += SharedConfigurationChanged;

            SourceInitialized += MainWindow_SourceInitialized;
            Loaded += MainWindow_Loaded;
            StateChanged += MainWindow_StateChanged;
            LocationChanged += (_, _) => AddressSuggestPopup.IsOpen = false;
            SizeChanged += (_, _) =>
            {
                AddressSuggestPopup.IsOpen = false;
                if (_showPreviewPane)
                {
                    double maxW = ComputePreviewMaxWidth();
                    PreviewPaneColumn.MaxWidth = maxW;
                    if (PreviewPaneColumn.Width.IsAbsolute && PreviewPaneColumn.Width.Value > maxW)
                    {
                        PreviewPaneColumn.Width = new GridLength(maxW);
                    }
                }
            };
            BrowserStackGrid.SizeChanged += (_, _) => _selectedTab?.BrowserHost.SyncBrowserRect();
            PreviewMouseDown += (_, _) => _selectedTab?.BrowserHost.SuppressNativeFocusRect();
            Activated += (_, _) =>
            {
                UpdateChromeLuminosity(isWindowActive: true);
                SyncStatusTimerState();
                UpdateCommandBarAndStatus(computeBytes: true);
                _selectedTab?.BrowserHost.SuppressNativeFocusRect();
            };
            Deactivated += (_, _) =>
            {
                AddressSuggestPopup.IsOpen = false;
                UpdateChromeLuminosity(isWindowActive: false);
                SyncStatusTimerState();
            };
            Closing += MainWindow_Closing;
            Closed += MainWindow_Closed;
        }

        private void SyncStatusTimerState()
        {
            bool shouldRun =
                _isLoadedCompleted &&
                !_isClosingWindow &&
                IsVisible &&
                WindowState != WindowState.Minimized &&
                (IsActive || (_selectedTab?.BrowserHost.IsSearchRunning ?? false));

            if (shouldRun)
            {
                if (!_statusRefreshTimer.IsEnabled)
                {
                    _statusRefreshTimer.Start();
                }
            }
            else if (_statusRefreshTimer.IsEnabled)
            {
                _statusRefreshTimer.Stop();
            }
        }

        private void LoadConfigurationAndWindowPlacement()
        {
            if (_skipSessionPersistence)
                return;

            _config = _sharedStore!.Settings;
            _showBookmarkBar = _config.ShowBookmarkBar;
            _showPreviewPane = _config.ShowPreviewPane;
            _previewPaneWidthDip = _config.PreviewPaneWidthDip;
            _enableWarmStandby = _config.EnableWarmStandby;
            _restorePreviousTabs = _config.RestorePreviousTabs;

            BookmarkBarBorder.Visibility = _showBookmarkBar ? Visibility.Visible : Visibility.Collapsed;
            CommandBarSurface.BorderThickness = _showBookmarkBar ? new Thickness(0) : new Thickness(0, 0, 0, 1);
            UpdatePreviewButtonVisual();
            ApplyPreviewPaneLayout();

            if (_config.WindowWidth >= MinWidth && _config.WindowHeight >= MinHeight)
            {
                Width = Math.Min(_config.WindowWidth, SystemParameters.VirtualScreenWidth);
                Height = Math.Min(_config.WindowHeight, SystemParameters.VirtualScreenHeight);
            }

            if (_config.WindowLeft.HasValue && _config.WindowTop.HasValue)
            {
                double left = _config.WindowLeft.Value;
                double top = _config.WindowTop.Value;
                if (left >= SystemParameters.VirtualScreenLeft - 40 &&
                    top >= SystemParameters.VirtualScreenTop - 40 &&
                    left + 200 <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
                    top + 120 <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = left;
                    Top = top;
                }
            }

            if (_config.IsMaximized)
            {
                WindowState = WindowState.Maximized;
            }
        }

        private void CaptureOpenTabsSnapshotForStore(bool isWindowClosing = false)
        {
            if (_sharedStore == null || _isRestoringSavedTabs || _skipSaveOnForceClose)
                return;

            if (!_restorePreviousTabs)
            {
                _sharedStore.UpdateSavedTabs(null, 0);
                return;
            }

            // Do not overwrite previously saved tabs when an already-hidden standby window with 0 tabs exits.
            if (!IsVisible && _tabs.Count == 0)
                return;

            var items = new List<SavedTabConfigItem>();
            int selectedIdx = 0;

            if (App.CurrentApp != null)
            {
                if (isWindowClosing && !_forceExitProcess)
                {
                    // If this secondary window is closing while other visible windows remain open, keep remaining windows' tabs.
                    var otherTabs = new List<(ExplorerTabItem Tab, bool IsSelected)>();
                    foreach (MainWindow w in App.CurrentApp.ManagedWindows)
                    {
                        if (!ReferenceEquals(w, this) && w.IsVisible && !w.IsClosingOrClosed && w.Tabs.Count > 0)
                        {
                            foreach (ExplorerTabItem t in w.Tabs)
                            {
                                otherTabs.Add((t, ReferenceEquals(w, App.CurrentApp.MainWindow) && ReferenceEquals(t, w.SelectedTab)));
                            }
                        }
                    }

                    if (otherTabs.Count > 0)
                    {
                        foreach (var entry in otherTabs)
                        {
                            SavedTabConfigItem exported = entry.Tab.ExportSavedConfig();
                            if (!string.IsNullOrWhiteSpace(exported.CurrentPath))
                            {
                                if (entry.IsSelected) selectedIdx = items.Count;
                                items.Add(exported);
                            }
                        }
                        if (items.Count > 0)
                        {
                            _sharedStore.UpdateSavedTabs(items, selectedIdx);
                        }
                        return;
                    }
                }
                else if (!isWindowClosing)
                {
                    // Live session auto-save: aggregate tabs across all visible open windows so multi-window sessions never clobber one another.
                    MainWindow? activeWin = App.CurrentApp.GetPreferredTargetWindow() ?? this;
                    var allTabs = new List<(ExplorerTabItem Tab, bool IsSelected)>();
                    foreach (MainWindow w in App.CurrentApp.ManagedWindows)
                    {
                        if (w.IsVisible && !w.IsClosingOrClosed && w.Tabs.Count > 0)
                        {
                            foreach (ExplorerTabItem t in w.Tabs)
                            {
                                allTabs.Add((t, ReferenceEquals(w, activeWin) && ReferenceEquals(t, w.SelectedTab)));
                            }
                        }
                    }

                    if (allTabs.Count > 0)
                    {
                        foreach (var entry in allTabs)
                        {
                            SavedTabConfigItem exported = entry.Tab.ExportSavedConfig();
                            if (!string.IsNullOrWhiteSpace(exported.CurrentPath))
                            {
                                if (entry.IsSelected) selectedIdx = items.Count;
                                items.Add(exported);
                            }
                        }
                        if (items.Count > 0)
                        {
                            _sharedStore.UpdateSavedTabs(items, selectedIdx);
                        }
                        return;
                    }
                }
            }

            if (_tabs.Count == 0)
                return;

            for (int i = 0; i < _tabs.Count; i++)
            {
                ExplorerTabItem tab = _tabs[i];
                SavedTabConfigItem exported = tab.ExportSavedConfig();
                if (!string.IsNullOrWhiteSpace(exported.CurrentPath))
                {
                    if (ReferenceEquals(tab, _selectedTab))
                    {
                        selectedIdx = items.Count;
                    }
                    items.Add(exported);
                }
            }

            if (items.Count > 0)
            {
                _sharedStore.UpdateSavedTabs(items, selectedIdx);
            }
        }

        internal static void SaveUnifiedSessionBeforeExit(IReadOnlyList<MainWindow> windows, MainWindow? preferredWindow)
        {
            if (windows == null || windows.Count == 0)
                return;

            var visibleWithTabs = new List<MainWindow>(windows.Count);
            foreach (MainWindow w in windows)
            {
                if (!w.IsClosingOrClosed && w.Tabs.Count > 0 && (w.IsVisible || windows.Count > 1))
                {
                    visibleWithTabs.Add(w);
                }
            }

            MainWindow? primary = null;
            if (preferredWindow != null && !preferredWindow.IsClosingOrClosed)
            {
                primary = preferredWindow;
            }
            else if (visibleWithTabs.Count > 0)
            {
                primary = visibleWithTabs[visibleWithTabs.Count - 1];
            }
            else
            {
                for (int i = windows.Count - 1; i >= 0; i--)
                {
                    if (!windows[i].IsClosingOrClosed)
                    {
                        primary = windows[i];
                        break;
                    }
                }
            }

            if (primary?._sharedStore == null || primary._skipSessionPersistence)
                return;

            AppConfigStore store = primary._sharedStore;
            store.UpdatePreferences(
                primary._showBookmarkBar,
                primary._showPreviewPane,
                primary._enableWarmStandby,
                primary._previewPaneWidthDip,
                primary._restorePreviousTabs);

            if (!primary._restorePreviousTabs)
            {
                store.UpdateSavedTabs(null, 0);
            }
            else if (visibleWithTabs.Count > 0)
            {
                var items = new List<SavedTabConfigItem>();
                int selectedIdx = 0;
                MainWindow activeWin = visibleWithTabs.Contains(primary) ? primary : visibleWithTabs[visibleWithTabs.Count - 1];

                foreach (MainWindow w in visibleWithTabs)
                {
                    foreach (ExplorerTabItem tab in w.Tabs)
                    {
                        SavedTabConfigItem exported = tab.ExportSavedConfig();
                        if (!string.IsNullOrWhiteSpace(exported.CurrentPath))
                        {
                            if (ReferenceEquals(w, activeWin) && ReferenceEquals(tab, w.SelectedTab))
                            {
                                selectedIdx = items.Count;
                            }
                            items.Add(exported);
                        }
                    }
                }

                if (items.Count > 0)
                {
                    store.UpdateSavedTabs(items, selectedIdx);
                }
            }

            if (primary.IsVisible)
            {
                Rect bounds = primary.WindowState == WindowState.Normal
                    ? new Rect(primary.Left, primary.Top, primary.Width, primary.Height)
                    : primary.RestoreBounds;
                store.UpdatePlacement(bounds, primary.WindowState == WindowState.Maximized);
            }

            store.Flush();
        }

        private void SaveConfigurationSnapshot(bool isWindowClosing = false)
        {
            if (_sharedStore == null) return;
            _sharedStore.UpdatePreferences(
                _showBookmarkBar,
                _showPreviewPane,
                _enableWarmStandby,
                _previewPaneWidthDip,
                _restorePreviousTabs);
            CaptureOpenTabsSnapshotForStore(isWindowClosing);
            Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            _sharedStore.UpdatePlacement(bounds, WindowState == WindowState.Maximized);
            if (!_sharedStore.Flush() && !_isClosingWindow)
                SetTransientStatus("配置保存失败，当前修改仍保留在内存中。", 5000);
        }

        private void SharedConfigurationChanged(object? sender, EventArgs e)
        {
            if (_isClosingWindow || _sharedStore == null) return;
            var cfg = _sharedStore.Settings;
            _enableWarmStandby = cfg.EnableWarmStandby;
            _restorePreviousTabs = cfg.RestorePreviousTabs;
            bool widthChanged = Math.Abs(_previewPaneWidthDip - cfg.PreviewPaneWidthDip) >= 1.0;
            if (widthChanged)
            {
                _previewPaneWidthDip = cfg.PreviewPaneWidthDip;
            }
            if (_showBookmarkBar != cfg.ShowBookmarkBar)
            {
                _showBookmarkBar = cfg.ShowBookmarkBar;
                BookmarkBarBorder.Visibility = _showBookmarkBar ? Visibility.Visible : Visibility.Collapsed;
                CommandBarSurface.BorderThickness = _showBookmarkBar ? new Thickness(0) : new Thickness(0, 0, 0, 1);
                SyncGlassRegion();
                _selectedTab?.BrowserHost.SyncBrowserRect();
            }
            if (_showPreviewPane != cfg.ShowPreviewPane)
            {
                _showPreviewPane = cfg.ShowPreviewPane;
                foreach (var tab in _tabs) tab.BrowserHost.SetPreviewPaneVisible(_showPreviewPane);
                UpdatePreviewButtonVisual();
                ApplyPreviewPaneLayout();
            }
            else if (widthChanged && _showPreviewPane)
            {
                ApplyPreviewPaneLayout();
            }
            HydrateBookmarkIconsDeferred();
            UpdateBookmarkStarVisual();
        }

        private void RecordRecentFolder(ExplorerTabItem tab)
        {
            if (tab == null || tab.BrowserHost.IsShowingSearchResults)
                return;

            string path = tab.GetPersistablePath();
            if (string.IsNullOrWhiteSpace(path) ||
                string.Equals(path, "此电脑", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            for (int i = _recentFolders.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_recentFolders[i], path, StringComparison.OrdinalIgnoreCase))
                {
                    _recentFolders.RemoveAt(i);
                }
            }

            _recentFolders.Insert(0, path);
            if (_recentFolders.Count > MaxRecentFolders)
            {
                _recentFolders.RemoveRange(MaxRecentFolders, _recentFolders.Count - MaxRecentFolders);
            }
        }

        private void ScheduleSessionSave()
        {
            if (_skipSessionPersistence || _isClosingWindow || !_isLoadedCompleted || _isRestoringSavedTabs)
                return;

            _sharedStore?.UpdatePreferences(
                _showBookmarkBar,
                _showPreviewPane,
                _enableWarmStandby,
                _previewPaneWidthDip,
                _restorePreviousTabs);
            CaptureOpenTabsSnapshotForStore();
            _sharedStore?.ScheduleSave();
        }

        // ==================== PARTITIONED LIQUID GLASS BACKDROP & WINDOW CHROME ====================

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                IntPtr hideWParam = NativeMethods.MakeUiStateWParam(
                    NativeMethods.UIS_SET,
                    (ushort)(NativeMethods.UISF_HIDEFOCUS | NativeMethods.UISF_HIDEACCEL));
                NativeMethods.SendMessageW(hwnd, NativeMethods.WM_CHANGEUISTATE, hideWParam, IntPtr.Zero);
                NativeMethods.SendMessageW(hwnd, NativeMethods.WM_UPDATEUISTATE, hideWParam, IntPtr.Zero);

                _windowHwndSource = HwndSource.FromHwnd(hwnd);
                _windowHwndSource?.AddHook(MainWindowWndProc);
            }

            (int topPx, int bottomPx) = ComputeGlassMarginsPixels();
            _isGlassActive = WindowBackdropHelper.TryApplyLiquidGlassBackdrop(this, hwnd, topPx, bottomPx);
            UpdateChromeLuminosity(isWindowActive: true);

            ComponentDispatcher.ThreadPreprocessMessage += ComponentDispatcher_ThreadPreprocessMessage;
            UpdateMaximizedLayoutMargin();
        }

        private IntPtr MainWindowWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == (int)NativeMethods.WM_APPCOMMAND && !_isClosingWindow)
            {
                int cmd = (int)((lParam.ToInt64() >> 16) & 0x0FFF);
                if (cmd == NativeMethods.APPCOMMAND_BROWSER_BACKWARD)
                {
                    HandleMouseSideButton(NativeMethods.XBUTTON1);
                    handled = true;
                    return new IntPtr(1);
                }
                if (cmd == NativeMethods.APPCOMMAND_BROWSER_FORWARD)
                {
                    HandleMouseSideButton(NativeMethods.XBUTTON2);
                    handled = true;
                    return new IntPtr(1);
                }
            }
            return IntPtr.Zero;
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            SyncGlassRegion();
        }

        private (double TopDip, double BottomDip) ComputeGlassMarginsDip()
        {
            double padTop = RootFrameBorder.Padding.Top;
            double padBottom = RootFrameBorder.Padding.Bottom;

            double topDip = TabStripHeightDip + NavBarHeightDip + CommandBarHeightDip
                            + (_showBookmarkBar ? BookmarkBarHeightDip : 0.0)
                            + padTop;
            double bottomDip = BottomStatusBarHeightDip + padBottom;
            return (topDip, bottomDip);
        }

        private (int TopPx, int BottomPx) ComputeGlassMarginsPixels()
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            (double topDip, double bottomDip) = ComputeGlassMarginsDip();
            return (
                (int)Math.Ceiling(topDip * dpi.DpiScaleY),
                (int)Math.Ceiling(bottomDip * dpi.DpiScaleY));
        }

        private void SyncGlassRegion()
        {
            (double topDip, double bottomDip) = ComputeGlassMarginsDip();
            MainWindowChrome.GlassFrameThickness = new Thickness(0, topDip, 0, bottomDip);
            CommandBarSurface.BorderThickness = _showBookmarkBar ? new Thickness(0) : new Thickness(0, 0, 0, 1);

            if (_isGlassActive)
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                (int topPx, int bottomPx) = ComputeGlassMarginsPixels();
                WindowBackdropHelper.UpdateGlassFrameMargins(hwnd, topPx, bottomPx);
            }
        }

        private void UpdateChromeLuminosity(bool isWindowActive)
        {
            if (!_isGlassActive)
            {
                TabTitleBarSurface.Background = UiBrushes.Get(
                    isWindowActive ? Color.FromRgb(0xE2, 0xEC, 0xF7) : Color.FromRgb(0xEB, 0xF0, 0xF7));
                NavBarSurface.Background = UiBrushes.Get(Color.FromRgb(0xFA, 0xFC, 0xFF));
                CommandBarSurface.Background = UiBrushes.Get(Color.FromRgb(0xF6, 0xFA, 0xFF));
                BookmarkBarBorder.Background = UiBrushes.Get(Color.FromRgb(0xF6, 0xFA, 0xFF));
                BottomStatusBar.Background = UiBrushes.Get(Color.FromRgb(0xF1, 0xF6, 0xFC));
                return;
            }

            TabTitleBarSurface.Background = UiBrushes.Get(
                isWindowActive
                    ? Color.FromArgb(0x96, 0xDE, 0xE8, 0xF5)
                    : Color.FromArgb(0xDC, 0xEA, 0xEF, 0xF7));
            NavBarSurface.Background = UiBrushes.Get(Color.FromArgb(0xE8, 0xFA, 0xFC, 0xFF));
            CommandBarSurface.Background = UiBrushes.Get(Color.FromArgb(0xEC, 0xF5, 0xF9, 0xFF));
            BookmarkBarBorder.Background = UiBrushes.Get(Color.FromArgb(0xEE, 0xF5, 0xF9, 0xFF));
            BottomStatusBar.Background = UiBrushes.Get(Color.FromArgb(0xDC, 0xEF, 0xF5, 0xFC));
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Match standard Windows 11 Explorer window lifecycle:
            // Closing all windows closes those tabs cleanly; opening a specific folder opens ONLY that folder,
            // and opening LeafTab directly opens a single fresh "此电脑" tab.
            if (_tabs.Count == 0 && !_deferInitialFallbackTab)
            {
                EnsureAtLeastOneTabOpen(markAsAutoFallback: true);
            }

            _isLoadedCompleted = true;
            _loadedTcs.TrySetResult(true);
            SyncStatusTimerState();
            HydrateBookmarkIconsDeferred();
            SchedulePrewarmThisPcHost();
        }

        public void EnsureAtLeastOneTabOpen(bool markAsAutoFallback = false)
        {
            if (_isClosingWindow || _tabs.Count > 0)
                return;

            IntPtr thisPcPidl = PidlManager.CreateThisPcPidl();
            try
            {
                ExplorerTabItem created = AddNewTab(thisPcPidl, activate: true, animateOpen: false);
                if (markAsAutoFallback)
                {
                    _initialAutoFallbackTab = created;
                    _initialFallbackCreatedUtc = DateTime.UtcNow;
                }
            }
            finally
            {
                PidlManager.Free(ref thisPcPidl);
            }
        }

        private void HydrateBookmarkIconsDeferred()
        {
            if (_bookmarks.Count == 0)
                return;

            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (_isClosingWindow)
                    return;

                foreach (BookmarkItemViewModel bm in _bookmarks)
                {
                    AppConfigService.HydrateBookmarkIcon(bm);
                }
            }));
        }

        public Task WaitUntilLoadedAsync() => _loadedTcs.Task;

        public void BringWindowToFront()
        {
            if (_isClosingWindow)
                return;

            DiagnosticLog.Write("window-foreground-request");
            if (!IsVisible)
            {
                Show();
            }

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
                if (hwnd != IntPtr.Zero)
                {
                    NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                }
            }

            if (hwnd != IntPtr.Zero && !NativeMethods.IsWindowVisible(hwnd))
            {
                NativeMethods.ShowWindow(
                    hwnd,
                    WindowState == WindowState.Maximized ? NativeMethods.SW_SHOWMAXIMIZED : NativeMethods.SW_SHOW);
            }

            Activate();
            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.SetForegroundWindow(hwnd);
            }

            SyncStatusTimerState();
        }

        public async Task<bool> TryRestoreSavedTabsIfEnabledAsync()
        {
            if (_skipSessionPersistence || _isClosingWindow || _sharedStore == null)
                return false;

            AppConfig cfg = _sharedStore.Settings;
            if (!cfg.RestorePreviousTabs || cfg.OpenTabs == null || cfg.OpenTabs.Count == 0)
                return false;

            var snapshot = cfg.OpenTabs.ToArray();
            int savedSelectedIdx = Math.Max(0, Math.Min(cfg.SelectedTabIndex, snapshot.Length - 1));
            var restoredTabs = new List<ExplorerTabItem>(snapshot.Length);
            ExplorerTabItem? preferredTab = null;

            _isRestoringSavedTabs = true;
            try
            {
                for (int i = 0; i < snapshot.Length; i++)
                {
                    SavedTabConfigItem item = snapshot[i];
                    if (item == null || string.IsNullOrWhiteSpace(item.CurrentPath))
                        continue;

                    (IntPtr pidl, _) = await PidlManager.TryParseAddressAsync(item.CurrentPath, CancellationToken.None);
                    if (pidl == IntPtr.Zero)
                        continue;

                    try
                    {
                        ExplorerTabItem tab = AddNewTab(pidl, activate: false, animateOpen: false);
                        if (item.HistoryPaths != null && item.HistoryPaths.Count > 0)
                        {
                            tab.RestoreHistory(item.HistoryPaths, item.HistoryIndex);
                        }
                        restoredTabs.Add(tab);
                        if (i == savedSelectedIdx)
                        {
                            preferredTab = tab;
                        }
                    }
                    finally
                    {
                        PidlManager.Free(ref pidl);
                    }
                }
            }
            finally
            {
                _isRestoringSavedTabs = false;
            }

            if (restoredTabs.Count == 0)
                return false;

            ExplorerTabItem toActivate = preferredTab ?? restoredTabs[Math.Min(savedSelectedIdx, restoredTabs.Count - 1)];
            ActivateTab(toActivate, focusNativeView: true);
            UpdateNavigationButtonsState();
            ScheduleSessionSave();
            return true;
        }

        public async Task HandleExternalLaunchRequestAsync(LaunchActionRequest request, bool isInitialLaunch)
        {
            if (_isClosingWindow || request == null || request.IsPassiveStartup)
                return;

            if (request.IsQuitProcess)
            {
                ForceExitApplication();
                return;
            }

            if (!_isLoadedCompleted)
            {
                BringWindowToFront();
                await WaitUntilLoadedAsync();
            }

            bool wakingFromStandby = !IsVisible;

            if (request.Targets.Count == 0)
            {
                if (!wakingFromStandby)
                {
                    BringWindowToFront();
                }

                if ((wakingFromStandby || isInitialLaunch) && _tabs.Count == 0)
                {
                    if (!await TryRestoreSavedTabsIfEnabledAsync())
                    {
                        OpenNewThisPcTab();
                    }
                }
                else if (!isInitialLaunch || _tabs.Count == 0)
                {
                    OpenNewThisPcTab();
                }

                if (wakingFromStandby)
                {
                    BringWindowToFront();
                    _selectedTab?.BrowserHost.FocusNativeView();
                }
                return;
            }

            bool anyOpenedHere = false;
            foreach (LaunchTargetItem item in request.Targets)
            {
                IntPtr pidl = item.PidlBytes != null && item.PidlBytes.Length > 0
                    ? PidlManager.NormalizeToBrowsablePidl(PidlManager.FromBytes(item.PidlBytes))
                    : IntPtr.Zero;

                string? parseErr = null;
                if (pidl == IntPtr.Zero && !string.IsNullOrWhiteSpace(item.FolderOrAlias))
                {
                    (pidl, parseErr) = await PidlManager.TryParseAddressAsync(item.FolderOrAlias, CancellationToken.None);
                }

                if (pidl == IntPtr.Zero)
                {
                    if (!wakingFromStandby) BringWindowToFront();
                    SetTransientStatus(parseErr ?? $"无法打开路径：{item.FolderOrAlias}");
                    continue;
                }

                ExplorerTabItem targetTab;
                try
                {
                    // Smart duplicate folder reuse across open windows (unless user explicitly requested a new window)
                    if (!request.OpenInNewWindow && !isInitialLaunch && !wakingFromStandby && App.CurrentApp != null)
                    {
                        var existing = App.CurrentApp.TryFindExistingOpenTab(pidl, skipThisPcWhenForeground: true);
                        if (existing.HasValue)
                        {
                            MainWindow ownerWin = existing.Value.Window;
                            ExplorerTabItem existingTab = existing.Value.Tab;
                            ownerWin.BringWindowToFront();
                            ownerWin.ActivateTab(existingTab, focusNativeView: true);

                            if (!string.IsNullOrWhiteSpace(item.SelectItemName))
                            {
                                existingTab.BrowserHost.ClearSelection();
                                await existingTab.BrowserHost.SelectChildItemWhenReadyAsync(item.SelectItemName!);
                                ownerWin.UpdateCommandBarAndStatus(computeBytes: true);
                            }
                            else
                            {
                                ownerWin.SetTransientStatus($"已定位到已打开的标签页：{existingTab.Title}");
                            }
                            continue;
                        }
                    }

                    if (!wakingFromStandby && !anyOpenedHere)
                    {
                        BringWindowToFront();
                    }
                    anyOpenedHere = true;

                    bool canReuseInitialFallback =
                        _initialAutoFallbackTab != null &&
                        _tabs.Count == 1 &&
                        ReferenceEquals(_tabs[0], _initialAutoFallbackTab) &&
                        (isInitialLaunch || (DateTime.UtcNow - _initialFallbackCreatedUtc).TotalMilliseconds < 1800);

                    if (canReuseInitialFallback)
                    {
                        targetTab = _initialAutoFallbackTab!;
                        _initialAutoFallbackTab = null;
                        targetTab.SuppressNextHistoryRecord();
                        targetTab.BrowserHost.NavigateToPidl(pidl);
                        ActivateTab(targetTab, focusNativeView: !wakingFromStandby);
                    }
                    else
                    {
                        _initialAutoFallbackTab = null;
                        bool shouldAnimate = !_skipSessionPersistence && !wakingFromStandby && _tabs.Count > 0;
                        targetTab = AddNewTab(pidl, activate: true, animateOpen: shouldAnimate);
                    }
                }
                finally
                {
                    PidlManager.Free(ref pidl);
                }

                if (wakingFromStandby)
                {
                    wakingFromStandby = false;
                    BringWindowToFront();
                    targetTab.BrowserHost.FocusNativeView();
                }

                if (!string.IsNullOrWhiteSpace(item.SelectItemName))
                {
                    await targetTab.BrowserHost.SelectChildItemWhenReadyAsync(item.SelectItemName!);
                    UpdateCommandBarAndStatus(computeBytes: true);
                }
            }

            if (wakingFromStandby)
            {
                BringWindowToFront();
            }

            EnsureAtLeastOneTabOpen();
            if (anyOpenedHere && _selectedTab != null)
            {
                _selectedTab.BrowserHost.FocusNativeView();
            }
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            AddressSuggestPopup.IsOpen = false;
            UpdateMaximizedLayoutMargin();
            SyncStatusTimerState();
        }

        private void UpdateMaximizedLayoutMargin()
        {
            if (WindowState == WindowState.Maximized)
            {
                Thickness resize = SystemParameters.WindowResizeBorderThickness;
                RootFrameBorder.Padding = new Thickness(
                    resize.Left + 4,
                    resize.Top + 4,
                    resize.Right + 4,
                    resize.Bottom + 4);
                MaxRestoreButton.ToolTip = "向下还原";
                MaxRestoreGlyph.Data = Geometry.Parse(
                    "M2.4,0.6 H8.1 A0.8,0.8 0 0 1 8.9,1.4 V7.1 M1.4,2.4 H7.1 A0.8,0.8 0 0 1 7.9,3.2 V8.1 A0.8,0.8 0 0 1 7.1,8.9 H1.4 A0.8,0.8 0 0 1 0.6,8.1 V3.2 A0.8,0.8 0 0 1 1.4,2.4 Z");
            }
            else
            {
                RootFrameBorder.Padding = new Thickness(0);
                MaxRestoreButton.ToolTip = "最大化";
                MaxRestoreGlyph.Data = Geometry.Parse(
                    "M1.5,0.6 H8 A0.9,0.9 0 0 1 8.9,1.5 V8 A0.9,0.9 0 0 1 8,8.9 H1.5 A0.9,0.9 0 0 1 0.6,8 V1.5 A0.9,0.9 0 0 1 1.5,0.6 Z");
            }

            SyncGlassRegion();
        }

        // ==================== PRE-WARMED BACKGROUND TAB POOL ====================

        private void SchedulePrewarmThisPcHost(int delayMs = 1200)
        {
            if (!_enableAutoPrewarm || _isClosingWindow || _prewarmedThisPcHost != null || _prewarmScheduled)
                return;

            _prewarmScheduled = true;
            var delayTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(50, delayMs))
            };
            delayTimer.Tick += (_, _) =>
            {
                delayTimer.Stop();
                _prewarmScheduled = false;
                if (_enableAutoPrewarm && !_isClosingWindow && _prewarmedThisPcHost == null)
                {
                    EnsurePrewarmedThisPcHostReady();
                }
            };
            delayTimer.Start();
        }

        internal void EnsurePrewarmedThisPcHostReady()
        {
            if (_isClosingWindow || _prewarmedThisPcHost != null)
                return;

            if (_cachedThisPcPidl == IntPtr.Zero)
            {
                _cachedThisPcPidl = PidlManager.CreateThisPcPidl();
            }
            if (_cachedThisPcPidl == IntPtr.Zero)
                return;

            var host = new ExplorerBrowserHost(_cachedThisPcPidl, _showPreviewPane)
            {
                Visibility = Visibility.Hidden
            };
            _prewarmedThisPcHost = host;
            BrowserStackGrid.Children.Insert(0, host);
        }

        internal void DiscardPrewarmedHost()
        {
            if (_prewarmedThisPcHost == null)
                return;

            ExplorerBrowserHost host = _prewarmedThisPcHost;
            _prewarmedThisPcHost = null;
            BrowserStackGrid.Children.Remove(host);
            host.Dispose();
        }

        private ExplorerBrowserHost? TryConsumePrewarmedHost(IntPtr targetPidl)
        {
            if (_prewarmedThisPcHost == null || _cachedThisPcPidl == IntPtr.Zero || targetPidl == IntPtr.Zero)
                return null;

            ExplorerBrowserHost adopted = _prewarmedThisPcHost;
            _prewarmedThisPcHost = null;
            adopted.SetPreviewPaneVisible(_showPreviewPane);
            return adopted;
        }

        // ==================== TAB LIFECYCLE, ANIMATION & SESSION HISTORY ====================

        public ExplorerTabItem AddNewTab(IntPtr targetPidl, bool activate = true) =>
            AddNewTab(targetPidl, activate, animateOpen: !_skipSessionPersistence, deferHostMount: false);

        public ExplorerTabItem AddNewTab(IntPtr targetPidl, bool activate, bool animateOpen, bool deferHostMount = false)
        {
            IntPtr effectivePidl = targetPidl != IntPtr.Zero
                ? PidlManager.Clone(targetPidl)
                : PidlManager.CreateThisPcPidl();

            try
            {
                ExplorerBrowserHost? adoptedHost = TryConsumePrewarmedHost(effectivePidl);
                var tab = new ExplorerTabItem(effectivePidl, _showPreviewPane, adoptedHost);
                ExplorerBrowserHost host = tab.BrowserHost;

                host.Visibility = activate ? Visibility.Visible : Visibility.Hidden;

                host.NavigationCompleted += (_, args) =>
                {
                    tab.UpdateFromNavigation(args.FolderPidl);
                    RecordRecentFolder(tab);
                    if (ReferenceEquals(tab, _selectedTab))
                    {
                        SyncChromeWithSelectedTab(computeBytes: false);
                    }
                    ScheduleSessionSave();
                };

                host.NavigationFailed += (_, message) =>
                {
                    tab.NotifyNavigationFailed();
                    if (ReferenceEquals(tab, _selectedTab))
                    {
                        UpdateNavigationButtonsState();
                    }
                    SetTransientStatus(message);
                };

                host.FolderMiddleClicked += (_, args) =>
                {
                    AddNewTab(args.TargetFolderPidl, activate: false, animateOpen: !_skipSessionPersistence);
                    SetTransientStatus("已在后台新标签页打开文件夹");
                };

                host.BlankAreaDoubleClicked += (_, _) =>
                {
                    SetTransientStatus("已返回上一级");
                };

                host.ViewInteractionOccurred += (_, _) =>
                {
                    if (ReferenceEquals(tab, _selectedTab))
                    {
                        if (AddressEditGrid.Visibility == Visibility.Visible)
                            ExitAddressEditMode();
                        UpdateCommandBarAndStatus(computeBytes: true);
                    }
                };

                host.SearchStateChanged += (_, _) =>
                {
                    if (ReferenceEquals(tab, _selectedTab))
                    {
                        if (!host.IsShowingSearchResults && !string.IsNullOrEmpty(tab.SearchQuery))
                        {
                            tab.SearchQuery = string.Empty;
                            SetSearchBoxTextSilently(string.Empty);
                        }
                        RebuildBreadcrumbBar(tab);
                        UpdateNavigationButtonsState();
                        SyncStatusTimerState();
                        UpdateCommandBarAndStatus(computeBytes: false);
                    }
                };

                _tabs.Add(tab);
                RecordRecentFolder(tab);
                if (!deferHostMount && !BrowserStackGrid.Children.Contains(host))
                {
                    BrowserStackGrid.Children.Add(host);
                }

                if (animateOpen)
                {
                    AnimateNewTabOpen(tab);
                }

                if (activate || (!deferHostMount && _selectedTab == null))
                {
                    ActivateTab(tab, focusNativeView: true);
                }
                else
                {
                    UpdateCommandBarAndStatus(computeBytes: false);
                }

                if (adoptedHost != null)
                {
                    SchedulePrewarmThisPcHost();
                }

                ScheduleSessionSave();
                return tab;
            }
            finally
            {
                PidlManager.Free(ref effectivePidl);
            }
        }

        private void AnimateNewTabOpen(ExplorerTabItem tab)
        {
            TabsItemsControl.UpdateLayout();
            if (TabsItemsControl.ItemContainerGenerator.ContainerFromItem(tab) is UIElement container)
            {
                AdaptiveTabStripPanel.SetOpenProgress(container, 1.0);
                container.Opacity = 1.0;

                var expandAnim = new DoubleAnimation(0.22, 1.0, TimeSpan.FromMilliseconds(165))
                {
                    EasingFunction = s_cubicOutEase,
                    FillBehavior = FillBehavior.Stop
                };
                var fadeAnim = new DoubleAnimation(0.15, 1.0, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = s_cubicOutEase,
                    FillBehavior = FillBehavior.Stop
                };

                container.BeginAnimation(AdaptiveTabStripPanel.OpenProgressProperty, expandAnim);
                container.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
            }
        }

        public void ActivateTab(ExplorerTabItem tab, bool focusNativeView = true)
        {
            if (_isClosingWindow || !_tabs.Contains(tab))
                return;

            if (!BrowserStackGrid.Children.Contains(tab.BrowserHost))
            {
                BrowserStackGrid.Children.Add(tab.BrowserHost);
            }

            _selectedTab = tab;
            EnsureActiveTabVisible();

            foreach (ExplorerTabItem item in _tabs)
            {
                bool active = ReferenceEquals(item, tab);
                item.IsSelected = active;
                item.BrowserHost.Visibility = active ? Visibility.Visible : Visibility.Hidden;

                if (item.BrowserHost.Handle != IntPtr.Zero)
                {
                    NativeMethods.ShowWindow(
                        item.BrowserHost.Handle,
                        active ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);

                    if (active)
                    {
                        item.BrowserHost.SyncBrowserRect();
                    }
                }
            }

            ExitAddressEditMode();
            SyncChromeWithSelectedTab(computeBytes: false);
            SetSearchBoxTextSilently(tab.SearchQuery);
            ScheduleSessionSave();

            if (focusNativeView)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    if (ReferenceEquals(_selectedTab, tab))
                        tab.BrowserHost.FocusNativeView();
                }));
            }
        }

        private void SyncChromeWithSelectedTab(bool computeBytes = false)
        {
            if (_selectedTab == null)
                return;

            if (!AddressTextBox.IsKeyboardFocusWithin)
            {
                AddressTextBox.Text = _selectedTab.AddressText;
            }

            AddressFolderIcon.Source = _selectedTab.Icon;
            Title = string.IsNullOrWhiteSpace(_selectedTab.Title) ? "此电脑" : _selectedTab.Title;
            SearchPlaceholderText.Text = $"在 {_selectedTab.Title} 中搜索";

            RebuildBreadcrumbBar(_selectedTab);
            UpdateBookmarkStarVisual();
            UpdateNavigationButtonsState();
            UpdateViewModeIndicator();
            UpdateCommandBarAndStatus(computeBytes);
        }

        private void PushClosedTabSnapshot(ExplorerTabItem tab)
        {
            if (_closedTabs.Count >= MaxClosedTabSnapshots)
            {
                var trimmed = _closedTabs.ToArray();
                _closedTabs.Clear();
                for (int i = Math.Min(trimmed.Length - 1, MaxClosedTabSnapshots - 2); i >= 0; i--)
                    _closedTabs.Push(trimmed[i]);
            }
            _closedTabs.Push(tab.CreateClosedSnapshot());
        }

        private void DetachAndDisposeTabFromCurrentWindow(ExplorerTabItem tab)
        {
            int index = _tabs.IndexOf(tab);
            if (index < 0)
                return;

            ClearDragHoverState();

            bool wasSelected = ReferenceEquals(tab, _selectedTab);
            _tabs.RemoveAt(index);
            BrowserStackGrid.Children.Remove(tab.BrowserHost);
            tab.Dispose();

            if (wasSelected && _tabs.Count > 0)
            {
                int nextIdx = Math.Min(index, _tabs.Count - 1);
                ActivateTab(_tabs[nextIdx], focusNativeView: true);
            }
            else
            {
                UpdateCommandBarAndStatus(computeBytes: false);
            }

            ScheduleSessionSave();
        }

        public void CloseTab(ExplorerTabItem tab)
        {
            int index = _tabs.IndexOf(tab);
            if (index < 0)
                return;

            if (_tabs.Count <= 1)
            {
                Close();
                return;
            }

            PushClosedTabSnapshot(tab);
            DetachAndDisposeTabFromCurrentWindow(tab);
        }

        public ExplorerTabItem? DuplicateTab(ExplorerTabItem sourceTab)
        {
            if (sourceTab == null || !_tabs.Contains(sourceTab))
                return null;

            ClosedTabSnapshot snap = sourceTab.CreateClosedSnapshot();
            IntPtr pidl = sourceTab.CurrentPidl != IntPtr.Zero
                ? PidlManager.Clone(sourceTab.CurrentPidl)
                : PidlManager.CreateThisPcPidl();

            try
            {
                int srcIdx = _tabs.IndexOf(sourceTab);
                ExplorerTabItem dup = AddNewTab(pidl, activate: true, animateOpen: !_skipSessionPersistence);
                dup.RestoreHistoryFromSnapshot(snap);

                int dupIdx = _tabs.IndexOf(dup);
                if (srcIdx >= 0 && dupIdx >= 0 && dupIdx != srcIdx + 1 && srcIdx + 1 < _tabs.Count)
                {
                    _tabs.Move(dupIdx, srcIdx + 1);
                }

                UpdateNavigationButtonsState();
                ScheduleSessionSave();
                return dup;
            }
            finally
            {
                PidlManager.Free(ref pidl);
            }
        }

        public async Task<MainWindow?> MoveTabToNewWindowAsync(ExplorerTabItem tab, Point? screenTopLeftDip = null)
        {
            if (App.CurrentApp == null || tab == null || !_tabs.Contains(tab) || _tabs.Count <= 1)
                return null;

            ClosedTabSnapshot snapshot = tab.CreateClosedSnapshot();
            IntPtr clonedPidl = tab.CurrentPidl != IntPtr.Zero
                ? PidlManager.Clone(tab.CurrentPidl)
                : PidlManager.CreateThisPcPidl();

            try
            {
                DetachAndDisposeTabFromCurrentWindow(tab);
                return await App.CurrentApp.OpenNewIndependentWindowAsync(
                    clonedPidl,
                    historySnapshot: snapshot,
                    cascadeFrom: screenTopLeftDip.HasValue ? null : this,
                    screenTopLeftDip: screenTopLeftDip);
            }
            finally
            {
                PidlManager.Free(ref clonedPidl);
            }
        }

        private void CloseTabsToRight(ExplorerTabItem pivotTab)
        {
            int pivotIdx = _tabs.IndexOf(pivotTab);
            if (pivotIdx < 0 || pivotIdx >= _tabs.Count - 1)
                return;

            ClearDragHoverState();
            bool selectedWasRemoved = false;

            for (int i = _tabs.Count - 1; i > pivotIdx; i--)
            {
                ExplorerTabItem victim = _tabs[i];
                if (ReferenceEquals(victim, _selectedTab))
                {
                    selectedWasRemoved = true;
                }
                PushClosedTabSnapshot(victim);
                _tabs.RemoveAt(i);
                BrowserStackGrid.Children.Remove(victim.BrowserHost);
                victim.Dispose();
            }

            if (selectedWasRemoved && _tabs.Count > 0)
            {
                ActivateTab(pivotTab, focusNativeView: true);
            }
            else
            {
                UpdateCommandBarAndStatus(computeBytes: false);
            }

            ScheduleSessionSave();
        }

        private void CloseOtherTabs(ExplorerTabItem keepTab)
        {
            if (!_tabs.Contains(keepTab) || _tabs.Count <= 1)
                return;

            ClearDragHoverState();

            for (int i = _tabs.Count - 1; i >= 0; i--)
            {
                ExplorerTabItem candidate = _tabs[i];
                if (ReferenceEquals(candidate, keepTab))
                    continue;

                PushClosedTabSnapshot(candidate);
                _tabs.RemoveAt(i);
                BrowserStackGrid.Children.Remove(candidate.BrowserHost);
                candidate.Dispose();
            }

            if (!ReferenceEquals(_selectedTab, keepTab))
            {
                ActivateTab(keepTab, focusNativeView: true);
            }
            else
            {
                UpdateCommandBarAndStatus(computeBytes: false);
            }

            ScheduleSessionSave();
        }

        public bool ReopenLastClosedTab()
        {
            while (_closedTabs.Count > 0)
            {
                ClosedTabSnapshot snap = _closedTabs.Pop();
                IntPtr pidl = PidlManager.FromBytes(snap.CurrentPidlBytes);
                if (pidl == IntPtr.Zero && !string.IsNullOrWhiteSpace(snap.CurrentPath))
                {
                    pidl = PidlManager.TryParseLocalOrShellFast(snap.CurrentPath);
                }

                if (pidl != IntPtr.Zero)
                {
                    try
                    {
                        ExplorerTabItem reopened = AddNewTab(pidl, activate: true, animateOpen: !_skipSessionPersistence);
                        reopened.RestoreHistoryFromSnapshot(snap);
                        UpdateNavigationButtonsState();
                        SetTransientStatus($"已恢复标签页：{reopened.Title}");
                        return true;
                    }
                    finally
                    {
                        PidlManager.Free(ref pidl);
                    }
                }
            }

            SetTransientStatus("没有可恢复的已关闭标签页");
            return false;
        }

        // ==================== GLOBAL KEYBOARD ACCELERATORS & MOUSE SIDE BUTTONS ====================

        internal bool HandleMouseSideButton(int xButton)
        {
            if (_selectedTab == null || _isClosingWindow)
                return false;

            if (AddressEditGrid.Visibility == Visibility.Visible)
            {
                ExitAddressEditMode();
            }

            if (xButton == NativeMethods.XBUTTON1)
            {
                bool moved = _selectedTab.NavigateHistoryStep(-1);
                UpdateNavigationButtonsState();
                return moved;
            }

            if (xButton == NativeMethods.XBUTTON2)
            {
                bool moved = _selectedTab.NavigateHistoryStep(+1);
                UpdateNavigationButtonsState();
                return moved;
            }

            return false;
        }

        private async Task OpenNewWindowFromShortcutAsync()
        {
            if (App.CurrentApp == null)
                return;

            IntPtr targetPidl = _selectedTab != null && _selectedTab.CurrentPidl != IntPtr.Zero
                ? _selectedTab.CurrentPidl
                : IntPtr.Zero;

            await App.CurrentApp.OpenNewIndependentWindowAsync(
                targetPidl,
                historySnapshot: null,
                cascadeFrom: this);
        }

        private void OpenTerminalInActiveTab()
        {
            OpenTerminalAtPath(_selectedTab?.FileSystemPath);
        }

        private void OpenTerminalAtPath(string? folderPath)
        {
            QuickLaunchAndInstallService.OpenPreferredTerminal(folderPath, out string statusMsg);
            if (!string.IsNullOrEmpty(statusMsg))
            {
                SetTransientStatus(statusMsg);
            }
        }

        private bool IsPreviewContentFocused() =>
            PreviewSidebarBorder.Visibility == Visibility.Visible &&
            (PreviewTextBox.IsKeyboardFocusWithin || PreviewDocumentViewer.IsKeyboardFocusWithin);

        private void ComponentDispatcher_ThreadPreprocessMessage(ref MSG msg, ref bool handled)
        {
            if (handled || _isClosingWindow || !IsVisible)
                return;

            IntPtr myHwnd = new WindowInteropHelper(this).Handle;
            if (myHwnd == IntPtr.Zero || msg.hwnd == IntPtr.Zero)
                return;

            // Multi-window isolation: only handle messages belonging to THIS window or its native child HWNDs
            IntPtr rootHwnd = msg.hwnd == myHwnd
                ? myHwnd
                : NativeMethods.GetAncestor(msg.hwnd, NativeMethods.GA_ROOT);
            if (rootHwnd != myHwnd)
                return;

            // Intercept Mouse Side Buttons (XBUTTON1 = Back, XBUTTON2 = Forward) over both native ExplorerBrowser and WPF chrome
            if (msg.message == NativeMethods.WM_XBUTTONDOWN ||
                msg.message == NativeMethods.WM_NCXBUTTONDOWN ||
                msg.message == NativeMethods.WM_XBUTTONUP ||
                msg.message == NativeMethods.WM_NCXBUTTONUP)
            {
                int xButton = (int)((msg.wParam.ToInt64() >> 16) & 0xFFFF);
                if (xButton == NativeMethods.XBUTTON1 || xButton == NativeMethods.XBUTTON2)
                {
                    if (msg.message == NativeMethods.WM_XBUTTONUP || msg.message == NativeMethods.WM_NCXBUTTONUP)
                    {
                        HandleMouseSideButton(xButton);
                    }
                    handled = true;
                    return;
                }
            }

            if (msg.message != NativeMethods.WM_KEYDOWN && msg.message != NativeMethods.WM_SYSKEYDOWN)
                return;

            int vk = (int)msg.wParam.ToInt64();
            ModifierKeys mods = Keyboard.Modifiers;
            bool inWpfTextBox = AddressTextBox.IsKeyboardFocusWithin ||
                                SearchTextBox.IsKeyboardFocusWithin ||
                                IsPreviewContentFocused();

            if (mods == ModifierKeys.Control)
            {
                if (vk == 0x0D && !inWpfTextBox && _selectedTab?.BrowserHost.IsShowingSearchResults == true) // Ctrl + Enter
                {
                    _ = OpenSelectedSearchItemLocationAsync();
                    handled = true;
                    return;
                }
                if (vk == 'T')
                {
                    OpenNewThisPcTab();
                    handled = true;
                    return;
                }
                if (vk == 'N')
                {
                    _ = OpenNewWindowFromShortcutAsync();
                    handled = true;
                    return;
                }
                if (vk == 'W')
                {
                    if (_selectedTab != null) CloseTab(_selectedTab);
                    handled = true;
                    return;
                }
                if (vk == 'L')
                {
                    EnterAddressEditMode();
                    handled = true;
                    return;
                }
                if (vk == 'F' || vk == 'E')
                {
                    FocusSearchBox();
                    handled = true;
                    return;
                }
                if (vk == 'D')
                {
                    ToggleBookmarkForCurrentFolder();
                    handled = true;
                    return;
                }
                if (vk == 0xC0) // Ctrl + ` (OEM_3)
                {
                    OpenTerminalInActiveTab();
                    handled = true;
                    return;
                }
                if (vk == 0x09) // Tab
                {
                    SwitchRelativeTab(+1);
                    handled = true;
                    return;
                }
            }
            else if (mods == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                if (vk == 0x09) // Ctrl+Shift+Tab
                {
                    SwitchRelativeTab(-1);
                    handled = true;
                    return;
                }
                if (vk == 'T')
                {
                    ReopenLastClosedTab();
                    handled = true;
                    return;
                }
                if (vk == 'B')
                {
                    ToggleBookmarkBar();
                    handled = true;
                    return;
                }
                if (vk == 'N' && !inWpfTextBox)
                {
                    _ = CreateNewItemInActiveTabAsync(NewItemKind.Folder);
                    handled = true;
                    return;
                }
                if (vk == 'C' && !inWpfTextBox)
                {
                    CopySelectedItemPathsToClipboard();
                    handled = true;
                    return;
                }
            }
            else if (mods == ModifierKeys.Alt)
            {
                if (vk == 'D')
                {
                    EnterAddressEditMode();
                    handled = true;
                    return;
                }
                if (vk == 0x25 && _selectedTab != null && !inWpfTextBox) // Alt+Left
                {
                    _selectedTab.NavigateHistoryStep(-1);
                    UpdateNavigationButtonsState();
                    handled = true;
                    return;
                }
                if (vk == 0x27 && _selectedTab != null && !inWpfTextBox) // Alt+Right
                {
                    _selectedTab.NavigateHistoryStep(+1);
                    UpdateNavigationButtonsState();
                    handled = true;
                    return;
                }
                if (vk == 0x26 && _selectedTab != null && !inWpfTextBox) // Alt+Up
                {
                    _selectedTab.BrowserHost.NavigateUp();
                    handled = true;
                    return;
                }
                if (vk == 0x20) // Alt+Space
                {
                    WindowBackdropHelper.ShowSystemMenuAtScreenPoint(this, PointToScreen(new Point(14, TabStripHeightDip)));
                    handled = true;
                    return;
                }
            }
            else if (mods == ModifierKeys.None && !inWpfTextBox)
            {
                if (vk == 0x74 && _selectedTab != null) // F5
                {
                    RefreshActiveTab(focusNativeView: false);
                    handled = true;
                    return;
                }
                if (vk == 0x73) // F4
                {
                    EnterAddressEditMode();
                    handled = true;
                    return;
                }
            }

            if (!inWpfTextBox && _selectedTab != null && _selectedTab.BrowserHost.TryTranslateShellAccelerator(ref msg))
            {
                handled = true;
            }
        }

        private void SwitchRelativeTab(int delta)
        {
            if (_tabs.Count <= 1 || _selectedTab == null)
                return;

            int current = _tabs.IndexOf(_selectedTab);
            int next = (current + delta % _tabs.Count + _tabs.Count) % _tabs.Count;
            ActivateTab(_tabs[next], focusNativeView: true);
        }

        public ExplorerTabItem OpenNewThisPcTab()
        {
            if (_cachedThisPcPidl != IntPtr.Zero)
            {
                return AddNewTab(_cachedThisPcPidl, activate: true, animateOpen: !_skipSessionPersistence);
            }

            IntPtr pidl = PidlManager.CreateThisPcPidl();
            try
            {
                return AddNewTab(pidl, activate: true, animateOpen: !_skipSessionPersistence);
            }
            finally
            {
                PidlManager.Free(ref pidl);
            }
        }

        // ==================== TAB GESTURES, DRAG TEAR-OUT & RIGHT-CLICK MENU ====================

        private void TabPill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ExplorerTabItem tab)
            {
                if (e.ClickCount == 2)
                {
                    _tabReorderCandidate = null;
                    _isReorderingTab = false;
                    _isTabTearOutArmed = false;
                    fe.Cursor = null;
                    e.Handled = true;
                    CloseTab(tab);
                    return;
                }

                ActivateTab(tab, focusNativeView: true);
                _tabReorderCandidate = tab;
                _tabReorderStartPt = e.GetPosition(TabsItemsControl);
                _isReorderingTab = false;
                _isTabTearOutArmed = false;
                fe.CaptureMouse();
                e.Handled = true;
            }
        }

        private void TabPill_MouseMove(object sender, MouseEventArgs e)
        {
            if (_tabReorderCandidate == null || e.LeftButton != MouseButtonState.Pressed)
                return;

            Point currentPt = e.GetPosition(TabsItemsControl);

            bool outsideVertically = _tabs.Count > 1 &&
                                     (currentPt.Y < -36.0 || currentPt.Y > TabStripHeightDip + 42.0);
            if (outsideVertically != _isTabTearOutArmed)
            {
                _isTabTearOutArmed = outsideVertically;
                if (sender is FrameworkElement pillFe)
                {
                    pillFe.Cursor = outsideVertically ? Cursors.SizeAll : null;
                }
                if (outsideVertically)
                {
                    SetTransientStatus("松开鼠标即可将此标签页拆分为独立新窗口");
                }
            }

            if (outsideVertically)
                return;

            if (!_isReorderingTab && Math.Abs(currentPt.X - _tabReorderStartPt.X) >= 10.0)
            {
                _isReorderingTab = true;
            }

            if (!_isReorderingTab || _tabs.Count <= 1)
                return;

            int currentIdx = _tabs.IndexOf(_tabReorderCandidate);
            if (currentIdx < 0)
                return;

            for (int i = 0; i < _tabs.Count; i++)
            {
                if (i == currentIdx)
                    continue;

                if (TabsItemsControl.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement container &&
                    container.ActualWidth > 0)
                {
                    Point topLeft = container.TranslatePoint(new Point(0, 0), TabsItemsControl);
                    double midX = topLeft.X + container.ActualWidth * 0.5;

                    if ((i < currentIdx && currentPt.X < midX) ||
                        (i > currentIdx && currentPt.X > midX))
                    {
                        _tabs.Move(currentIdx, i);
                        ScheduleSessionSave();
                        break;
                    }
                }
            }
        }

        private void TabPill_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            ExplorerTabItem? candidate = _tabReorderCandidate;
            bool shouldTearOut = _isTabTearOutArmed && candidate != null && _tabs.Count > 1;

            if (sender is FrameworkElement fe)
            {
                fe.Cursor = null;
                if (fe.IsMouseCaptured)
                {
                    fe.ReleaseMouseCapture();
                }
            }

            _tabReorderCandidate = null;
            _isReorderingTab = false;
            _isTabTearOutArmed = false;

            if (shouldTearOut && candidate != null)
            {
                Point? dropTopLeftDip = null;
                if (NativeMethods.GetCursorPos(out POINT cursorPx))
                {
                    DpiScale dpi = VisualTreeHelper.GetDpi(this);
                    double dipX = cursorPx.X / Math.Max(0.1, dpi.DpiScaleX) - 96.0;
                    double dipY = cursorPx.Y / Math.Max(0.1, dpi.DpiScaleY) - 20.0;
                    dropTopLeftDip = new Point(dipX, dipY);
                }

                e.Handled = true;
                _ = MoveTabToNewWindowAsync(candidate, dropTopLeftDip);
            }
        }

        private void TabPill_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not ExplorerTabItem tab)
                return;

            e.Handled = true;
            int tabIdx = _tabs.IndexOf(tab);
            var menu = CreateLiquidGlassContextMenu(fe);

            var miNewTab = new MenuItem { Header = "新建标签页", InputGestureText = "Ctrl+T" };
            miNewTab.Click += (_, _) => OpenNewThisPcTab();
            menu.Items.Add(miNewTab);

            var miDuplicate = new MenuItem { Header = "复制标签页" };
            miDuplicate.Click += (_, _) => DuplicateTab(tab);
            menu.Items.Add(miDuplicate);

            var miMoveToWin = new MenuItem
            {
                Header = "移动到新窗口",
                IsEnabled = _tabs.Count > 1
            };
            miMoveToWin.Click += async (_, _) => await MoveTabToNewWindowAsync(tab);
            menu.Items.Add(miMoveToWin);

            var miOpenInWin = new MenuItem
            {
                Header = "在新窗口中打开",
                InputGestureText = "Ctrl+N"
            };
            miOpenInWin.Click += async (_, _) =>
            {
                if (App.CurrentApp != null)
                {
                    await App.CurrentApp.OpenNewIndependentWindowAsync(
                        tab.CurrentPidl,
                        historySnapshot: null,
                        cascadeFrom: this);
                }
            };
            menu.Items.Add(miOpenInWin);

            menu.Items.Add(CreateLiquidGlassMenuSeparator());

            var miCopyPath = new MenuItem
            {
                Header = "复制文件夹路径",
                IsEnabled = !string.IsNullOrWhiteSpace(tab.AddressText)
            };
            miCopyPath.Click += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(tab.AddressText))
                {
                    try
                    {
                        Clipboard.SetText(tab.AddressText);
                        SetTransientStatus($"已复制目录路径：{tab.AddressText}");
                    }
                    catch { }
                }
            };
            menu.Items.Add(miCopyPath);

            var miOpenTerminal = new MenuItem
            {
                Header = "在此处打开终端",
                InputGestureText = "Ctrl+`",
                IsEnabled = !string.IsNullOrWhiteSpace(tab.FileSystemPath)
            };
            miOpenTerminal.Click += (_, _) => OpenTerminalAtPath(tab.FileSystemPath);
            menu.Items.Add(miOpenTerminal);

            menu.Items.Add(CreateLiquidGlassMenuSeparator());

            var miClose = new MenuItem { Header = "关闭标签页", InputGestureText = "Ctrl+W" };
            miClose.Click += (_, _) => CloseTab(tab);
            menu.Items.Add(miClose);

            var miCloseRight = new MenuItem
            {
                Header = "关闭右侧标签页",
                IsEnabled = tabIdx >= 0 && tabIdx < _tabs.Count - 1
            };
            miCloseRight.Click += (_, _) => CloseTabsToRight(tab);
            menu.Items.Add(miCloseRight);

            var miCloseOthers = new MenuItem
            {
                Header = "关闭其他标签页",
                IsEnabled = _tabs.Count > 1
            };
            miCloseOthers.Click += (_, _) => CloseOtherTabs(tab);
            menu.Items.Add(miCloseOthers);

            menu.IsOpen = true;
        }

        private void TabPill_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle &&
                sender is FrameworkElement fe &&
                fe.DataContext is ExplorerTabItem tab)
            {
                e.Handled = true;
                CloseTab(tab);
            }
        }

        private void TabStrip_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_tabs.Count <= 1)
                return;

            if (TabsScrollViewer.ScrollableWidth > 0)
                TabsScrollViewer.ScrollToHorizontalOffset(TabsScrollViewer.HorizontalOffset - e.Delta);
            else
                SwitchRelativeTab(e.Delta > 0 ? -1 : +1);
            e.Handled = true;
        }

        private void CloseTabButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ExplorerTabItem tab)
            {
                CloseTab(tab);
                e.Handled = true;
            }
        }

        private void NewTabButton_Click(object sender, RoutedEventArgs e) => OpenNewThisPcTab();

        // ==================== CROSS-TAB FILE DRAG HOVER-TO-SWITCH ====================

        private void TabPill_DragEnter(object sender, DragEventArgs e) => HandleTabDragHover(sender, e);
        private void TabPill_DragOver(object sender, DragEventArgs e) => HandleTabDragHover(sender, e);

        private void HandleTabDragHover(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;

            if (sender is not FrameworkElement fe || fe.DataContext is not ExplorerTabItem targetTab)
                return;

            if (ReferenceEquals(targetTab, _selectedTab))
            {
                ClearDragHoverState();
                return;
            }

            if (!ReferenceEquals(_pendingDragHoverTab, targetTab))
            {
                ClearDragHoverState();
                _pendingDragHoverTab = targetTab;
                _pendingDragHoverTab.IsDragHoverTarget = true;
                _dragHoverSwitchTimer.Stop();
                _dragHoverSwitchTimer.Start();
            }
        }

        private void TabPill_DragLeave(object sender, DragEventArgs e)
        {
            if (sender is FrameworkElement fe &&
                fe.DataContext is ExplorerTabItem tab &&
                ReferenceEquals(tab, _pendingDragHoverTab))
            {
                ClearDragHoverState();
            }
        }

        private void TabPill_Drop(object sender, DragEventArgs e)
        {
            ClearDragHoverState();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        private void DragHoverSwitchTimer_Tick(object? sender, EventArgs e)
        {
            _dragHoverSwitchTimer.Stop();
            ExplorerTabItem? target = _pendingDragHoverTab;
            ClearDragHoverState();

            if (target != null && !ReferenceEquals(target, _selectedTab))
            {
                ActivateTab(target, focusNativeView: false);
                SetTransientStatus($"已切换至「{target.Title}」，请移入下方列表松手完成拖放");
            }
        }

        private void ClearDragHoverState()
        {
            _dragHoverSwitchTimer.Stop();
            if (_pendingDragHoverTab != null)
            {
                _pendingDragHoverTab.IsDragHoverTarget = false;
                _pendingDragHoverTab = null;
            }
        }

        // ==================== LIQUID GLASS COMMAND BAR ACTIONS & DROPDOWNS ====================

        private ContextMenu CreateLiquidGlassContextMenu(UIElement placementTarget)
        {
            var menu = new ContextMenu
            {
                Style = (Style)FindResource("LiquidGlassContextMenuStyle"),
                PlacementTarget = placementTarget,
                Placement = PlacementMode.Bottom,
                VerticalOffset = 4
            };

            menu.Closed += (_, _) =>
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    if (!_isClosingWindow &&
                        !AddressTextBox.IsKeyboardFocusWithin &&
                        !SearchTextBox.IsKeyboardFocusWithin &&
                        !IsPreviewContentFocused())
                    {
                        _selectedTab?.BrowserHost.FocusNativeView();
                    }
                }));
            };

            return menu;
        }

        private Separator CreateLiquidGlassMenuSeparator() =>
            new Separator { Style = (Style)FindResource("LiquidGlassMenuSeparatorStyle") };

        private static Image? CreateMenuIcon(ImageSource? source)
        {
            if (source == null)
                return null;

            var img = new Image
            {
                Width = 16,
                Height = 16,
                Source = source
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }

        private void NewItemDropdownButton_Click(object sender, RoutedEventArgs e)
        {
            var menu = CreateLiquidGlassContextMenu(NewItemDropdownButton);

            var miFolder = new MenuItem
            {
                Header = "文件夹",
                InputGestureText = "Ctrl+Shift+N",
                Icon = CreateMenuIcon(PidlManager.ExtractTypeSmallIcon("folder", isDirectory: true))
            };
            miFolder.Click += async (_, _) => await CreateNewItemInActiveTabAsync(NewItemKind.Folder);
            menu.Items.Add(miFolder);

            menu.Items.Add(CreateLiquidGlassMenuSeparator());

            var miTxt = new MenuItem
            {
                Header = "文本文档 (.txt)",
                Icon = CreateMenuIcon(PidlManager.ExtractTypeSmallIcon(".txt", isDirectory: false))
            };
            miTxt.Click += async (_, _) => await CreateNewItemInActiveTabAsync(NewItemKind.TextDocument);
            menu.Items.Add(miTxt);

            var miMd = new MenuItem
            {
                Header = "Markdown 文档 (.md)",
                Icon = CreateMenuIcon(PidlManager.ExtractTypeSmallIcon(".md", isDirectory: false))
            };
            miMd.Click += async (_, _) => await CreateNewItemInActiveTabAsync(NewItemKind.MarkdownDocument);
            menu.Items.Add(miMd);

            var miZip = new MenuItem
            {
                Header = "压缩文件夹 (.zip)",
                Icon = CreateMenuIcon(PidlManager.ExtractTypeSmallIcon(".zip", isDirectory: false))
            };
            miZip.Click += async (_, _) => await CreateNewItemInActiveTabAsync(NewItemKind.ZipArchive);
            menu.Items.Add(miZip);

            menu.IsOpen = true;
        }

        private async Task CreateNewItemInActiveTabAsync(NewItemKind kind)
        {
            if (_selectedTab == null)
                return;

            (bool ok, string? msg) = await _selectedTab.BrowserHost.TryCreateNewItemAsync(kind, _selectedTab.FileSystemPath);
            if (!string.IsNullOrEmpty(msg))
            {
                SetTransientStatus(msg!);
            }
            else if (!ok)
            {
                SetTransientStatus("当前位置不支持新建项目");
            }
            UpdateCommandBarAndStatus(computeBytes: false);
        }

        private void CutButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTab != null && _selectedTab.BrowserHost.TryInvokeSelectionVerb("cut"))
            {
                SetTransientStatus("已剪切选中项目");
                UpdateCommandBarAndStatus(computeBytes: false);
                _selectedTab.BrowserHost.FocusNativeView();
            }
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTab != null && _selectedTab.BrowserHost.TryInvokeSelectionVerb("copy"))
            {
                SetTransientStatus("已复制选中项目");
                UpdateCommandBarAndStatus(computeBytes: false);
                _selectedTab.BrowserHost.FocusNativeView();
            }
        }

        private void PasteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTab != null && _selectedTab.BrowserHost.TryInvokeBackgroundVerb("paste"))
            {
                UpdateCommandBarAndStatus(computeBytes: false);
                _selectedTab.BrowserHost.FocusNativeView();
            }
        }

        private void RenameButton_Click(object sender, RoutedEventArgs e)
        {
            _selectedTab?.BrowserHost.TryBeginInlineRename();
        }

        private void ShareCopyPathButton_Click(object sender, RoutedEventArgs e)
        {
            CopySelectedItemPathsToClipboard();
            _selectedTab?.BrowserHost.FocusNativeView();
        }

        private void CopySelectedItemPathsToClipboard()
        {
            if (_selectedTab == null)
                return;

            List<string> paths = _selectedTab.BrowserHost.GetSelectedPaths();
            if (paths.Count == 0)
                return;

            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, paths));
                SetTransientStatus(paths.Count == 1
                    ? $"已复制路径：{paths[0]}"
                    : $"已复制 {paths.Count} 个项目的路径");
            }
            catch
            {
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTab != null && _selectedTab.BrowserHost.TryInvokeSelectionVerb("delete"))
            {
                UpdateCommandBarAndStatus(computeBytes: false);
                _selectedTab.BrowserHost.FocusNativeView();
            }
        }

        private void SortDropdownButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTab == null)
                return;

            (PROPERTYKEY curKey, int curDir) = _selectedTab.BrowserHost.GetCurrentSortState();
            var menu = CreateLiquidGlassContextMenu(SortDropdownButton);

            void AddSortKeyItem(string title, PROPERTYKEY key)
            {
                var mi = new MenuItem
                {
                    Header = title,
                    IsChecked = curKey.Equals(key)
                };
                mi.Click += (_, _) =>
                {
                    _selectedTab?.BrowserHost.TrySetSortState(key, null);
                    _selectedTab?.BrowserHost.FocusNativeView();
                };
                menu.Items.Add(mi);
            }

            AddSortKeyItem("名称", PropertyKeys.ItemNameDisplay);
            if (_selectedTab.BrowserHost.IsShowingSearchResults)
            {
                AddSortKeyItem("所在文件夹", PropertyKeys.ItemFolderPathDisplay);
            }
            AddSortKeyItem("修改日期", PropertyKeys.DateModified);
            AddSortKeyItem("类型", PropertyKeys.ItemTypeText);
            AddSortKeyItem("大小", PropertyKeys.Size);

            menu.Items.Add(CreateLiquidGlassMenuSeparator());

            var miAsc = new MenuItem { Header = "递增", IsChecked = curDir == ShellConstants.SORT_ASCENDING };
            miAsc.Click += (_, _) =>
            {
                _selectedTab?.BrowserHost.TrySetSortState(null, ShellConstants.SORT_ASCENDING);
                _selectedTab?.BrowserHost.FocusNativeView();
            };
            menu.Items.Add(miAsc);

            var miDesc = new MenuItem { Header = "递减", IsChecked = curDir == ShellConstants.SORT_DESCENDING };
            miDesc.Click += (_, _) =>
            {
                _selectedTab?.BrowserHost.TrySetSortState(null, ShellConstants.SORT_DESCENDING);
                _selectedTab?.BrowserHost.FocusNativeView();
            };
            menu.Items.Add(miDesc);

            menu.IsOpen = true;
        }

        private void ViewDropdownButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTab == null)
                return;

            ExplorerViewStyle curStyle = _selectedTab.BrowserHost.GetCurrentViewStyle();
            (bool showExt, bool showHidden) = PidlManager.GetShellVisibilityFlags();

            var menu = CreateLiquidGlassContextMenu(ViewDropdownButton);

            void AddStyleItem(string label, ExplorerViewStyle style)
            {
                var mi = new MenuItem
                {
                    Header = label,
                    IsChecked = curStyle == style
                };
                mi.Click += (_, _) =>
                {
                    if (_selectedTab != null && _selectedTab.BrowserHost.TrySetViewStyle(style))
                    {
                        UpdateViewModeIndicator();
                        _selectedTab.BrowserHost.FocusNativeView();
                    }
                };
                menu.Items.Add(mi);
            }

            AddStyleItem("超大图标", ExplorerViewStyle.ExtraLargeIcons);
            AddStyleItem("大图标", ExplorerViewStyle.LargeIcons);
            AddStyleItem("中等图标", ExplorerViewStyle.MediumIcons);
            AddStyleItem("小图标", ExplorerViewStyle.SmallIcons);
            AddStyleItem("列表", ExplorerViewStyle.List);
            AddStyleItem("详细信息", ExplorerViewStyle.Details);
            AddStyleItem("平铺", ExplorerViewStyle.Tiles);
            AddStyleItem("内容", ExplorerViewStyle.Content);

            menu.Items.Add(CreateLiquidGlassMenuSeparator());

            var miExt = new MenuItem { Header = "文件扩展名", IsChecked = showExt };
            miExt.Click += (_, _) =>
            {
                PidlManager.SetShellVisibilityFlags(showExtensions: !showExt, showHidden: null);
                RefreshAllTabs();
                SetTransientStatus(!showExt ? "已显示文件扩展名" : "已隐藏文件扩展名");
            };
            menu.Items.Add(miExt);

            var miHidden = new MenuItem { Header = "隐藏的项目", IsChecked = showHidden };
            miHidden.Click += (_, _) =>
            {
                PidlManager.SetShellVisibilityFlags(showExtensions: null, showHidden: !showHidden);
                RefreshAllTabs();
                SetTransientStatus(!showHidden ? "已显示隐藏的项目" : "已不显示隐藏的项目");
            };
            menu.Items.Add(miHidden);

            menu.Items.Add(CreateLiquidGlassMenuSeparator());

            var miBookmarkBar = new MenuItem
            {
                Header = "书签栏",
                InputGestureText = "Ctrl+Shift+B",
                IsChecked = _showBookmarkBar
            };
            miBookmarkBar.Click += (_, _) => ToggleBookmarkBar();
            menu.Items.Add(miBookmarkBar);

            var miPreview = new MenuItem
            {
                Header = "预览窗格",
                IsChecked = _showPreviewPane
            };
            miPreview.Click += (_, _) => TogglePreviewPane();
            menu.Items.Add(miPreview);

            menu.IsOpen = true;
        }

        private void MoreMenuButton_Click(object sender, RoutedEventArgs e)
        {
            var menu = CreateLiquidGlassContextMenu(MoreMenuButton);

            if (_selectedTab?.BrowserHost.IsShowingSearchResults == true)
            {
                var miOpenLoc = new MenuItem
                {
                    Header = "打开文件所在位置",
                    InputGestureText = "Ctrl+Enter",
                    IsEnabled = _selectedTab.BrowserHost.GetItemCounts().SelectedItems == 1
                };
                miOpenLoc.Click += async (_, _) => await OpenSelectedSearchItemLocationAsync();
                menu.Items.Add(miOpenLoc);
                menu.Items.Add(CreateLiquidGlassMenuSeparator());
            }

            var miSelectAll = new MenuItem { Header = "全选", InputGestureText = "Ctrl+A" };
            miSelectAll.Click += (_, _) =>
            {
                _selectedTab?.BrowserHost.SelectAllItems();
                UpdateCommandBarAndStatus(computeBytes: true);
            };
            menu.Items.Add(miSelectAll);

            var miClearSel = new MenuItem { Header = "取消全部选择" };
            miClearSel.Click += (_, _) =>
            {
                _selectedTab?.BrowserHost.ClearSelection();
                UpdateCommandBarAndStatus(computeBytes: false);
            };
            menu.Items.Add(miClearSel);

            menu.Items.Add(CreateLiquidGlassMenuSeparator());

            var miAddBm = new MenuItem { Header = "收藏当前目录到书签栏", InputGestureText = "Ctrl+D" };
            miAddBm.Click += (_, _) => ToggleBookmarkForCurrentFolder();
            menu.Items.Add(miAddBm);

            var miToggleBmBar = new MenuItem
            {
                Header = _showBookmarkBar ? "隐藏书签栏" : "显示书签栏",
                InputGestureText = "Ctrl+Shift+B"
            };
            miToggleBmBar.Click += (_, _) => ToggleBookmarkBar();
            menu.Items.Add(miToggleBmBar);

            var miReopen = new MenuItem
            {
                Header = "重新打开关闭的标签页",
                InputGestureText = "Ctrl+Shift+T",
                IsEnabled = _closedTabs.Count > 0
            };
            miReopen.Click += (_, _) => ReopenLastClosedTab();
            menu.Items.Add(miReopen);

            menu.Items.Add(CreateLiquidGlassMenuSeparator());

            var miOpenNewWin = new MenuItem
            {
                Header = "打开新窗口",
                InputGestureText = "Ctrl+N"
            };
            miOpenNewWin.Click += async (_, _) => await OpenNewWindowFromShortcutAsync();
            menu.Items.Add(miOpenNewWin);

            var miOpenTerminal = new MenuItem
            {
                Header = "在此处打开终端",
                InputGestureText = "Ctrl+`",
                IsEnabled = _selectedTab != null && !string.IsNullOrWhiteSpace(_selectedTab.FileSystemPath)
            };
            miOpenTerminal.Click += (_, _) => OpenTerminalInActiveTab();
            menu.Items.Add(miOpenTerminal);

            var miCopyFolder = new MenuItem { Header = "复制当前目录路径" };
            miCopyFolder.Click += (_, _) =>
            {
                if (_selectedTab != null && !string.IsNullOrWhiteSpace(_selectedTab.AddressText))
                {
                    try
                    {
                        Clipboard.SetText(_selectedTab.AddressText);
                        SetTransientStatus($"已复制目录路径：{_selectedTab.AddressText}");
                    }
                    catch { }
                }
            };
            menu.Items.Add(miCopyFolder);

            var miProps = new MenuItem { Header = "属性", InputGestureText = "Alt+Enter" };
            miProps.Click += (_, _) =>
            {
                if (_selectedTab == null) return;
                if (_selectedTab.BrowserHost.GetItemCounts().SelectedItems > 0)
                    _selectedTab.BrowserHost.TryInvokeSelectionVerb("properties");
                else
                    _selectedTab.BrowserHost.TryInvokeBackgroundVerb("properties");
            };
            menu.Items.Add(miProps);

            menu.Items.Add(CreateLiquidGlassMenuSeparator());

            bool ctxEnabled = ShellIntegrationService.IsContextMenuEnabled();
            var miContextMenu = new MenuItem
            {
                Header = "右键菜单添加「在 LeafTab 中打开」",
                IsChecked = ctxEnabled
            };
            miContextMenu.Click += (_, _) =>
            {
                var res = ShellIntegrationService.SetContextMenuEnabled(!ctxEnabled);
                SetTransientStatus(res.Message, durationMs: 3600);
            };
            menu.Items.Add(miContextMenu);

            bool defExplorerEnabled = ShellIntegrationService.IsDefaultExplorerEnabled();
            var miDefaultExplorer = new MenuItem
            {
                Header = "设为默认资源管理器（接管文件夹与 Win+E）",
                IsChecked = defExplorerEnabled
            };
            miDefaultExplorer.Click += (_, _) =>
            {
                if (!defExplorerEnabled)
                {
                    string currentExe = ShellIntegrationService.GetCurrentExecutablePath();
                    if (QuickLaunchAndInstallService.IsVolatileLaunchLocation(currentExe))
                    {
                        string fixedExe = QuickLaunchAndInstallService.GetRecommendedInstallExePath();
                        MessageBoxResult choice = MessageBox.Show(
                            this,
                            $"检测到当前 LeafTab 正在临时或易清理目录中运行：\n{currentExe}\n\n" +
                            "如果直接在此位置设为默认资源管理器，日后清理「下载 / 桌面 / 临时文件夹」删除该文件后，会导致系统文件夹无法打开。\n\n" +
                            $"是否自动将 LeafTab 安装到固定目录并设为默认？（推荐）\n固定目录：{fixedExe}\n\n" +
                            "• 点击「是」：自动复制到固定目录、创建开始菜单快捷方式并无缝切换\n" +
                            "• 点击「否」：仍在当前位置直接注册\n" +
                            "• 点击「取消」：暂不设置",
                            "LeafTab 默认资源管理器安全提示",
                            MessageBoxButton.YesNoCancel,
                            MessageBoxImage.Question,
                            MessageBoxResult.Yes);

                        if (choice == MessageBoxResult.Cancel)
                            return;

                        if (choice == MessageBoxResult.Yes)
                        {
                            SaveConfigurationSnapshot();
                            if ((_sharedStore != null && !_sharedStore.Flush()) || !AppConfigService.PrepareInstalledConfig())
                            {
                                SetTransientStatus("配置迁移未完成，已保留当前程序和书签，请稍后重试。", 5000);
                                return;
                            }
                            if (!QuickLaunchAndInstallService.InstallToFixedLocation(
                                    currentExe,
                                    out string installedExe,
                                    out string? copyErr))
                            {
                                MessageBox.Show(
                                    this,
                                    $"复制到固定目录失败：{copyErr}",
                                    "LeafTab",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Warning);
                                return;
                            }

                            if (ctxEnabled)
                            {
                                ShellIntegrationService.SetContextMenuEnabledForExe(true, installedExe);
                            }

                            var installRes = ShellIntegrationService.SetDefaultExplorerEnabledForExe(true, installedExe);
                            if (!installRes.Ok)
                            {
                                SetTransientStatus(installRes.Message, durationMs: 4500);
                                return;
                            }

                            SaveConfigurationSnapshot();
                            App.CurrentApp?.HandoffToFixedInstallationAndExit(installedExe);
                            return;
                        }
                    }
                }

                var res = ShellIntegrationService.SetDefaultExplorerEnabled(!defExplorerEnabled);
                SetTransientStatus(res.Message, durationMs: 4200);
            };
            menu.Items.Add(miDefaultExplorer);

            var miWarmStandby = new MenuItem
            {
                Header = "关闭窗口后驻留极速热启动",
                IsChecked = _enableWarmStandby
            };
            miWarmStandby.Click += (_, _) =>
            {
                _enableWarmStandby = !_enableWarmStandby;
                SaveConfigurationSnapshot();
                SetTransientStatus(_enableWarmStandby
                    ? "已开启关闭窗口后后台极速热启动（打开文件夹毫秒级响应）"
                    : "已关闭后台热启动（关闭窗口后将完全退出进程）");
            };
            menu.Items.Add(miWarmStandby);

            var miRestoreTabs = new MenuItem
            {
                Header = "启动时恢复上次打开的标签页",
                IsChecked = _restorePreviousTabs
            };
            miRestoreTabs.Click += (_, _) =>
            {
                _restorePreviousTabs = !_restorePreviousTabs;
                SaveConfigurationSnapshot();
                SetTransientStatus(_restorePreviousTabs
                    ? "已开启启动时自动恢复上次打开的标签页"
                    : "已关闭启动时恢复标签页（直接启动将打开「此电脑」）");
            };
            menu.Items.Add(miRestoreTabs);

            menu.Items.Add(CreateLiquidGlassMenuSeparator());

            var miOpenDiagLogs = new MenuItem
            {
                Header = "打开诊断日志目录"
            };
            miOpenDiagLogs.Click += async (_, _) =>
            {
                try
                {
                    Directory.CreateDirectory(DiagnosticLog.DirectoryPath);
                    (IntPtr logPidl, _) = await PidlManager.TryParseAddressAsync(DiagnosticLog.DirectoryPath, CancellationToken.None);
                    if (logPidl != IntPtr.Zero)
                    {
                        try
                        {
                            AddNewTab(logPidl, activate: true, animateOpen: !_skipSessionPersistence);
                            SetTransientStatus($"已打开诊断日志目录：{DiagnosticLog.DirectoryPath}");
                        }
                        finally
                        {
                            PidlManager.Free(ref logPidl);
                        }
                    }
                }
                catch (Exception ex)
                {
                    SetTransientStatus($"无法打开诊断日志目录：{ex.Message}");
                }
            };
            menu.Items.Add(miOpenDiagLogs);

            string appVer = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.6.4";
            var miAboutVer = new MenuItem
            {
                Header = $"版本 LeafTab v{appVer} (x64)",
                IsEnabled = false
            };
            menu.Items.Add(miAboutVer);

            menu.Items.Add(CreateLiquidGlassMenuSeparator());

            var miQuitAll = new MenuItem
            {
                Header = "彻底退出 LeafTab"
            };
            miQuitAll.Click += (_, _) => ForceExitApplication();
            menu.Items.Add(miQuitAll);

            menu.IsOpen = true;
        }

        private void RefreshAllTabs()
        {
            _lastPreviewSelectionKey = string.Empty;
            foreach (ExplorerTabItem tab in _tabs)
            {
                tab.BrowserHost.RefreshView();
            }
            UpdateCommandBarAndStatus(computeBytes: true);
            if (_showPreviewPane && _selectedTab != null)
            {
                SchedulePostRefreshPreviewSync(_selectedTab);
            }
        }

        private void DetailsViewButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTab != null && _selectedTab.BrowserHost.TrySetViewStyle(ExplorerViewStyle.Details))
            {
                UpdateViewModeIndicator();
                _selectedTab.BrowserHost.FocusNativeView();
            }
        }

        private void IconViewButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTab != null && _selectedTab.BrowserHost.TrySetViewStyle(ExplorerViewStyle.LargeIcons))
            {
                UpdateViewModeIndicator();
                _selectedTab.BrowserHost.FocusNativeView();
            }
        }

        private void UpdateViewModeIndicator()
        {
            if (_selectedTab == null)
                return;

            ExplorerViewStyle style = _selectedTab.BrowserHost.GetCurrentViewStyle();
            bool isIcons = style == ExplorerViewStyle.ExtraLargeIcons ||
                           style == ExplorerViewStyle.LargeIcons ||
                           style == ExplorerViewStyle.MediumIcons ||
                           style == ExplorerViewStyle.Tiles;

            Brush activeStroke = UiBrushes.Get(Color.FromRgb(0x00, 0x7A, 0xFF));
            Brush inactiveStroke = UiBrushes.Get(Color.FromRgb(0x64, 0x74, 0x8B));
            Brush activeBg = UiBrushes.Get(Color.FromArgb(0x22, 0x00, 0x7A, 0xFF));

            DetailsQuickGlyph.Stroke = isIcons ? inactiveStroke : activeStroke;
            IconsQuickGlyph.Stroke = isIcons ? activeStroke : inactiveStroke;
            DetailsQuickButton.Background = isIcons ? Brushes.Transparent : activeBg;
            IconsQuickButton.Background = isIcons ? activeBg : Brushes.Transparent;
        }

        // ==================== COLLAPSIBLE BOOKMARK BAR ====================

        public void ToggleBookmarkBar()
        {
            _showBookmarkBar = !_showBookmarkBar;
            BookmarkBarBorder.Visibility = _showBookmarkBar ? Visibility.Visible : Visibility.Collapsed;
            SyncGlassRegion();
            _selectedTab?.BrowserHost.SyncBrowserRect();
            _selectedTab?.BrowserHost.FocusNativeView();
            SetTransientStatus(_showBookmarkBar ? "已显示书签栏 (Ctrl+Shift+B)" : "已隐藏书签栏 (Ctrl+Shift+B)");
            ScheduleSessionSave();
        }

        private BookmarkItemViewModel? FindBookmarkMatching(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            foreach (BookmarkItemViewModel bm in _bookmarks)
            {
                if (string.Equals(bm.TargetPath, path, StringComparison.OrdinalIgnoreCase))
                    return bm;
            }
            return null;
        }

        private void UpdateBookmarkStarVisual()
        {
            if (_selectedTab == null)
                return;

            string curPath = _selectedTab.GetPersistablePath();
            _isCurrentFolderStarred = FindBookmarkMatching(curPath) != null;

            if (_isCurrentFolderStarred)
            {
                BookmarkStarGlyph.Stroke = UiBrushes.Get(Color.FromRgb(0x00, 0x7A, 0xFF));
                BookmarkStarGlyph.Fill = UiBrushes.Get(Color.FromRgb(0x00, 0x7A, 0xFF));
                BookmarkStarButton.ToolTip = "从书签栏移除此文件夹 (Ctrl+D)";
            }
            else
            {
                BookmarkStarGlyph.Stroke = UiBrushes.Get(Color.FromRgb(0x64, 0x74, 0x8B));
                BookmarkStarGlyph.Fill = Brushes.Transparent;
                BookmarkStarButton.ToolTip = "收藏此文件夹到书签栏 (Ctrl+D)";
            }

            UpdateBookmarkStarOpacity(animate: false);
        }

        private void BookmarkStarButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleBookmarkForCurrentFolder();
            _selectedTab?.BrowserHost.FocusNativeView();
        }

        private void ToggleBookmarkForCurrentFolder()
        {
            if (_selectedTab == null || _selectedTab.BrowserHost.IsShowingSearchResults)
                return;

            string targetPath = _selectedTab.GetPersistablePath();
            if (string.IsNullOrWhiteSpace(targetPath))
                return;

            BookmarkItemViewModel? existing = FindBookmarkMatching(targetPath);
            if (existing != null)
            {
                _bookmarks.Remove(existing);
                UpdateBookmarkStarVisual();
                SetTransientStatus($"已从书签栏移除：{existing.Title}");
                ScheduleSessionSave();
                return;
            }

            _bookmarks.Add(new BookmarkItemViewModel
            {
                Title = _selectedTab.Title,
                TargetPath = targetPath,
                Icon = _selectedTab.Icon
            });

            if (!_showBookmarkBar)
            {
                _showBookmarkBar = true;
                BookmarkBarBorder.Visibility = Visibility.Visible;
                SyncGlassRegion();
                _selectedTab.BrowserHost.SyncBrowserRect();
            }

            UpdateBookmarkStarVisual();
            SetTransientStatus($"已收藏到书签栏：{_selectedTab.Title}");
            ScheduleSessionSave();
        }

        private async void BookmarkItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is BookmarkItemViewModel bm)
            {
                await NavigateAddressInputAsync(bm.TargetPath);
            }
        }

        private async void BookmarkItem_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle &&
                sender is Button btn &&
                btn.Tag is BookmarkItemViewModel bm)
            {
                e.Handled = true;
                (IntPtr pidl, string? err) = await PidlManager.TryParseAddressAsync(bm.TargetPath, CancellationToken.None);
                if (pidl != IntPtr.Zero)
                {
                    try
                    {
                        AddNewTab(pidl, activate: false, animateOpen: !_skipSessionPersistence);
                        SetTransientStatus($"已在后台打开书签：{bm.Title}");
                    }
                    finally
                    {
                        PidlManager.Free(ref pidl);
                    }
                }
                else if (!string.IsNullOrEmpty(err))
                {
                    SetTransientStatus(err!);
                }
            }
        }

        private void BookmarkItem_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not BookmarkItemViewModel bm)
                return;

            e.Handled = true;
            var menu = CreateLiquidGlassContextMenu(btn);

            var miOpenNew = new MenuItem { Header = "在新标签页中打开" };
            miOpenNew.Click += async (_, _) =>
            {
                (IntPtr pidl, _) = await PidlManager.TryParseAddressAsync(bm.TargetPath, CancellationToken.None);
                if (pidl != IntPtr.Zero)
                {
                    try { AddNewTab(pidl, activate: true, animateOpen: !_skipSessionPersistence); }
                    finally { PidlManager.Free(ref pidl); }
                }
            };
            menu.Items.Add(miOpenNew);

            menu.Items.Add(CreateLiquidGlassMenuSeparator());

            var miRemove = new MenuItem { Header = "从书签栏移除" };
            miRemove.Click += (_, _) =>
            {
                _bookmarks.Remove(bm);
                UpdateBookmarkStarVisual();
                SetTransientStatus($"已移除书签：{bm.Title}");
                ScheduleSessionSave();
            };
            menu.Items.Add(miRemove);

            menu.IsOpen = true;
        }

        private void BookmarkBar_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effects = DragDropEffects.Link;
            else
                e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        private void BookmarkBar_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return;

            if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
            {
                int added = 0;
                foreach (string path in dropped)
                {
                    if (Directory.Exists(path) && FindBookmarkMatching(path) == null)
                    {
                        string title = System.IO.Path.GetFileName(path.TrimEnd('\\'));
                        if (string.IsNullOrWhiteSpace(title)) title = path;
                        _bookmarks.Add(AppConfigService.CreateBookmarkViewModel(title, path));
                        added++;
                    }
                }

                if (added > 0)
                {
                    UpdateBookmarkStarVisual();
                    SetTransientStatus($"已添加 {added} 个文件夹到书签栏");
                    ScheduleSessionSave();
                }
            }
            e.Handled = true;
        }

        // ==================== STATUS BAR & COMMAND BAR SELECTION SYNC ====================

        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void MaxRestoreButton_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void CloseWindowButton_Click(object sender, RoutedEventArgs e) => Close();

        public void SetTransientStatus(string message, int durationMs = 2800)
        {
            StatusTransientText.Text = message;
            _transientStatusUntilUtc = DateTime.UtcNow.AddMilliseconds(durationMs);
        }

        private void UpdateCommandBarAndStatus(bool computeBytes = true)
        {
            if (_isClosingWindow || _selectedTab == null)
                return;

            if (DateTime.UtcNow >= _transientStatusUntilUtc && !string.IsNullOrEmpty(StatusTransientText.Text))
            {
                StatusTransientText.Text = string.Empty;
            }

            ExplorerBrowserHost host = _selectedTab.BrowserHost;
            SelectionStatusInfo sel = host.GetSelectionStatus(computeBytes || _showPreviewPane);

            if (_showPreviewPane)
            {
                RefreshPreviewPaneForSelection(sel, host.GetCachedSingleSelection());
            }

            bool isRealFsFolder = !string.IsNullOrWhiteSpace(_selectedTab.FileSystemPath) && !host.IsShowingSearchResults;
            bool hasSelection = sel.SelectedItems > 0;

            NewItemDropdownButton.IsEnabled = isRealFsFolder;
            CutButton.IsEnabled = hasSelection;
            CopyButton.IsEnabled = hasSelection;
            RenameButton.IsEnabled = sel.SelectedItems == 1;
            ShareCopyPathButton.IsEnabled = hasSelection;
            DeleteButton.IsEnabled = hasSelection;
            PasteButton.IsEnabled = isRealFsFolder && CanPasteFromClipboardSafe();
            OpenItemLocationButton.Visibility = host.IsShowingSearchResults ? Visibility.Visible : Visibility.Collapsed;
            OpenItemLocationButton.IsEnabled = host.IsShowingSearchResults && sel.SelectedItems == 1;

            if (host.IsShowingSearchResults && host.IsSearchRunning)
            {
                SearchLoadMoreButton.Visibility = Visibility.Collapsed;
                StatusLeftText.Text = $"正在搜索… 已找到 {host.SearchMatchCount} 个项目";
                return;
            }

            SearchLoadMoreButton.Visibility = (host.IsShowingSearchResults && host.CanLoadMoreSearchResults)
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (sel.SelectedItems > 0)
            {
                StatusLeftText.Text = sel.HasSizedFileSelection
                    ? $"{sel.TotalItems} 个项目  ·  已选择 {sel.SelectedItems} 个项目（{PidlManager.FormatByteSize(sel.SelectedSizeBytes)}）"
                    : $"{sel.TotalItems} 个项目  ·  已选择 {sel.SelectedItems} 个项目";
            }
            else if (host.IsShowingSearchResults)
            {
                string backendLabel = host.LastSearchBackend switch
                {
                    SearchBackendKind.Everything => "Everything 极速索引",
                    SearchBackendKind.MemoryCache or SearchBackendKind.NativeFastScan => "极速扫描",
                    _ => string.Empty
                };

                string? notice = host.LastSearchNotice;
                if (sel.TotalItems == 0 && !string.IsNullOrWhiteSpace(notice))
                {
                    StatusLeftText.Text = notice!;
                }
                else if (host.IsSearchTruncated)
                {
                    string baseText = !string.IsNullOrEmpty(backendLabel)
                        ? $"已显示前 {sel.TotalItems} 个匹配项目  ·  {backendLabel} ({host.LastSearchElapsedMs} ms)"
                        : $"已显示前 {sel.TotalItems} 个匹配项目（可继续加载更多或输入更精确关键词）";
                    StatusLeftText.Text = !string.IsNullOrWhiteSpace(notice) ? $"{baseText}  ·  {notice}" : baseText;
                }
                else if (!string.IsNullOrEmpty(backendLabel))
                {
                    string baseText = $"{sel.TotalItems} 个项目  ·  {backendLabel} ({host.LastSearchElapsedMs} ms)";
                    StatusLeftText.Text = !string.IsNullOrWhiteSpace(notice) ? $"{baseText}  ·  {notice}" : baseText;
                }
                else
                {
                    StatusLeftText.Text = !string.IsNullOrWhiteSpace(notice)
                        ? $"{sel.TotalItems} 个项目  ·  {notice}"
                        : $"{sel.TotalItems} 个项目";
                }
            }
            else
            {
                StatusLeftText.Text = $"{sel.TotalItems} 个项目";
            }
        }

        private static uint _clipboardSequence = uint.MaxValue;
        private static bool _clipboardHasFiles;
        private static bool CanPasteFromClipboardSafe()
        {
            uint current = NativeMethods.GetClipboardSequenceNumber();
            if (current != 0 && current == _clipboardSequence) return _clipboardHasFiles;
            try
            {
                _clipboardHasFiles = Clipboard.ContainsFileDropList() || Clipboard.ContainsData("Shell IDList Array");
                _clipboardSequence = current;
                return _clipboardHasFiles;
            }
            catch { _clipboardSequence = uint.MaxValue; return false; }
        }

        public void PrepareForForceExit(bool skipPerWindowSave = false)
        {
            _forceExitProcess = true;
            if (skipPerWindowSave)
            {
                _skipSaveOnForceClose = true;
            }
        }

        public void ForceExitApplication()
        {
            _forceExitProcess = true;
            if (App.CurrentApp != null)
            {
                App.CurrentApp.ForceExitAllWindows();
                return;
            }
            Close();
            Application.Current?.Shutdown();
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_skipSessionPersistence || _forceExitProcess || !_enableWarmStandby)
                return;

            bool shouldStandby = App.CurrentApp?.ShouldWindowEnterWarmStandby(this) ?? true;
            if (!shouldStandby)
                return;

            e.Cancel = true;
            EnterWarmStandby();
        }

        private void EnterWarmStandby()
        {
            SaveConfigurationSnapshot(isWindowClosing: true);

            _statusRefreshTimer.Stop();
            _searchDebounceTimer.Stop();
            ClearDragHoverState();

            CancelPendingPreviewLoad();
            _currentPreviewData = null;
            _lastPreviewSelectionKey = string.Empty;
            ClearPreviewBodyContent();

            _addressParseCts?.Cancel();
            ExitAddressEditMode();

            // Hide window immediately for 0ms perceived close, then release all tab COM browsers cleanly
            Hide();

            foreach (ExplorerTabItem tab in _tabs)
            {
                BrowserStackGrid.Children.Remove(tab.BrowserHost);
                tab.Dispose();
            }
            _tabs.Clear();
            _selectedTab = null;
            _initialAutoFallbackTab = null;
            _closedTabs.Clear();

            // Keep 1 prewarmed host ready so the next folder open completes in ~40ms
            SchedulePrewarmThisPcHost(delayMs: 250);
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            if (_sharedStore != null) _sharedStore.Changed -= SharedConfigurationChanged;
            AddressSuggestPopup.IsOpen = false;
            if (!_skipSaveOnForceClose)
            {
                SaveConfigurationSnapshot(isWindowClosing: true);
            }

            _isClosingWindow = true;
            _statusRefreshTimer.Stop();
            _searchDebounceTimer.Stop();
            ClearDragHoverState();

            CancelPendingPreviewLoad();
            _currentPreviewData = null;
            ClearPreviewBodyContent();

            _addressParseCts?.Cancel();
            _addressParseCts?.Dispose();
            _addressSuggestCts?.Cancel();
            _addressSuggestCts?.Dispose();

            _windowHwndSource?.RemoveHook(MainWindowWndProc);
            _windowHwndSource = null;
            ComponentDispatcher.ThreadPreprocessMessage -= ComponentDispatcher_ThreadPreprocessMessage;

            DiscardPrewarmedHost();
            PidlManager.Free(ref _cachedThisPcPidl);

            foreach (ExplorerTabItem tab in _tabs)
            {
                tab.Dispose();
            }
            _tabs.Clear();
            BrowserStackGrid.Children.Clear();
        }
    }
}
