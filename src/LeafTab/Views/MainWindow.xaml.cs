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

        // Periodic lightweight status refresh timer (updates item/selection counts & memory indicator)
        private readonly DispatcherTimer _statusRefreshTimer;

        private CancellationTokenSource? _addressParseCts;
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
            Closed += MainWindow_Closed;
        }

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;

            // Apply native rounded window corners when running on Windows 11 (Build 22000+);
            // harmlessly ignored on Windows 10 where DWM standard border/shadow applies.
            if (Environment.OSVersion.Version.Build >= 22000 && hwnd != IntPtr.Zero)
            {
                int pref = NativeMethods.DWMWCP_ROUND;
                NativeMethods.DwmSetWindowAttribute(
                    hwnd,
                    NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
                    ref pref,
                    sizeof(int));
            }

            ComponentDispatcher.ThreadPreprocessMessage += ComponentDispatcher_ThreadPreprocessMessage;
            UpdateMaximizedLayoutMargin();
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
                RootFrameBorder.BorderThickness = new Thickness(0);
                RootFrameBorder.Padding = new Thickness(
                    resize.Left + 4,
                    resize.Top + 4,
                    resize.Right + 4,
                    resize.Bottom + 4);
                MaxRestoreButton.ToolTip = "向下还原";
                MaxRestoreGlyph.Data = Geometry.Parse("M2.5,0.5 H9.5 V7.5 M0.5,2.5 H7.5 V9.5 H0.5 Z");
            }
            else
            {
                RootFrameBorder.BorderThickness = new Thickness(1);
                RootFrameBorder.Padding = new Thickness(0);
                MaxRestoreButton.ToolTip = "最大化";
                MaxRestoreGlyph.Data = Geometry.Parse("M0.5,0.5 H9.5 V9.5 H0.5 Z");
            }
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

                // Keep hidden until explicitly activated so background middle-click tabs never flash
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
                        Title = $"{tab.Title} - LeafTab";
                        RefreshStatusBarCounts();
                    }
                };

                host.NavigationFailed += (_, message) =>
                {
                    SetTransientStatus(message);
                };

                host.FolderMiddleClicked += (_, args) =>
                {
                    // Clover habit: Middle-clicking a folder opens it in a new tab
                    AddNewTab(args.TargetFolderPidl, activate: false);
                    SetTransientStatus($"已在新标签页打开文件夹 (UIA {args.UiaElapsedMs:F1}ms)");
                };

                host.BlankAreaDoubleClicked += (_, elapsedMs) =>
                {
                    SetTransientStatus($"返回上一级 (UIA {elapsedMs:F1}ms)");
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
            Title = $"{tab.Title} - LeafTab";
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

            // 1. Global tab / address bar shortcuts handled directly by LeafTab
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
                if (vk == 0x09) // VK_TAB
                {
                    SwitchRelativeTab(+1);
                    handled = true;
                    return;
                }
            }
            else if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && vk == 0x09) // Ctrl+Shift+Tab
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
                if (vk == 0x25 && _selectedTab != null) // VK_LEFT
                {
                    _selectedTab.BrowserHost.NavigateBack();
                    handled = true;
                    return;
                }
                if (vk == 0x27 && _selectedTab != null) // VK_RIGHT
                {
                    _selectedTab.BrowserHost.NavigateForward();
                    handled = true;
                    return;
                }
                if (vk == 0x26 && _selectedTab != null) // VK_UP
                {
                    _selectedTab.BrowserHost.NavigateUp();
                    handled = true;
                    return;
                }
            }
            else if (mods == ModifierKeys.None && vk == 0x74 && _selectedTab != null) // VK_F5
            {
                _selectedTab.BrowserHost.RefreshView();
                handled = true;
                return;
            }

            // 2. Forward file-view accelerators (F2, Delete, Ctrl+C/V/X/Z/A, Enter, Backspace, Alt+Enter)
            //    to the active IShellView when focus resides inside the native ExplorerBrowser control.
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

        // ==================== TAB GESTURES (Click, Double-Click Close, Middle-Click Close, Wheel) ====================

        private void TabPill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ExplorerTabItem tab)
            {
                if (e.ClickCount == 2)
                {
                    // Clover habit: Double-clicking a tab closes it
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

            // Clover habit: Mouse wheel over tab bar switches tabs
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

        private void TabPill_DragEnter(object sender, DragEventArgs e)
        {
            HandleTabDragHover(sender, e);
        }

        private void TabPill_DragOver(object sender, DragEventArgs e)
        {
            HandleTabDragHover(sender, e);
        }

        private void HandleTabDragHover(object sender, DragEventArgs e)
        {
            // Stage 1: Do not accept direct drop onto tab title; instead hover ~380ms switches tab
            // while keeping the native OLE IDataObject drag loop intact so user drops into native view.
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
                // Activate without calling SetFocus so active Win32 OLE DoDragDrop capture is undisturbed
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

        // ==================== NAVIGATION & ADDRESS BAR ====================

        private void BackButton_Click(object sender, RoutedEventArgs e) => _selectedTab?.BrowserHost.NavigateBack();
        private void ForwardButton_Click(object sender, RoutedEventArgs e) => _selectedTab?.BrowserHost.NavigateForward();
        private void UpButton_Click(object sender, RoutedEventArgs e) => _selectedTab?.BrowserHost.NavigateUp();
        private void RefreshButton_Click(object sender, RoutedEventArgs e) => _selectedTab?.BrowserHost.RefreshView();

        private void AddressTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            AddressBoxBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
            AddressTextBox.SelectAll();
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
                AddressBoxBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));
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
                        AddressBoxBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));
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

        // ==================== WINDOW CAPTION & STATUS BAR ====================

        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void MaxRestoreButton_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void CloseWindowButton_Click(object sender, RoutedEventArgs e) => Close();

        private DateTime _transientStatusUntilUtc = DateTime.MinValue;

        public void SetTransientStatus(string message, int durationMs = 3000)
        {
            StatusLeftText.Text = message;
            _transientStatusUntilUtc = DateTime.UtcNow.AddMilliseconds(durationMs);
        }

        private void RefreshStatusBarCounts()
        {
            if (_isClosingWindow) return;

            if (DateTime.UtcNow >= _transientStatusUntilUtc && _selectedTab != null)
            {
                (int total, int selected) = _selectedTab.BrowserHost.GetItemCounts();
                StatusLeftText.Text = selected > 0
                    ? $"{total} 个项目  |  已选择 {selected} 个项目"
                    : $"{total} 个项目";
            }

            using Process proc = Process.GetCurrentProcess();
            double wsMb = proc.WorkingSet64 / (1024.0 * 1024.0);
            StatusRightText.Text = $"{(Environment.Is64BitProcess ? "x64" : "x86")} · {_tabs.Count} 个标签 · {wsMb:F1} MB";
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
