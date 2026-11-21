using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LeafTab.Controls;
using LeafTab.Interop;
using LeafTab.Models;
using LeafTab.Services;
using LeafTab.Shared;

namespace LeafTab.Views
{
    public partial class MainWindow
    {
        private Task _addressSuggestionsTask = Task.CompletedTask;
        private string? _addressSuggestionsInput;
        private int _addressCompletionVersion;
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
            RefreshActiveTab(focusNativeView: true);
        }

        internal void RefreshActiveTab(bool focusNativeView)
        {
            if (_selectedTab == null)
                return;

            _lastPreviewSelectionKey = string.Empty;
            if (_selectedTab.BrowserHost.IsShowingSearchResults && !string.IsNullOrWhiteSpace(_selectedTab.SearchQuery))
            {
                FastFileSearchService.Shared.InvalidateCache();
                _ = ExecuteSearchOnTabAsync(_selectedTab, _selectedTab.SearchQuery);
            }
            else
            {
                _selectedTab.BrowserHost.RefreshView();
                UpdateCommandBarAndStatus(computeBytes: true);
                if (_showPreviewPane)
                {
                    SchedulePostRefreshPreviewSync(_selectedTab);
                }
            }

            if (focusNativeView)
            {
                _selectedTab.BrowserHost.FocusNativeView();
            }
        }

        private async void SchedulePostRefreshPreviewSync(ExplorerTabItem tab)
        {
            try
            {
                await Task.Delay(140).ConfigureAwait(true);
                if (_isClosingWindow || !_showPreviewPane || !ReferenceEquals(_selectedTab, tab))
                    return;

                tab.BrowserHost.InvalidateSelectionCache();
                UpdateCommandBarAndStatus(computeBytes: true);
            }
            catch
            {
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
                    Foreground = UiBrushes.Get(Color.FromRgb(0x00, 0x62, 0xCC)),
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
                    Foreground = UiBrushes.Get(
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
                        Stroke = UiBrushes.Get(Color.FromRgb(0x94, 0xA3, 0xB8)),
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
            if (sender is FrameworkElement element && element.Tag is BreadcrumbNode node && _selectedTab != null)
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

        private async void BreadcrumbChevron_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not BreadcrumbNode node || _selectedTab == null)
                return;

            e.Handled = true;
            var menu = CreateLiquidGlassContextMenu(btn);
            var lifetime = new CancellationTokenSource();
            CancellationToken token = lifetime.Token;
            int closed = 0;
            int activeOps = 1; // 追踪目录枚举与各页图标异步任务，避免门关闭后提前释放 CTS

            void ReleaseOp()
            {
                if (Interlocked.Decrement(ref activeOps) == 0 && Volatile.Read(ref closed) != 0)
                {
                    try { lifetime.Dispose(); } catch { }
                }
            }

            void CloseWhenDetached(object s, RoutedEventArgs args) => menu.IsOpen = false;
            void CleanupOnClose()
            {
                if (Interlocked.Exchange(ref closed, 1) != 0)
                    return;

                btn.Unloaded -= CloseWhenDetached;
                try { lifetime.Cancel(); } catch { }
                if (Volatile.Read(ref activeOps) == 0)
                {
                    try { lifetime.Dispose(); } catch { }
                }
            }

            btn.Unloaded += CloseWhenDetached;
            menu.Closed += (_, _) => CleanupOnClose();
            menu.Items.Add(new MenuItem { Header = "正在读取子文件夹…", IsEnabled = false });
            menu.IsOpen = true;
            if (!menu.IsOpen)
            {
                CleanupOnClose();
                ReleaseOp();
                return;
            }

            try
            {
                BreadcrumbChildrenResult result = await PidlManager.GetChildFoldersResultAsync(node.PidlBytes, token);
                if (token.IsCancellationRequested)
                    return;

                ShowBreadcrumbFolderPagesCore(
                    menu,
                    result.Children,
                    token,
                    result.IsTruncated,
                    onIconWorkStart: () => Interlocked.Increment(ref activeOps),
                    onIconWorkEnd: ReleaseOp);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                DiagnosticLog.Write("breadcrumb-enumeration-failed", ex);
                if (token.IsCancellationRequested) return;
                menu.Items.Clear();
                menu.Items.Add(new MenuItem { Header = "无法读取子文件夹", IsEnabled = false });
                ResetContextMenuScrollToTop(menu);
            }
            finally
            {
                ReleaseOp();
            }
        }

        private void ShowBreadcrumbFolderPages(ContextMenu menu, List<BreadcrumbNode> children, CancellationToken token) =>
            ShowBreadcrumbFolderPagesCore(menu, children, token, isTruncated: false, onIconWorkStart: null, onIconWorkEnd: null);

        private void ShowBreadcrumbFolderPagesCore(
            ContextMenu menu,
            List<BreadcrumbNode> children,
            CancellationToken token,
            bool isTruncated,
            Action? onIconWorkStart,
            Action? onIconWorkEnd)
        {
            const int pageSize = 80;
            CancellationTokenSource? loadingIcons = null;
            async void ShowPage(int start)
            {
                if (token.IsCancellationRequested)
                    return;

                try { loadingIcons?.Cancel(); } catch { }
                menu.Items.Clear();
                if (children.Count == 0)
                {
                    menu.Items.Add(new MenuItem { Header = "（无子文件夹）", IsEnabled = false });
                    ResetContextMenuScrollToTop(menu);
                    return;
                }

                void AddPageButton(string title, int offset)
                {
                    var button = new MenuItem { Header = title, StaysOpenOnClick = true };
                    button.Click += (_, _) => ShowPage(offset);
                    menu.Items.Add(button);
                }

                if (start > 0) AddPageButton("上一页", Math.Max(0, start - pageSize));
                var page = children.GetRange(start, Math.Min(pageSize, children.Count - start));
                var items = new List<MenuItem>(page.Count);
                foreach (BreadcrumbNode child in page)
                {
                    var item = new MenuItem
                    {
                        Header = child.Title.Replace("_", "__"),
                        Tag = child
                    };
                    item.Click += BreadcrumbSegment_Click;
                    menu.Items.Add(item);
                    items.Add(item);
                }
                if (start + page.Count < children.Count)
                {
                    AddPageButton("下一页", start + page.Count);
                }
                else if (isTruncated)
                {
                    menu.Items.Add(new MenuItem
                    {
                        Header = $"已达安全上限，仅显示前 {children.Count} 个子文件夹",
                        IsEnabled = false
                    });
                }

                ResetContextMenuScrollToTop(menu);

                onIconWorkStart?.Invoke();
                CancellationTokenSource? request = null;
                try
                {
                    try
                    {
                        request = CancellationTokenSource.CreateLinkedTokenSource(token);
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }

                    loadingIcons = request;
                    var icons = await PidlManager.GetChildFolderIconsAsync(page, request.Token);
                    if (request.IsCancellationRequested || token.IsCancellationRequested)
                        return;
                    for (int i = 0; i < items.Count; i++)
                        items[i].Icon = CreateMenuIcon(icons[i]);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { DiagnosticLog.Write("breadcrumb-icons-failed", ex); }
                finally
                {
                    if (request != null)
                    {
                        if (ReferenceEquals(loadingIcons, request))
                            loadingIcons = null;
                        try { request.Dispose(); } catch { }
                    }
                    onIconWorkEnd?.Invoke();
                }
            }
            ShowPage(0);
        }

        private static void ResetContextMenuScrollToTop(ContextMenu menu)
        {
            ScrollViewer? sv = FindVisualDescendant<ScrollViewer>(menu);
            if (sv != null)
            {
                sv.ScrollToTop();
            }
            else
            {
                _ = menu.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    FindVisualDescendant<ScrollViewer>(menu)?.ScrollToTop();
                }));
            }
        }

        private static T? FindVisualDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T match)
                    return match;
                T? nested = FindVisualDescendant<T>(child);
                if (nested != null)
                    return nested;
            }
            return null;
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

        internal static bool ShouldEnterAddressEditFromBreadcrumbClick(DependencyObject? originalSource, DependencyObject? containerStop)
        {
            if (originalSource == null)
                return false;

            DependencyObject? current = originalSource;
            while (current != null && !ReferenceEquals(current, containerStop))
            {
                if (current is System.Windows.Controls.Primitives.ButtonBase)
                    return false;

                if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
                {
                    current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
                }
                else
                {
                    current = LogicalTreeHelper.GetParent(current);
                }
            }

            return true;
        }

        private void BreadcrumbBlank_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (AddressEditGrid.Visibility == Visibility.Visible)
                return;

            if (ShouldEnterAddressEditFromBreadcrumbClick(e.OriginalSource as DependencyObject, AddressBoxBorder))
            {
                EnterAddressEditMode();
                e.Handled = true;
            }
        }

        public void EnterAddressEditMode()
        {
            _suppressAddressSuggestUpdate = true;
            try
            {
                if (_selectedTab != null)
                {
                    AddressTextBox.Text = _selectedTab.AddressText;
                }
            }
            finally
            {
                _suppressAddressSuggestUpdate = false;
            }

            BreadcrumbContainerGrid.Visibility = Visibility.Collapsed;
            AddressEditGrid.Visibility = Visibility.Visible;
            AddressBoxBorder.Background = Brushes.White;
            AddressBoxBorder.BorderBrush = UiBrushes.Get(Color.FromArgb(0xB8, 0x00, 0x7A, 0xFF));
            AddressBoxBorder.BorderThickness = new Thickness(1.4);

            bool selectAllOnFirstFocus = true;
            void FocusAddressEditor()
            {
                int selectionStart = AddressTextBox.SelectionStart;
                int selectionLength = AddressTextBox.SelectionLength;
                // Let WPF acquire the HWND focus together with keyboard focus.
                // A raw SetFocus(root) restores its previously focused native
                // child and can reopen the file-list focus after a rename.
                AddressEditGrid.UpdateLayout();
                AddressTextBox.Focus();
                Keyboard.Focus(AddressTextBox);
                if (selectAllOnFirstFocus) AddressTextBox.SelectAll();
                else AddressTextBox.Select(selectionStart, selectionLength);
                // The Shell's delayed focus handoff must not select text that
                // the user already typed after the first focus request.
                selectAllOnFirstFocus = false;
            }
            if (_selectedTab != null) _selectedTab.BrowserHost.MoveFocusToWpfEditor(FocusAddressEditor);
            else FocusAddressEditor();

            if (!_skipSessionPersistence && IsActive)
            {
                RefreshAddressSuggestions(showDefaultHistory: true);
            }
        }

        public void ExitAddressEditMode()
        {
            _selectedTab?.BrowserHost.CancelPendingWpfEditorFocus();
            _addressCompletionVersion++;
            _addressParseCts?.Cancel();
            AddressBusyIndicator.Visibility = Visibility.Collapsed;
            _addressSuggestCts?.Cancel();
            _addressSuggestionsInput = null;
            AddressSuggestListBox.ItemsSource = null;
            AddressSuggestPopup.IsOpen = false;
            AddressEditGrid.Visibility = Visibility.Collapsed;
            BreadcrumbContainerGrid.Visibility = Visibility.Visible;
            AddressBoxBorder.Background = UiBrushes.Get(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));
            AddressBoxBorder.BorderBrush = UiBrushes.Get(Color.FromArgb(0x16, 0x0F, 0x17, 0x2A));
            AddressBoxBorder.BorderThickness = new Thickness(1);
        }

        private void RefreshAddressSuggestions(bool showDefaultHistory) =>
            _addressSuggestionsTask = RefreshAddressSuggestionsAsync(showDefaultHistory);

        private async Task RefreshAddressSuggestionsAsync(bool showDefaultHistory)
        {
            if (AddressEditGrid.Visibility != Visibility.Visible || _isClosingWindow)
            {
                AddressSuggestPopup.IsOpen = false;
                return;
            }

            var bookmarkPaths = new List<string>(_bookmarks.Count);
            foreach (BookmarkItemViewModel bm in _bookmarks)
            {
                if (!string.IsNullOrWhiteSpace(bm.TargetPath))
                {
                    bookmarkPaths.Add(bm.TargetPath);
                }
            }

            _addressSuggestCts?.Cancel();
            _addressSuggestCts?.Dispose();
            _addressSuggestCts = new CancellationTokenSource();
            CancellationToken token = _addressSuggestCts.Token;
            string text = AddressTextBox.Text;
            _addressSuggestionsInput = null;
            AddressSuggestListBox.ItemsSource = null;
            try
            {
                var suggestions = await QuickLaunchAndInstallService.BuildAddressSuggestionsAsync(
                    text, showDefaultHistory, _recentFolders.ToArray(), bookmarkPaths, token);
                if (token.IsCancellationRequested || _isClosingWindow || AddressEditGrid.Visibility != Visibility.Visible ||
                    !string.Equals(text, AddressTextBox.Text, StringComparison.Ordinal)) return;
                _addressSuggestionsInput = text;
                AddressSuggestListBox.ItemsSource = suggestions;
                AddressSuggestListBox.SelectedIndex = -1;
                AddressSuggestPopup.IsOpen = suggestions.Count > 0 && IsActive;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { DiagnosticLog.Write("address-completion-failed", ex); }
        }

        private void SetAddressTextFromSuggestion(string text)
        {
            _suppressAddressSuggestUpdate = true;
            try
            {
                AddressTextBox.Text = text ?? string.Empty;
                AddressTextBox.CaretIndex = AddressTextBox.Text.Length;
                // Arrow-key cycling still belongs to this same suggestion list.
                _addressSuggestionsInput = AddressTextBox.Text;
            }
            finally
            {
                _suppressAddressSuggestUpdate = false;
            }
        }

        private void AddressHistoryToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (AddressSuggestPopup.IsOpen)
            {
                AddressSuggestPopup.IsOpen = false;
            }
            else
            {
                RefreshAddressSuggestions(showDefaultHistory: true);
            }
            e.Handled = true;
        }

        private void AddressTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressAddressSuggestUpdate ||
                AddressEditGrid.Visibility != Visibility.Visible ||
                !AddressTextBox.IsKeyboardFocusWithin)
            {
                return;
            }

            _addressCompletionVersion++;
            _addressParseCts?.Cancel();
            AddressBusyIndicator.Visibility = Visibility.Collapsed;
            RefreshAddressSuggestions(showDefaultHistory: false);
        }

        private async void AddressTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool completing = e.Key == Key.Tab && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0;
            if (!completing && e.Key != Key.Down && !(e.Key == Key.Up && AddressSuggestPopup.IsOpen)) return;
            // Mark handled before awaiting: WPF must not move focus while completion is pending.
            e.Handled = true;
            await ApplyAddressSuggestionAsync(e.Key, completing && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        }

        private async Task ApplyAddressSuggestionAsync(Key key, bool reverse = false)
        {
            int version = ++_addressCompletionVersion;
            string input = AddressTextBox.Text;
            if (_addressSuggestionsTask.IsCompleted &&
                (!AddressSuggestPopup.IsOpen || !string.Equals(_addressSuggestionsInput, input, StringComparison.Ordinal)))
            {
                bool history = key != Key.Tab && (string.IsNullOrWhiteSpace(input) ||
                    string.Equals(input, _selectedTab?.AddressText, StringComparison.OrdinalIgnoreCase));
                RefreshAddressSuggestions(history);
            }
            await _addressSuggestionsTask;
            if (version != _addressCompletionVersion || _isClosingWindow ||
                AddressEditGrid.Visibility != Visibility.Visible ||
                !string.Equals(input, AddressTextBox.Text, StringComparison.Ordinal) ||
                !string.Equals(_addressSuggestionsInput, input, StringComparison.Ordinal)) return;
            int count = AddressSuggestListBox.Items.Count;
            if (count == 0) return;
            int index = AddressSuggestListBox.SelectedIndex;
            if (key == Key.Up || reverse) index = index <= 0 ? count - 1 : index - 1;
            else if (key == Key.Down) index = (index + 1) % count;
            else if (index < 0) index = 0;
            AddressSuggestListBox.SelectedIndex = index;
            AddressSuggestListBox.ScrollIntoView(AddressSuggestListBox.SelectedItem);
            if (AddressSuggestListBox.SelectedItem is not AddressSuggestionItem candidate) return;
            string completed = candidate.InsertText;
            if (key == Key.Tab && !candidate.IsCommand && Path.IsPathRooted(completed) &&
                !completed.EndsWith("\\", StringComparison.Ordinal)) completed += "\\";
            SetAddressTextFromSuggestion(completed);
            if (key == Key.Tab) RefreshAddressSuggestions(showDefaultHistory: false);
        }

        private async void AddressSuggestItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBoxItem lbi && lbi.DataContext is AddressSuggestionItem item)
            {
                e.Handled = true;
                AddressSuggestPopup.IsOpen = false;
                await NavigateAddressInputAsync(item.InsertText);
            }
        }

        private void AddressTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            AddressBoxBorder.Background = Brushes.White;
            AddressBoxBorder.BorderBrush = UiBrushes.Get(Color.FromArgb(0xB8, 0x00, 0x7A, 0xFF));
            AddressTextBox.SelectAll();
        }

        private void AddressTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (e.NewFocus == null && _selectedTab?.BrowserHost.HasPendingWpfEditorFocus == true)
                return;
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
                string targetInput = (AddressSuggestPopup.IsOpen &&
                                      AddressSuggestListBox.SelectedItem is AddressSuggestionItem selectedItem)
                    ? selectedItem.InsertText
                    : AddressTextBox.Text;
                AddressSuggestPopup.IsOpen = false;
                await NavigateAddressInputAsync(targetInput);
            }
        }

        public async Task<bool> NavigateAddressInputAsync(string inputText)
        {
            if (_selectedTab == null)
                return false;

            if (QuickLaunchAndInstallService.TryExecuteAddressToolCommand(
                    inputText,
                    _selectedTab.FileSystemPath,
                    out string toolStatus))
            {
                ExitAddressEditMode();
                _selectedTab.BrowserHost.FocusNativeView();
                if (!string.IsNullOrEmpty(toolStatus))
                {
                    SetTransientStatus(toolStatus);
                }
                return true;
            }

            _addressParseCts?.Cancel();
            _addressParseCts?.Dispose();
            _addressParseCts = new CancellationTokenSource();
            CancellationToken token = _addressParseCts.Token;

            int reqId = ++_addressNavRequestId;
            ExplorerTabItem targetTab = _selectedTab;
            AddressBusyIndicator.Visibility = Visibility.Visible;

            try
            {
                (IntPtr pidl, string? error) = await PidlManager.TryParseAddressAsync(inputText, token);
                if (token.IsCancellationRequested || reqId != _addressNavRequestId || _isClosingWindow ||
                    !_tabs.Contains(targetTab) || !ReferenceEquals(_selectedTab, targetTab))
                {
                    PidlManager.Free(ref pidl);
                    return false;
                }

                if (pidl == IntPtr.Zero)
                {
                    if (ReferenceEquals(_selectedTab, targetTab))
                    {
                        SetTransientStatus(error ?? "路径无效。");
                    }
                    return false;
                }

                try
                {
                    bool ok = targetTab.BrowserHost.NavigateToPidl(pidl);
                    if (ok)
                    {
                        if (ReferenceEquals(_selectedTab, targetTab))
                        {
                            ExitAddressEditMode();
                            targetTab.BrowserHost.FocusNativeView();
                        }
                    }
                    else if (ReferenceEquals(_selectedTab, targetTab))
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
                if (reqId == _addressNavRequestId)
                {
                    AddressBusyIndicator.Visibility = Visibility.Collapsed;
                }
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
            SearchBoxBorder.BorderBrush = UiBrushes.Get(Color.FromArgb(0xB8, 0x00, 0x7A, 0xFF));
            SearchBoxBorder.BorderThickness = new Thickness(1.4);
        }

        private void SearchTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            SearchBoxBorder.Background = UiBrushes.Get(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));
            SearchBoxBorder.BorderBrush = UiBrushes.Get(Color.FromArgb(0x16, 0x0F, 0x17, 0x2A));
            SearchBoxBorder.BorderThickness = new Thickness(1);
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateSearchBoxVisuals();
            if (_suppressSearchTextChanged || _selectedTab == null)
                return;

            _selectedTab.SearchQuery = SearchTextBox.Text;
            _searchDebounceTimer.Stop();
            _pendingSearchTab = null;

            if (string.IsNullOrWhiteSpace(SearchTextBox.Text))
            {
                if (_selectedTab.BrowserHost.IsShowingSearchResults)
                {
                    _selectedTab.BrowserHost.ExitSearchMode();
                }
                return;
            }

            _pendingSearchTab = _selectedTab;
            _searchDebounceTimer.Start();
        }

        private async void SearchDebounceTimer_Tick(object? sender, EventArgs e)
        {
            _searchDebounceTimer.Stop();
            ExplorerTabItem? target = _pendingSearchTab;
            _pendingSearchTab = null;

            if (target != null) await RunDebouncedSearchAsync(target);
        }

        private async Task RunDebouncedSearchAsync(ExplorerTabItem target)
        {
            try { await ExecuteSearchOnTabAsync(target, target.SearchQuery); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { DiagnosticLog.Write("search-ui-failed", ex); }
        }

        private void CancelSearchForNavigation(ExplorerTabItem tab)
        {
            if (ReferenceEquals(_pendingSearchTab, tab))
            {
                _searchDebounceTimer.Stop();
                _pendingSearchTab = null;
            }
            tab.SearchQuery = string.Empty;
            if (ReferenceEquals(_selectedTab, tab)) SetSearchBoxTextSilently(string.Empty);
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
                _pendingSearchTab = null;
                e.Handled = true;
                try
                {
                    await ExecuteSearchOnSelectedTabAsync();
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { DiagnosticLog.Write("search-ui-failed", ex); }
            }
        }

        private async void SearchSubmitButton_Click(object sender, RoutedEventArgs e)
        {
            _searchDebounceTimer.Stop();
            _pendingSearchTab = null;
            if (string.IsNullOrWhiteSpace(SearchTextBox.Text))
            {
                FocusSearchBox();
                return;
            }
            try
            {
                await ExecuteSearchOnSelectedTabAsync();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { DiagnosticLog.Write("search-ui-failed", ex); }
        }

        private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
        {
            ClearAndExitSearch();
            _selectedTab?.BrowserHost.FocusNativeView();
        }

        private void ClearAndExitSearch()
        {
            _searchDebounceTimer.Stop();
            _pendingSearchTab = null;
            if (_selectedTab != null)
            {
                _selectedTab.SearchQuery = string.Empty;
                _selectedTab.BrowserHost.ExitSearchMode();
            }
            SetSearchBoxTextSilently(string.Empty);
            UpdateNavigationButtonsState();
            UpdateCommandBarAndStatus(computeBytes: false);
        }

        public Task<int> ExecuteSearchOnSelectedTabAsync() =>
            _selectedTab != null
                ? ExecuteSearchOnTabAsync(_selectedTab, SearchTextBox.Text)
                : Task.FromResult(0);

        private async Task<int> ExecuteSearchOnTabAsync(ExplorerTabItem targetTab, string? rawQuery)
        {
            if (targetTab == null || !_tabs.Contains(targetTab))
                return 0;

            string query = (rawQuery ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(query))
                return 0;

            targetTab.SearchQuery = query;
            int found = await targetTab.BrowserHost.StartSearchAsync(
                query,
                targetTab.CurrentPidl,
                targetTab.FileSystemPath);

            if (ReferenceEquals(_selectedTab, targetTab))
            {
                UpdateNavigationButtonsState();
                UpdateCommandBarAndStatus(computeBytes: false);
            }

            return found;
        }

        private async void SearchLoadMoreButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await LoadMoreSearchResultsInSelectedTabAsync();
                _selectedTab?.BrowserHost.FocusNativeView();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { DiagnosticLog.Write("search-ui-failed", ex); }
        }

        internal async Task<int> LoadMoreSearchResultsInSelectedTabAsync()
        {
            if (_selectedTab == null ||
                !_selectedTab.BrowserHost.IsShowingSearchResults ||
                !_selectedTab.BrowserHost.CanLoadMoreSearchResults)
            {
                return _selectedTab?.BrowserHost.SearchMatchCount ?? 0;
            }

            ExplorerTabItem targetTab = _selectedTab;
            int found = await targetTab.BrowserHost.LoadMoreSearchResultsAsync();
            if (ReferenceEquals(_selectedTab, targetTab))
            {
                UpdateNavigationButtonsState();
                UpdateCommandBarAndStatus(computeBytes: false);
            }
            return found;
        }

        private async void OpenItemLocationButton_Click(object sender, RoutedEventArgs e)
        {
            await OpenSelectedSearchItemLocationAsync();
        }

        internal async Task<bool> OpenSelectedSearchItemLocationAsync()
        {
            if (_selectedTab == null || !_selectedTab.BrowserHost.IsShowingSearchResults)
                return false;

            List<string> selectedPaths = _selectedTab.BrowserHost.GetSelectedPaths();
            string? targetItemPath = selectedPaths.Count > 0
                ? selectedPaths[0]
                : _selectedTab.BrowserHost.GetCachedSingleSelection().FileSystemPath;

            if (string.IsNullOrWhiteSpace(targetItemPath))
            {
                SetTransientStatus("请先选择一个搜索结果项目");
                return false;
            }

            string trimmed = targetItemPath!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? parentDir = Path.GetDirectoryName(trimmed);
            string childName = Path.GetFileName(trimmed);

            if (string.IsNullOrWhiteSpace(parentDir) || !Directory.Exists(parentDir))
            {
                SetTransientStatus("无法定位该项目的所在文件夹");
                return false;
            }

            (IntPtr parentPidl, string? parseErr) = await PidlManager.TryParseAddressAsync(parentDir, CancellationToken.None);
            if (parentPidl == IntPtr.Zero)
            {
                SetTransientStatus(parseErr ?? $"无法打开目录：{parentDir}");
                return false;
            }

            ExplorerTabItem targetTab;
            try
            {
                ExplorerTabItem? existingTab = null;
                foreach (ExplorerTabItem candidate in _tabs)
                {
                    if (!candidate.BrowserHost.IsShowingSearchResults &&
                        candidate.CurrentPidl != IntPtr.Zero &&
                        PidlManager.AreEqual(candidate.CurrentPidl, parentPidl))
                    {
                        existingTab = candidate;
                        break;
                    }
                }

                if (existingTab != null)
                {
                    ActivateTab(existingTab, focusNativeView: true);
                    existingTab.BrowserHost.ClearSelection();
                    targetTab = existingTab;
                }
                else
                {
                    targetTab = AddNewTab(parentPidl, activate: true, animateOpen: !_skipSessionPersistence);
                }
            }
            finally
            {
                PidlManager.Free(ref parentPidl);
            }

            if (!string.IsNullOrWhiteSpace(childName))
            {
                await targetTab.BrowserHost.SelectChildItemWhenReadyAsync(childName);
                UpdateCommandBarAndStatus(computeBytes: true);
            }

            SetTransientStatus($"已定位到所在文件夹：{parentDir}");
            return true;
        }
    }
}
