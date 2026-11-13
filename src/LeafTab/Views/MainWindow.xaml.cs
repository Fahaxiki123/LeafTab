using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

        private static readonly CubicEase s_cubicOutEase = CreateFrozenCubicOutEase();

        private static CubicEase CreateFrozenCubicOutEase()
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            ease.Freeze();
            return ease;
        }

        private readonly ObservableCollection<ExplorerTabItem> _tabs = new ObservableCollection<ExplorerTabItem>();
        private readonly ObservableCollection<BookmarkItemViewModel> _bookmarks = new ObservableCollection<BookmarkItemViewModel>();
        private readonly Stack<ClosedTabSnapshot> _closedTabs = new Stack<ClosedTabSnapshot>();

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
        private bool _suppressSearchTextChanged;

        // Lightweight periodic UI/selection sync timer
        private readonly DispatcherTimer _statusRefreshTimer;
        private DateTime _transientStatusUntilUtc = DateTime.MinValue;

        // Tab drag-to-reorder state
        private ExplorerTabItem? _tabReorderCandidate;
        private Point _tabReorderStartPt;
        private bool _isReorderingTab;

        private CancellationTokenSource? _addressParseCts;
        private string _lastRenderedBreadcrumbKey = string.Empty;
        private bool _isCurrentFolderStarred;
        private bool _isGlassActive;
        private bool _showBookmarkBar;
        private bool _showPreviewPane;
        private bool _isClosingWindow;

        public ObservableCollection<ExplorerTabItem> Tabs => _tabs;
        public ExplorerTabItem? SelectedTab => _selectedTab;
        internal bool HasPrewarmedHost => _prewarmedThisPcHost != null;

        public MainWindow() : this(skipSessionPersistence: false)
        {
        }

        public MainWindow(bool skipSessionPersistence)
        {
            _skipSessionPersistence = skipSessionPersistence;
            _enableAutoPrewarm = !skipSessionPersistence;
            _cachedThisPcPidl = PidlManager.CreateThisPcPidl();

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
                Interval = TimeSpan.FromMilliseconds(420)
            };
            _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;

            _statusRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(350)
            };
            _statusRefreshTimer.Tick += (_, _) => UpdateCommandBarAndStatus(computeBytes: true);

            LoadConfigurationAndWindowPlacement();

            SourceInitialized += MainWindow_SourceInitialized;
            Loaded += MainWindow_Loaded;
            StateChanged += MainWindow_StateChanged;
            Activated += (_, _) => UpdateChromeLuminosity(isWindowActive: true);
            Deactivated += (_, _) => UpdateChromeLuminosity(isWindowActive: false);
            Closed += MainWindow_Closed;
        }

        private void LoadConfigurationAndWindowPlacement()
        {
            if (_skipSessionPersistence)
                return;

            _config = AppConfigService.Load();
            _showBookmarkBar = _config.ShowBookmarkBar;
            _showPreviewPane = _config.ShowPreviewPane;

            BookmarkBarBorder.Visibility = _showBookmarkBar ? Visibility.Visible : Visibility.Collapsed;
            CommandBarSurface.BorderThickness = _showBookmarkBar ? new Thickness(0) : new Thickness(0, 0, 0, 1);
            UpdatePreviewButtonVisual();

            _bookmarks.Clear();
            foreach (BookmarkConfigItem bm in _config.Bookmarks)
            {
                if (!string.IsNullOrWhiteSpace(bm.TargetPath))
                {
                    _bookmarks.Add(AppConfigService.CreateBookmarkViewModel(bm.Title, bm.TargetPath));
                }
            }

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

        private void SaveConfigurationSnapshot()
        {
            if (_skipSessionPersistence)
                return;

            var cfg = new AppConfig
            {
                Version = 1,
                IsMaximized = WindowState == WindowState.Maximized,
                ShowBookmarkBar = _showBookmarkBar,
                BookmarksInitialized = true,
                ShowPreviewPane = _showPreviewPane,
                SelectedTabIndex = _selectedTab != null ? Math.Max(0, _tabs.IndexOf(_selectedTab)) : 0
            };

            Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            if (bounds.Width >= MinWidth && bounds.Height >= MinHeight)
            {
                cfg.WindowLeft = bounds.Left;
                cfg.WindowTop = bounds.Top;
                cfg.WindowWidth = bounds.Width;
                cfg.WindowHeight = bounds.Height;
            }

            foreach (BookmarkItemViewModel bm in _bookmarks)
            {
                cfg.Bookmarks.Add(new BookmarkConfigItem
                {
                    Title = bm.Title,
                    TargetPath = bm.TargetPath
                });
            }

            foreach (ExplorerTabItem tab in _tabs)
            {
                cfg.OpenTabs.Add(tab.ExportSavedConfig());
            }

            AppConfigService.Save(cfg);
        }

        // ==================== PARTITIONED LIQUID GLASS BACKDROP & WINDOW CHROME ====================

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            (int topPx, int bottomPx) = ComputeGlassMarginsPixels();
            _isGlassActive = WindowBackdropHelper.TryApplyLiquidGlassBackdrop(this, hwnd, topPx, bottomPx);
            UpdateChromeLuminosity(isWindowActive: true);

            ComponentDispatcher.ThreadPreprocessMessage += ComponentDispatcher_ThreadPreprocessMessage;
            UpdateMaximizedLayoutMargin();
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
                TabTitleBarSurface.Background = new SolidColorBrush(
                    isWindowActive ? Color.FromRgb(0xE2, 0xEC, 0xF7) : Color.FromRgb(0xEB, 0xF0, 0xF7));
                NavBarSurface.Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFC, 0xFF));
                CommandBarSurface.Background = new SolidColorBrush(Color.FromRgb(0xF6, 0xFA, 0xFF));
                BookmarkBarBorder.Background = new SolidColorBrush(Color.FromRgb(0xF6, 0xFA, 0xFF));
                BottomStatusBar.Background = new SolidColorBrush(Color.FromRgb(0xF1, 0xF6, 0xFC));
                return;
            }

            TabTitleBarSurface.Background = new SolidColorBrush(
                isWindowActive
                    ? Color.FromArgb(0x96, 0xDE, 0xE8, 0xF5)
                    : Color.FromArgb(0xDC, 0xEA, 0xEF, 0xF7));
            NavBarSurface.Background = new SolidColorBrush(Color.FromArgb(0xE8, 0xFA, 0xFC, 0xFF));
            CommandBarSurface.Background = new SolidColorBrush(Color.FromArgb(0xEC, 0xF5, 0xF9, 0xFF));
            BookmarkBarBorder.Background = new SolidColorBrush(Color.FromArgb(0xEE, 0xF5, 0xF9, 0xFF));
            BottomStatusBar.Background = new SolidColorBrush(Color.FromArgb(0xDC, 0xEF, 0xF5, 0xFC));
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_tabs.Count == 0)
            {
                bool restoredAny = false;
                if (!_skipSessionPersistence && _config.OpenTabs.Count > 0)
                {
                    int targetSelected = Math.Max(0, Math.Min(_config.SelectedTabIndex, _config.OpenTabs.Count - 1));
                    ExplorerTabItem? tabToActivate = null;
                    var deferredInactiveTabs = new List<ExplorerTabItem>();

                    for (int i = 0; i < _config.OpenTabs.Count; i++)
                    {
                        SavedTabConfigItem saved = _config.OpenTabs[i];
                        IntPtr pidl = PidlManager.TryParseLocalOrShellFast(saved.CurrentPath);
                        if (pidl == IntPtr.Zero)
                            continue;

                        try
                        {
                            bool isTargetActive = (i == targetSelected) || (tabToActivate == null && i == _config.OpenTabs.Count - 1);
                            ExplorerTabItem created = AddNewTab(
                                pidl,
                                activate: false,
                                animateOpen: false,
                                deferHostMount: !isTargetActive);
                            created.RestoreHistory(saved.HistoryPaths, saved.HistoryIndex);

                            if (i == targetSelected || tabToActivate == null)
                            {
                                tabToActivate = created;
                            }
                            restoredAny = true;
                        }
                        finally
                        {
                            PidlManager.Free(ref pidl);
                        }
                    }

                    if (tabToActivate != null)
                    {
                        ActivateTab(tabToActivate, focusNativeView: true);
                        foreach (ExplorerTabItem t in _tabs)
                        {
                            if (!ReferenceEquals(t, tabToActivate) && !BrowserStackGrid.Children.Contains(t.BrowserHost))
                            {
                                deferredInactiveTabs.Add(t);
                            }
                        }
                    }

                    foreach (ExplorerTabItem bgTab in deferredInactiveTabs)
                    {
                        ExplorerTabItem captured = bgTab;
                        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                        {
                            if (!_isClosingWindow && _tabs.Contains(captured) && !BrowserStackGrid.Children.Contains(captured.BrowserHost))
                            {
                                BrowserStackGrid.Children.Add(captured.BrowserHost);
                            }
                        }));
                    }
                }

                if (!restoredAny)
                {
                    IntPtr thisPcPidl = PidlManager.CreateThisPcPidl();
                    try
                    {
                        AddNewTab(thisPcPidl, activate: true, animateOpen: false);
                    }
                    finally
                    {
                        PidlManager.Free(ref thisPcPidl);
                    }
                }
            }

            _statusRefreshTimer.Start();
            SchedulePrewarmThisPcHost();
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e) => UpdateMaximizedLayoutMargin();

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

        private void SchedulePrewarmThisPcHost()
        {
            if (!_enableAutoPrewarm || _isClosingWindow || _prewarmedThisPcHost != null || _prewarmScheduled)
                return;

            _prewarmScheduled = true;
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                _prewarmScheduled = false;
                if (_enableAutoPrewarm && !_isClosingWindow && _prewarmedThisPcHost == null)
                {
                    EnsurePrewarmedThisPcHostReady();
                }
            }));
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

            if (_prewarmedThisPcHost.ShowPreviewPane != _showPreviewPane)
                return null;

            if (!PidlManager.AreEqual(targetPidl, _cachedThisPcPidl))
                return null;

            ExplorerBrowserHost adopted = _prewarmedThisPcHost;
            _prewarmedThisPcHost = null;
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
                    if (ReferenceEquals(tab, _selectedTab))
                    {
                        SyncChromeWithSelectedTab(computeBytes: false);
                    }
                };

                host.NavigationFailed += (_, message) =>
                {
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
                        UpdateCommandBarAndStatus(computeBytes: true);
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
                        UpdateCommandBarAndStatus(computeBytes: false);
                    }
                };

                _tabs.Add(tab);
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
            Title = $"{_selectedTab.Title} - LeafTab";
            SearchPlaceholderText.Text = $"在 {_selectedTab.Title} 中搜索";

            RebuildBreadcrumbBar(_selectedTab);
            UpdateBookmarkStarVisual();
            UpdateNavigationButtonsState();
            UpdateViewModeIndicator();
            UpdateCommandBarAndStatus(computeBytes);
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

            ClearDragHoverState();

            if (_closedTabs.Count >= MaxClosedTabSnapshots)
            {
                var trimmed = _closedTabs.ToArray();
                _closedTabs.Clear();
                for (int i = Math.Min(trimmed.Length - 1, MaxClosedTabSnapshots - 2); i >= 0; i--)
                    _closedTabs.Push(trimmed[i]);
            }
            _closedTabs.Push(tab.CreateClosedSnapshot());

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

        // ==================== GLOBAL KEYBOARD ACCELERATORS ====================

        private void ComponentDispatcher_ThreadPreprocessMessage(ref MSG msg, ref bool handled)
        {
            if (handled || _isClosingWindow)
                return;

            if (msg.message != NativeMethods.WM_KEYDOWN && msg.message != NativeMethods.WM_SYSKEYDOWN)
                return;

            int vk = (int)msg.wParam.ToInt64();
            ModifierKeys mods = Keyboard.Modifiers;
            bool inWpfTextBox = AddressTextBox.IsKeyboardFocusWithin || SearchTextBox.IsKeyboardFocusWithin;

            if (mods == ModifierKeys.Control)
            {
                if (vk == 'T')
                {
                    OpenNewThisPcTab();
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
                    SystemCommands.ShowSystemMenu(this, new Point(Left + 14, Top + TabStripHeightDip));
                    handled = true;
                    return;
                }
            }
            else if (mods == ModifierKeys.None && !inWpfTextBox)
            {
                if (vk == 0x74 && _selectedTab != null) // F5
                {
                    _selectedTab.BrowserHost.RefreshView();
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

        // ==================== TAB GESTURES & DRAG REORDERING ====================

        private void TabPill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ExplorerTabItem tab)
            {
                if (e.ClickCount == 2)
                {
                    _tabReorderCandidate = null;
                    _isReorderingTab = false;
                    e.Handled = true;
                    CloseTab(tab);
                    return;
                }

                ActivateTab(tab, focusNativeView: true);
                _tabReorderCandidate = tab;
                _tabReorderStartPt = e.GetPosition(TabsItemsControl);
                _isReorderingTab = false;
                fe.CaptureMouse();
                e.Handled = true;
            }
        }

        private void TabPill_MouseMove(object sender, MouseEventArgs e)
        {
            if (_tabReorderCandidate == null || e.LeftButton != MouseButtonState.Pressed)
                return;

            Point currentPt = e.GetPosition(TabsItemsControl);
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
                        break;
                    }
                }
            }
        }

        private void TabPill_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.IsMouseCaptured)
            {
                fe.ReleaseMouseCapture();
            }
            _tabReorderCandidate = null;
            _isReorderingTab = false;
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

        // ==================== NAVIGATION & INTERACTIVE BREADCRUMB BAR ====================

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTab != null)
            {
                _selectedTab.NavigateHistoryStep(-1);
                UpdateNavigationButtonsState();
                _selectedTab.BrowserHost.FocusNativeView();
            }
        }

        private void ForwardButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTab != null)
            {
                _selectedTab.NavigateHistoryStep(+1);
                UpdateNavigationButtonsState();
                _selectedTab.BrowserHost.FocusNativeView();
            }
        }

        private void UpButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTab != null)
            {
                _selectedTab.BrowserHost.NavigateUp();
                _selectedTab.BrowserHost.FocusNativeView();
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTab != null)
            {
                _selectedTab.BrowserHost.RefreshView();
                _selectedTab.BrowserHost.FocusNativeView();
            }
        }

        private void UpdateNavigationButtonsState()
        {
            if (_selectedTab == null)
            {
                BackButton.IsEnabled = false;
                ForwardButton.IsEnabled = false;
                UpButton.IsEnabled = false;
                return;
            }

            BackButton.IsEnabled = _selectedTab.CanNavigateBack;
            ForwardButton.IsEnabled = _selectedTab.CanNavigateForward;
            UpButton.IsEnabled = _selectedTab.CanNavigateUp;
        }

        private void RebuildBreadcrumbBar(ExplorerTabItem tab)
        {
            BreadcrumbItemsPanel.Children.Clear();
            Style segStyle = (Style)FindResource("GlassBreadcrumbSegmentButtonStyle");

            string currentKey = tab.BrowserHost.IsShowingSearchResults
                ? $"__SEARCH__:{tab.Id}:{tab.SearchQuery}"
                : $"{tab.Id}:{tab.AddressText}";

            if (tab.BrowserHost.IsShowingSearchResults)
            {
                var searchLabel = new Button
                {
                    Content = $"“{tab.SearchQuery}”在 {tab.Title} 中的搜索结果",
                    Style = segStyle,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0x62, 0xCC)),
                    FontWeight = FontWeights.Medium,
                    ToolTip = "点击退出搜索并返回原目录"
                };
                searchLabel.Click += (_, _) =>
                {
                    ClearAndExitSearch();
                    _selectedTab?.BrowserHost.FocusNativeView();
                };
                BreadcrumbItemsPanel.Children.Add(searchLabel);
                AnimateBreadcrumbIfChanged(currentKey);
                return;
            }

            List<BreadcrumbNode> chain = PidlManager.BuildBreadcrumbChain(tab.CurrentPidl);
            for (int i = 0; i < chain.Count; i++)
            {
                BreadcrumbNode node = chain[i];
                bool isLast = (i == chain.Count - 1);

                var segBtn = new Button
                {
                    Content = node.Title,
                    Tag = node,
                    Style = segStyle,
                    FontWeight = isLast ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = new SolidColorBrush(
                        isLast ? Color.FromRgb(0x0F, 0x17, 0x2A) : Color.FromRgb(0x33, 0x41, 0x55)),
                    ToolTip = node.ParsingName
                };
                segBtn.Click += BreadcrumbSegment_Click;
                segBtn.MouseUp += BreadcrumbSegment_MouseUp;
                BreadcrumbItemsPanel.Children.Add(segBtn);

                var chevronBtn = new Button
                {
                    Width = 18,
                    Height = 24,
                    Padding = new Thickness(0),
                    Tag = node,
                    Style = segStyle,
                    ToolTip = $"查看「{node.Title}」下的子文件夹",
                    Content = new System.Windows.Shapes.Path
                    {
                        Width = 4.8,
                        Height = 7.8,
                        Stretch = Stretch.Uniform,
                        Stroke = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
                        StrokeThickness = 1.25,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        StrokeLineJoin = PenLineJoin.Round,
                        Data = Geometry.Parse("M1,1 L4.5,4.5 L1,8")
                    }
                };
                chevronBtn.Click += BreadcrumbChevron_Click;
                BreadcrumbItemsPanel.Children.Add(chevronBtn);
            }

            BreadcrumbScrollViewer.ScrollToRightEnd();
            AnimateBreadcrumbIfChanged(currentKey);
        }

        private void AnimateBreadcrumbIfChanged(string currentKey)
        {
            if (!_skipSessionPersistence &&
                !string.IsNullOrEmpty(_lastRenderedBreadcrumbKey) &&
                !string.Equals(_lastRenderedBreadcrumbKey, currentKey, StringComparison.Ordinal))
            {
                BreadcrumbSlideTransform.X = 0.0;
                BreadcrumbItemsPanel.Opacity = 1.0;

                var slideAnim = new DoubleAnimation(5.5, 0.0, TimeSpan.FromMilliseconds(135))
                {
                    EasingFunction = s_cubicOutEase,
                    FillBehavior = FillBehavior.Stop
                };
                var fadeAnim = new DoubleAnimation(0.45, 1.0, TimeSpan.FromMilliseconds(135))
                {
                    EasingFunction = s_cubicOutEase,
                    FillBehavior = FillBehavior.Stop
                };

                BreadcrumbSlideTransform.BeginAnimation(TranslateTransform.XProperty, slideAnim);
                BreadcrumbItemsPanel.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
            }

            _lastRenderedBreadcrumbKey = currentKey;
        }

        private void BreadcrumbSegment_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is BreadcrumbNode node && _selectedTab != null)
            {
                IntPtr pidl = PidlManager.FromBytes(node.PidlBytes);
                if (pidl != IntPtr.Zero)
                {
                    try
                    {
                        _selectedTab.BrowserHost.NavigateToPidl(pidl);
                        _selectedTab.BrowserHost.FocusNativeView();
                    }
                    finally
                    {
                        PidlManager.Free(ref pidl);
                    }
                }
            }
        }

        private void BreadcrumbSegment_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle &&
                sender is Button btn &&
                btn.Tag is BreadcrumbNode node)
            {
                IntPtr pidl = PidlManager.FromBytes(node.PidlBytes);
                if (pidl != IntPtr.Zero)
                {
                    try
                    {
                        AddNewTab(pidl, activate: false, animateOpen: !_skipSessionPersistence);
                        SetTransientStatus($"已在后台新标签打开：{node.Title}");
                    }
                    finally
                    {
                        PidlManager.Free(ref pidl);
                    }
                }
                e.Handled = true;
            }
        }

        private void BreadcrumbChevron_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not BreadcrumbNode node || _selectedTab == null)
                return;

            IntPtr parentPidl = PidlManager.FromBytes(node.PidlBytes);
            if (parentPidl == IntPtr.Zero)
                return;

            List<BreadcrumbNode> children;
            try
            {
                children = PidlManager.EnumerateChildFolders(parentPidl, maxCount: 80);
            }
            finally
            {
                PidlManager.Free(ref parentPidl);
            }

            var menu = CreateLiquidGlassContextMenu(btn);
            if (children.Count == 0)
            {
                menu.Items.Add(new MenuItem { Header = "（无子文件夹）", IsEnabled = false });
            }
            else
            {
                foreach (BreadcrumbNode child in children)
                {
                    var mi = new MenuItem
                    {
                        Header = child.Title,
                        Tag = child,
                        Icon = CreateMenuIcon(child.Icon)
                    };
                    mi.Click += (_, _) =>
                    {
                        IntPtr target = PidlManager.FromBytes(child.PidlBytes);
                        if (target != IntPtr.Zero)
                        {
                            try
                            {
                                _selectedTab?.BrowserHost.NavigateToPidl(target);
                                _selectedTab?.BrowserHost.FocusNativeView();
                            }
                            finally
                            {
                                PidlManager.Free(ref target);
                            }
                        }
                    };
                    menu.Items.Add(mi);
                }
            }

            menu.IsOpen = true;
            e.Handled = true;
        }

        private void AddressBoxBorder_MouseEnter(object sender, MouseEventArgs e) => UpdateBookmarkStarOpacity(animate: true);

        private void AddressBoxBorder_MouseLeave(object sender, MouseEventArgs e) => UpdateBookmarkStarOpacity(animate: true);

        private void UpdateBookmarkStarOpacity(bool animate)
        {
            double targetOpacity = _isCurrentFolderStarred
                ? 1.0
                : (AddressBoxBorder.IsMouseOver ? 0.82 : 0.0);

            BookmarkStarButton.IsHitTestVisible = targetOpacity > 0.01;

            if (!animate || _skipSessionPersistence)
            {
                BookmarkStarButton.BeginAnimation(UIElement.OpacityProperty, null);
                BookmarkStarButton.Opacity = targetOpacity;
                return;
            }

            var anim = new DoubleAnimation(targetOpacity, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = s_cubicOutEase
            };
            BookmarkStarButton.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        private void BreadcrumbRootIconButton_Click(object sender, RoutedEventArgs e)
        {
            EnterAddressEditMode();
            e.Handled = true;
        }

        private void BreadcrumbBlank_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is Grid || e.OriginalSource is ScrollViewer || e.OriginalSource is ScrollContentPresenter || e.OriginalSource is StackPanel)
            {
                EnterAddressEditMode();
                e.Handled = true;
            }
        }

        public void EnterAddressEditMode()
        {
            if (_selectedTab != null)
            {
                AddressTextBox.Text = _selectedTab.AddressText;
            }

            BreadcrumbContainerGrid.Visibility = Visibility.Collapsed;
            AddressEditGrid.Visibility = Visibility.Visible;
            AddressBoxBorder.Background = Brushes.White;
            AddressBoxBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0xB8, 0x00, 0x7A, 0xFF));
            AddressBoxBorder.BorderThickness = new Thickness(1.4);

            AddressTextBox.Focus();
            AddressTextBox.SelectAll();
        }

        public void ExitAddressEditMode()
        {
            AddressEditGrid.Visibility = Visibility.Collapsed;
            BreadcrumbContainerGrid.Visibility = Visibility.Visible;
            AddressBoxBorder.Background = new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));
            AddressBoxBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x16, 0x0F, 0x17, 0x2A));
            AddressBoxBorder.BorderThickness = new Thickness(1);
        }

        private void AddressTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            AddressBoxBorder.Background = Brushes.White;
            AddressBoxBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0xB8, 0x00, 0x7A, 0xFF));
            AddressTextBox.SelectAll();
        }

        private void AddressTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            ExitAddressEditMode();
        }

        private async void AddressTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                ExitAddressEditMode();
                _selectedTab?.BrowserHost.FocusNativeView();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await NavigateAddressInputAsync(AddressTextBox.Text);
            }
        }

        public async Task<bool> NavigateAddressInputAsync(string inputText)
        {
            if (_selectedTab == null)
                return false;

            _addressParseCts?.Cancel();
            _addressParseCts?.Dispose();
            _addressParseCts = new CancellationTokenSource();
            CancellationToken token = _addressParseCts.Token;

            ExplorerTabItem targetTab = _selectedTab;
            AddressBusyIndicator.Visibility = Visibility.Visible;

            try
            {
                (IntPtr pidl, string? error) = await PidlManager.TryParseAddressAsync(inputText, token);
                if (token.IsCancellationRequested)
                {
                    PidlManager.Free(ref pidl);
                    return false;
                }

                if (pidl == IntPtr.Zero)
                {
                    SetTransientStatus(error ?? "路径无效。");
                    return false;
                }

                try
                {
                    bool ok = targetTab.BrowserHost.NavigateToPidl(pidl);
                    if (ok)
                    {
                        ExitAddressEditMode();
                        targetTab.BrowserHost.FocusNativeView();
                    }
                    else
                    {
                        SetTransientStatus($"无法跳转到：{inputText}");
                    }
                    return ok;
                }
                finally
                {
                    PidlManager.Free(ref pidl);
                }
            }
            finally
            {
                AddressBusyIndicator.Visibility = Visibility.Collapsed;
            }
        }

        // ==================== ASYNC DIRECTORY SEARCH POD ====================

        private void FocusSearchBox()
        {
            SearchTextBox.Focus();
            SearchTextBox.SelectAll();
        }

        private void SetSearchBoxTextSilently(string text)
        {
            _suppressSearchTextChanged = true;
            try
            {
                SearchTextBox.Text = text ?? string.Empty;
                UpdateSearchBoxVisuals();
            }
            finally
            {
                _suppressSearchTextChanged = false;
            }
        }

        private void UpdateSearchBoxVisuals()
        {
            bool hasText = !string.IsNullOrEmpty(SearchTextBox.Text);
            SearchPlaceholderText.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
            ClearSearchButton.Visibility = (hasText || (_selectedTab?.BrowserHost.IsShowingSearchResults ?? false))
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void SearchTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            SearchBoxBorder.Background = Brushes.White;
            SearchBoxBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0xB8, 0x00, 0x7A, 0xFF));
            SearchBoxBorder.BorderThickness = new Thickness(1.4);
        }

        private void SearchTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            SearchBoxBorder.Background = new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));
            SearchBoxBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x16, 0x0F, 0x17, 0x2A));
            SearchBoxBorder.BorderThickness = new Thickness(1);
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateSearchBoxVisuals();
            if (_suppressSearchTextChanged || _selectedTab == null)
                return;

            _selectedTab.SearchQuery = SearchTextBox.Text;
            _searchDebounceTimer.Stop();

            if (string.IsNullOrWhiteSpace(SearchTextBox.Text))
            {
                if (_selectedTab.BrowserHost.IsShowingSearchResults)
                {
                    _selectedTab.BrowserHost.ExitSearchMode();
                }
                return;
            }

            _searchDebounceTimer.Start();
        }

        private async void SearchDebounceTimer_Tick(object? sender, EventArgs e)
        {
            _searchDebounceTimer.Stop();
            await ExecuteSearchOnSelectedTabAsync();
        }

        private async void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                ClearAndExitSearch();
                _selectedTab?.BrowserHost.FocusNativeView();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter)
            {
                _searchDebounceTimer.Stop();
                e.Handled = true;
                await ExecuteSearchOnSelectedTabAsync();
            }
        }

        private async void SearchSubmitButton_Click(object sender, RoutedEventArgs e)
        {
            _searchDebounceTimer.Stop();
            if (string.IsNullOrWhiteSpace(SearchTextBox.Text))
            {
                FocusSearchBox();
                return;
            }
            await ExecuteSearchOnSelectedTabAsync();
        }

        private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
        {
            ClearAndExitSearch();
            _selectedTab?.BrowserHost.FocusNativeView();
        }

        private void ClearAndExitSearch()
        {
            _searchDebounceTimer.Stop();
            if (_selectedTab != null)
            {
                _selectedTab.SearchQuery = string.Empty;
                _selectedTab.BrowserHost.ExitSearchMode();
            }
            SetSearchBoxTextSilently(string.Empty);
            UpdateNavigationButtonsState();
            UpdateCommandBarAndStatus(computeBytes: false);
        }

        public async Task<int> ExecuteSearchOnSelectedTabAsync()
        {
            if (_selectedTab == null)
                return 0;

            string query = (SearchTextBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(query))
                return 0;

            _selectedTab.SearchQuery = query;
            int found = await _selectedTab.BrowserHost.StartSearchAsync(
                query,
                _selectedTab.CurrentPidl,
                _selectedTab.FileSystemPath);

            UpdateNavigationButtonsState();
            UpdateCommandBarAndStatus(computeBytes: false);
            return found;
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
                        !SearchTextBox.IsKeyboardFocusWithin)
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

            menu.IsOpen = true;
        }

        private void PreviewPaneButton_Click(object sender, RoutedEventArgs e)
        {
            TogglePreviewPane();
            _selectedTab?.BrowserHost.FocusNativeView();
        }

        private void TogglePreviewPane()
        {
            _showPreviewPane = !_showPreviewPane;
            UpdatePreviewButtonVisual();

            foreach (ExplorerTabItem tab in _tabs)
            {
                tab.BrowserHost.SetPreviewPaneVisible(
                    _showPreviewPane,
                    tab.CurrentPidl,
                    beforeRebrowse: () => tab.SuppressNextHistoryRecord());
            }

            if (_prewarmedThisPcHost != null && _cachedThisPcPidl != IntPtr.Zero)
            {
                _prewarmedThisPcHost.SetPreviewPaneVisible(_showPreviewPane, _cachedThisPcPidl);
            }

            SetTransientStatus(_showPreviewPane ? "已开启右侧预览窗格" : "已关闭右侧预览窗格");
        }

        private void UpdatePreviewButtonVisual()
        {
            if (_showPreviewPane)
            {
                PreviewPaneButton.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x7A, 0xFF));
                PreviewPaneButton.BorderBrush = new SolidColorBrush(Color.FromArgb(0x48, 0x00, 0x7A, 0xFF));
                PreviewPaneText.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0x62, 0xCC));
                PreviewPaneText.FontWeight = FontWeights.Medium;
            }
            else
            {
                PreviewPaneButton.Background = Brushes.Transparent;
                PreviewPaneButton.BorderBrush = Brushes.Transparent;
                PreviewPaneText.Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B));
                PreviewPaneText.FontWeight = FontWeights.Normal;
            }
        }

        private void RefreshAllTabs()
        {
            foreach (ExplorerTabItem tab in _tabs)
            {
                tab.BrowserHost.RefreshView();
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

            Brush activeStroke = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xFF));
            Brush inactiveStroke = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
            Brush activeBg = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x7A, 0xFF));

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
                BookmarkStarGlyph.Stroke = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xFF));
                BookmarkStarGlyph.Fill = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xFF));
                BookmarkStarButton.ToolTip = "从书签栏移除此文件夹 (Ctrl+D)";
            }
            else
            {
                BookmarkStarGlyph.Stroke = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
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
            SelectionStatusInfo sel = host.GetSelectionStatus(computeBytes);

            bool isRealFsFolder = !string.IsNullOrWhiteSpace(_selectedTab.FileSystemPath) && !host.IsShowingSearchResults;
            bool hasSelection = sel.SelectedItems > 0;

            NewItemDropdownButton.IsEnabled = isRealFsFolder;
            CutButton.IsEnabled = hasSelection;
            CopyButton.IsEnabled = hasSelection;
            RenameButton.IsEnabled = sel.SelectedItems == 1;
            ShareCopyPathButton.IsEnabled = hasSelection;
            DeleteButton.IsEnabled = hasSelection;
            PasteButton.IsEnabled = isRealFsFolder && CanPasteFromClipboardSafe();

            if (host.IsShowingSearchResults && host.IsSearchRunning)
            {
                StatusLeftText.Text = $"正在搜索… 已找到 {host.SearchMatchCount} 个项目";
                return;
            }

            if (sel.SelectedItems > 0)
            {
                StatusLeftText.Text = sel.HasSizedFileSelection
                    ? $"{sel.TotalItems} 个项目  ·  已选择 {sel.SelectedItems} 个项目（{PidlManager.FormatByteSize(sel.SelectedSizeBytes)}）"
                    : $"{sel.TotalItems} 个项目  ·  已选择 {sel.SelectedItems} 个项目";
            }
            else
            {
                StatusLeftText.Text = $"{sel.TotalItems} 个项目";
            }
        }

        private static bool CanPasteFromClipboardSafe()
        {
            try
            {
                return Clipboard.ContainsFileDropList() || Clipboard.ContainsData("Shell IDList Array");
            }
            catch
            {
                return false;
            }
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            SaveConfigurationSnapshot();

            _isClosingWindow = true;
            _statusRefreshTimer.Stop();
            _searchDebounceTimer.Stop();
            ClearDragHoverState();

            _addressParseCts?.Cancel();
            _addressParseCts?.Dispose();

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
