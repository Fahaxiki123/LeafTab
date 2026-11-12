using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LeafTab.Controls;
using LeafTab.Interop;
using LeafTab.Models;

namespace LeafTab.Views
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<ExplorerTabItem> _tabs = new ObservableCollection<ExplorerTabItem>();
        private ExplorerTabItem? _selectedTab;

        // Timer for cross-tab OLE file drag hover-to-switch (~380ms)
        private readonly DispatcherTimer _dragHoverSwitchTimer;
        private ExplorerTabItem? _pendingDragHoverTab;

        // Periodic lightweight status refresh timer
        private readonly DispatcherTimer _statusRefreshTimer;

        private CancellationTokenSource? _addressParseCts;
        private bool _isSystemBlurActive;
        private bool _isClosingWindow;

        public ObservableCollection<ExplorerTabItem> Tabs => _tabs;
        public ExplorerTabItem? SelectedTab => _selectedTab;

        public MainWindow()
        {
            InitializeComponent();
            TabsItemsControl.ItemsSource = _tabs;

            _dragHoverSwitchTimer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(380)
            };
            _dragHoverSwitchTimer.Tick += DragHoverSwitchTimer_Tick;

            _statusRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(600)
            };
            _statusRefreshTimer.Tick += (_, _) => RefreshStatusBarCounts();

            SourceInitialized += MainWindow_SourceInitialized;
            Loaded += MainWindow_Loaded;
            StateChanged += MainWindow_StateChanged;
            Activated += (_, _) => UpdateHeaderGlassLuminosity(isWindowActive: true);
            Deactivated += (_, _) => UpdateHeaderGlassLuminosity(isWindowActive: false);
            Closed += MainWindow_Closed;
        }

        private const double HeaderGlassHeightDip = 86.0;
        private const double FooterGlassHeightDip = 24.0;

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            (int topPx, int bottomPx) = ComputeGlassFramePixels();
            _isSystemBlurActive = WindowBackdropHelper.TryApplyLiquidGlassBackdrop(this, hwnd, topPx, bottomPx);
            UpdateHeaderGlassLuminosity(isWindowActive: true);

            ComponentDispatcher.ThreadPreprocessMessage += ComponentDispatcher_ThreadPreprocessMessage;
            UpdateMaximizedLayoutMargin();
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            SyncGlassFrameRegions();
        }

        private (int TopPx, int BottomPx) ComputeGlassFramePixels()
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            double padTop = RootFrameBorder.Padding.Top;
            double padBottom = RootFrameBorder.Padding.Bottom;
            int topPx = (int)Math.Ceiling((HeaderGlassHeightDip + padTop) * dpi.DpiScaleY);
            int bottomPx = (int)Math.Ceiling((FooterGlassHeightDip + padBottom) * dpi.DpiScaleY);
            return (topPx, bottomPx);
        }

        private void SyncGlassFrameRegions()
        {
            double padTop = RootFrameBorder.Padding.Top;
            double padBottom = RootFrameBorder.Padding.Bottom;
            MainWindowChrome.GlassFrameThickness = new Thickness(0, HeaderGlassHeightDip + padTop, 0, FooterGlassHeightDip + padBottom);

            if (_isSystemBlurActive)
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                (int topPx, int bottomPx) = ComputeGlassFramePixels();
                WindowBackdropHelper.UpdateGlassFrameMargins(hwnd, topPx, bottomPx);
            }
        }

        /// <summary>
        /// Tunes header glass alpha based on whether live OS DWM Acrylic blur is active and whether the window is focused
        /// (active window reveals airy DWM frosted glass; unfocused window settles into a calm matte frost like macOS).
        /// </summary>
        private void UpdateHeaderGlassLuminosity(bool isWindowActive)
        {
            if (!_isSystemBlurActive)
            {
                HeaderTopStop.Color = Color.FromRgb(0xF4, 0xF7, 0xFC);
                HeaderBottomStop.Color = Color.FromRgb(0xEB, 0xF0, 0xF7);
                BottomGlassFooter.Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF7, 0xFB));
                return;
            }

            if (isWindowActive)
            {
                HeaderTopStop.Color = Color.FromArgb(0xCC, 0xF7, 0xFA, 0xFF);
                HeaderBottomStop.Color = Color.FromArgb(0xDC, 0xF0, 0xF5, 0xFC);
                BottomGlassFooter.Background = new SolidColorBrush(Color.FromArgb(0xDC, 0xF5, 0xF8, 0xFD));
            }
            else
            {
                HeaderTopStop.Color = Color.FromArgb(0xEC, 0xF4, 0xF7, 0xFC);
                HeaderBottomStop.Color = Color.FromArgb(0xF4, 0xEE, 0xF3, 0xFA);
                BottomGlassFooter.Background = new SolidColorBrush(Color.FromArgb(0xF2, 0xF4, 0xF7, 0xFB));
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_tabs.Count == 0)
            {
                IntPtr thisPcPidl = PidlManager.CreateThisPcPidl();
                try
                {
                    AddNewTab(thisPcPidl, activate: true);
                }
                finally
                {
                    PidlManager.Free(ref thisPcPidl);
                }
            }

            _statusRefreshTimer.Start();
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            UpdateMaximizedLayoutMargin();
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
                    "M2.6,0.6 H7.4 A1.0,1.0 0 0 1 8.4,1.6 V6.4 M1.6,2.4 H6.4 A1.0,1.0 0 0 1 7.4,3.4 V7.4 A1.0,1.0 0 0 1 6.4,8.4 H1.6 A1.0,1.0 0 0 1 0.6,7.4 V3.4 A1.0,1.0 0 0 1 1.6,2.4 Z");
            }
            else
            {
                RootFrameBorder.Padding = new Thickness(0);
                MaxRestoreButton.ToolTip = "最大化";
                MaxRestoreGlyph.Data = Geometry.Parse(
                    "M1.8,0.6 H7.2 A1.2,1.2 0 0 1 8.4,1.8 V7.2 A1.2,1.2 0 0 1 7.2,8.4 H1.8 A1.2,1.2 0 0 1 0.6,7.2 V1.8 A1.2,1.2 0 0 1 1.8,0.6 Z");
            }

            SyncGlassFrameRegions();
        }

        public ExplorerTabItem AddNewTab(IntPtr targetPidl, bool activate = true)
        {
            IntPtr effectivePidl = targetPidl != IntPtr.Zero
                ? PidlManager.Clone(targetPidl)
                : PidlManager.CreateThisPcPidl();

            try
            {
                var tab = new ExplorerTabItem(effectivePidl);
                ExplorerBrowserHost host = tab.BrowserHost;

                host.Visibility = activate ? Visibility.Visible : Visibility.Hidden;

                host.NavigationCompleted += (_, args) =>
                {
                    tab.UpdateFromNavigation(args.FolderPidl);
                    if (ReferenceEquals(tab, _selectedTab))
                    {
                        if (!AddressTextBox.IsKeyboardFocusWithin)
                        {
                            AddressTextBox.Text = tab.AddressText;
                        }
                        AddressFolderIcon.Source = tab.Icon;
                        Title = $"{tab.Title} - LeafTab";
                        UpdateViewModeIndicator();
                        RefreshStatusBarCounts();
                    }
                };

                host.NavigationFailed += (_, message) =>
                {
                    SetTransientStatus(message);
                };

                host.FolderMiddleClicked += (_, args) =>
                {
                    AddNewTab(args.TargetFolderPidl, activate: false);
                    SetTransientStatus($"已在后台新标签打开文件夹");
                };

                host.BlankAreaDoubleClicked += (_, _) =>
                {
                    SetTransientStatus("已返回上一级");
                };

                _tabs.Add(tab);
                BrowserStackGrid.Children.Add(host);

                if (activate || _selectedTab == null)
                {
                    ActivateTab(tab, focusNativeView: true);
                }
                else
                {
                    RefreshStatusBarCounts();
                }

                TabScrollViewer.ScrollToRightEnd();
                return tab;
            }
            finally
            {
                PidlManager.Free(ref effectivePidl);
            }
        }

        public void ActivateTab(ExplorerTabItem tab, bool focusNativeView = true)
        {
            if (_isClosingWindow || !_tabs.Contains(tab))
                return;

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

            AddressTextBox.Text = tab.AddressText;
            AddressFolderIcon.Source = tab.Icon;
            Title = $"{tab.Title} - LeafTab";
            UpdateViewModeIndicator();
            RefreshStatusBarCounts();

            if (focusNativeView)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    if (ReferenceEquals(_selectedTab, tab))
                        tab.BrowserHost.FocusNativeView();
                }));
            }
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
                RefreshStatusBarCounts();
            }
        }

        private void ComponentDispatcher_ThreadPreprocessMessage(ref MSG msg, ref bool handled)
        {
            if (handled || _isClosingWindow)
                return;

            if (msg.message != NativeMethods.WM_KEYDOWN && msg.message != NativeMethods.WM_SYSKEYDOWN)
                return;

            int vk = (int)msg.wParam.ToInt64();
            ModifierKeys mods = Keyboard.Modifiers;

            if (mods == ModifierKeys.Control)
            {
                if (vk == 'T')
                {
                    IntPtr pidl = PidlManager.CreateThisPcPidl();
                    try { AddNewTab(pidl, activate: true); }
                    finally { PidlManager.Free(ref pidl); }
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
                    FocusAddressBar();
                    handled = true;
                    return;
                }
                if (vk == 0x09)
                {
                    SwitchRelativeTab(+1);
                    handled = true;
                    return;
                }
            }
            else if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && vk == 0x09)
            {
                SwitchRelativeTab(-1);
                handled = true;
                return;
            }
            else if (mods == ModifierKeys.Alt)
            {
                if (vk == 'D')
                {
                    FocusAddressBar();
                    handled = true;
                    return;
                }
                if (vk == 0x25 && _selectedTab != null)
                {
                    _selectedTab.BrowserHost.NavigateBack();
                    handled = true;
                    return;
                }
                if (vk == 0x27 && _selectedTab != null)
                {
                    _selectedTab.BrowserHost.NavigateForward();
                    handled = true;
                    return;
                }
                if (vk == 0x26 && _selectedTab != null)
                {
                    _selectedTab.BrowserHost.NavigateUp();
                    handled = true;
                    return;
                }
                if (vk == 0x20)
                {
                    SystemCommands.ShowSystemMenu(this, new Point(Left + 14, Top + 42));
                    handled = true;
                    return;
                }
            }
            else if (mods == ModifierKeys.None && vk == 0x74 && _selectedTab != null)
            {
                _selectedTab.BrowserHost.RefreshView();
                handled = true;
                return;
            }

            if (_selectedTab != null && _selectedTab.BrowserHost.TryTranslateShellAccelerator(ref msg))
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

        private void FocusAddressBar()
        {
            AddressTextBox.Focus();
            AddressTextBox.SelectAll();
        }

        // ==================== TAB CAPSULE GESTURES ====================

        private void TabPill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ExplorerTabItem tab)
            {
                if (e.ClickCount == 2)
                {
                    e.Handled = true;
                    CloseTab(tab);
                    return;
                }

                ActivateTab(tab, focusNativeView: true);
                e.Handled = true;
            }
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

        private void NewTabButton_Click(object sender, RoutedEventArgs e)
        {
            IntPtr pidl = PidlManager.CreateThisPcPidl();
            try
            {
                AddNewTab(pidl, activate: true);
            }
            finally
            {
                PidlManager.Free(ref pidl);
            }
        }

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

        // ==================== SAFARI TOOLBAR, ADDRESS POD & VIEW CAPSULE ====================

        private void BackButton_Click(object sender, RoutedEventArgs e) => _selectedTab?.BrowserHost.NavigateBack();
        private void ForwardButton_Click(object sender, RoutedEventArgs e) => _selectedTab?.BrowserHost.NavigateForward();
        private void UpButton_Click(object sender, RoutedEventArgs e) => _selectedTab?.BrowserHost.NavigateUp();
        private void RefreshButton_Click(object sender, RoutedEventArgs e) => _selectedTab?.BrowserHost.RefreshView();

        private void DetailsViewButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTab != null && _selectedTab.BrowserHost.TrySetViewMode(FOLDERVIEWMODE.FVM_DETAILS))
            {
                UpdateViewModeIndicator();
                _selectedTab.BrowserHost.FocusNativeView();
            }
        }

        private void IconViewButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTab != null && _selectedTab.BrowserHost.TrySetViewMode(FOLDERVIEWMODE.FVM_ICON))
            {
                UpdateViewModeIndicator();
                _selectedTab.BrowserHost.FocusNativeView();
            }
        }

        private void UpdateViewModeIndicator()
        {
            if (_selectedTab == null)
                return;

            FOLDERVIEWMODE mode = _selectedTab.BrowserHost.GetCurrentViewMode();
            bool isIcons = mode == FOLDERVIEWMODE.FVM_ICON || mode == FOLDERVIEWMODE.FVM_THUMBNAIL || mode == FOLDERVIEWMODE.FVM_TILE;

            Brush activeStroke = new SolidColorBrush(Color.FromRgb(0x00, 0x66, 0xCC));
            Brush inactiveStroke = new SolidColorBrush(Color.FromRgb(0x52, 0x60, 0x71));
            Brush activeBg = new SolidColorBrush(Color.FromArgb(0x1F, 0x00, 0x7A, 0xFF));

            DetailsViewGlyph.Stroke = isIcons ? inactiveStroke : activeStroke;
            IconViewGlyph.Stroke = isIcons ? activeStroke : inactiveStroke;
            DetailsViewButton.Background = isIcons ? Brushes.Transparent : activeBg;
            IconViewButton.Background = isIcons ? activeBg : Brushes.Transparent;
        }

        private void AddressTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            AddressBoxBorder.Background = new SolidColorBrush(Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF));
            AddressBoxBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0xB8, 0x00, 0x7A, 0xFF));
            AddressPodShadow.Color = Color.FromRgb(0x00, 0x7A, 0xFF);
            AddressPodShadow.BlurRadius = 11;
            AddressPodShadow.ShadowDepth = 0;
            AddressPodShadow.Opacity = 0.20;
            AddressTextBox.SelectAll();
        }

        private void AddressTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            RestoreAddressPodRestStyle();
        }

        private void RestoreAddressPodRestStyle()
        {
            if (TryFindResource("AddressPodRestFillBrush") is Brush restFill)
                AddressBoxBorder.Background = restFill;
            if (TryFindResource("GlassCapsuleRimBrush") is Brush rimBrush)
                AddressBoxBorder.BorderBrush = rimBrush;

            AddressPodShadow.Color = Color.FromRgb(0x0F, 0x17, 0x2A);
            AddressPodShadow.BlurRadius = 7;
            AddressPodShadow.ShadowDepth = 1;
            AddressPodShadow.Opacity = 0.045;
        }

        private void AddressTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!AddressTextBox.IsKeyboardFocusWithin)
            {
                e.Handled = true;
                AddressTextBox.Focus();
            }
        }

        private async void AddressTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (_selectedTab != null)
                {
                    AddressTextBox.Text = _selectedTab.AddressText;
                    _selectedTab.BrowserHost.FocusNativeView();
                }
                RestoreAddressPodRestStyle();
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
                        RestoreAddressPodRestStyle();
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

        // ==================== CAPTION & FINDER-STYLE CENTERED STATUS ====================

        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void MaxRestoreButton_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void CloseWindowButton_Click(object sender, RoutedEventArgs e) => Close();

        private DateTime _transientStatusUntilUtc = DateTime.MinValue;

        public void SetTransientStatus(string message, int durationMs = 2800)
        {
            StatusCenterText.Text = message;
            _transientStatusUntilUtc = DateTime.UtcNow.AddMilliseconds(durationMs);
        }

        private void RefreshStatusBarCounts()
        {
            if (_isClosingWindow) return;

            if (DateTime.UtcNow >= _transientStatusUntilUtc && _selectedTab != null)
            {
                (int total, int selected) = _selectedTab.BrowserHost.GetItemCounts();
                StatusCenterText.Text = selected > 0
                    ? $"{total} 个项目  ·  已选 {selected} 项"
                    : $"{total} 个项目";
            }

            using Process proc = Process.GetCurrentProcess();
            double wsMb = proc.WorkingSet64 / (1024.0 * 1024.0);
            StatusRightText.Text = $"{_tabs.Count} 标签 · {wsMb:F0} MB";
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            _isClosingWindow = true;
            _statusRefreshTimer.Stop();
            ClearDragHoverState();

            _addressParseCts?.Cancel();
            _addressParseCts?.Dispose();

            ComponentDispatcher.ThreadPreprocessMessage -= ComponentDispatcher_ThreadPreprocessMessage;

            foreach (ExplorerTabItem tab in _tabs)
            {
                tab.Dispose();
            }
            _tabs.Clear();
            BrowserStackGrid.Children.Clear();
        }
    }
}
