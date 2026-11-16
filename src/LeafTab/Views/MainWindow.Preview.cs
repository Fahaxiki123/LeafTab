using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LeafTab.Controls;
using LeafTab.Interop;
using LeafTab.Models;
using LeafTab.Services;

namespace LeafTab.Views
{
    public partial class MainWindow
    {
        private const int RemotePreviewTimeoutMs = 3500;
        private bool? _previewMarkdownUserOverrideRaw;

        private void PreviewPaneButton_Click(object sender, RoutedEventArgs e)
        {
            TogglePreviewPane();
            _selectedTab?.BrowserHost.FocusNativeView();
        }

        private void PreviewCloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_showPreviewPane)
            {
                TogglePreviewPane();
                _selectedTab?.BrowserHost.FocusNativeView();
            }
        }

        private void PreviewRawToggleButton_Click(object sender, RoutedEventArgs e)
        {
            bool currentlyRaw = _previewMarkdownUserOverrideRaw
                ?? (_previewMarkdownRawMode || (_currentPreviewData?.MarkdownAutoRawFallback ?? false));
            bool nextRaw = !currentlyRaw;
            _previewMarkdownUserOverrideRaw = nextRaw;
            if (_currentPreviewData?.MarkdownAutoRawFallback != true)
            {
                _previewMarkdownRawMode = nextRaw;
            }

            if (_currentPreviewData != null && _currentPreviewData.Kind == FilePreviewKind.Markdown)
            {
                RenderLoadedPreviewData(_currentPreviewData, _currentPreviewData.HeaderIcon ?? PreviewHeaderIcon.Source);
            }
            RestoreNativeFocusFromPreviewChrome();
        }

        private void PreviewGridSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (PreviewPaneColumn.Width.IsAbsolute && PreviewPaneColumn.Width.Value >= 220)
            {
                _previewPaneWidthDip = PreviewPaneColumn.Width.Value;
                ScheduleSessionSave();
            }
            _selectedTab?.BrowserHost.SyncBrowserRect();
            RestoreNativeFocusFromPreviewChrome();
        }

        private void PreviewSidebarBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.Handled)
                return;

            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(RestoreNativeFocusFromPreviewChrome));
        }

        private void RestoreNativeFocusFromPreviewChrome()
        {
            if (!_isClosingWindow &&
                !AddressTextBox.IsKeyboardFocusWithin &&
                !SearchTextBox.IsKeyboardFocusWithin &&
                !IsPreviewContentFocused())
            {
                _selectedTab?.BrowserHost.FocusNativeView();
            }
        }

        private void PreviewOpenDefaultButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPreviewData == null ||
                !_currentPreviewData.CanOpenWithDefaultApp ||
                string.IsNullOrWhiteSpace(_currentPreviewData.FilePath) ||
                !File.Exists(_currentPreviewData.FilePath))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(_currentPreviewData.FilePath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SetTransientStatus($"无法打开文件：{ex.Message}");
            }
        }

        internal void TogglePreviewPane()
        {
            _showPreviewPane = !_showPreviewPane;
            UpdatePreviewButtonVisual();

            foreach (ExplorerTabItem tab in _tabs)
            {
                tab.BrowserHost.SetPreviewPaneVisible(_showPreviewPane);
            }

            _prewarmedThisPcHost?.SetPreviewPaneVisible(_showPreviewPane);

            ApplyPreviewPaneLayout();
            SetTransientStatus(_showPreviewPane ? "已开启右侧预览窗格" : "已关闭右侧预览窗格");
            ScheduleSessionSave();
        }

        private double ComputePreviewMaxWidth()
        {
            double refWidth = ActualWidth > 400
                ? ActualWidth
                : (!double.IsNaN(Width) && Width > 400 ? Width : 1160);
            return Math.Max(220, Math.Min(refWidth * 0.55, refWidth - 330));
        }

        private void ApplyPreviewPaneLayout()
        {
            if (_showPreviewPane)
            {
                PreviewGridSplitter.Visibility = Visibility.Visible;
                PreviewSidebarBorder.Visibility = Visibility.Visible;
                PreviewSplitterColumn.Width = new GridLength(5);
                PreviewPaneColumn.MinWidth = 220;

                double maxW = ComputePreviewMaxWidth();
                PreviewPaneColumn.MaxWidth = maxW;
                PreviewPaneColumn.Width = new GridLength(Math.Max(220, Math.Min(_previewPaneWidthDip, maxW)));

                _lastPreviewSelectionKey = string.Empty;
                _selectedTab?.BrowserHost.InvalidateSelectionCache();
            }
            else
            {
                CancelPendingPreviewLoad();
                _currentPreviewData = null;
                _lastPreviewSelectionKey = string.Empty;
                ClearPreviewBodyContent();

                // Reset MinWidth/MaxWidth before setting Width=0 so Grid column collapses cleanly to zero width.
                PreviewPaneColumn.MinWidth = 0;
                PreviewPaneColumn.MaxWidth = double.PositiveInfinity;
                PreviewSplitterColumn.Width = new GridLength(0);
                PreviewPaneColumn.Width = new GridLength(0);
                PreviewGridSplitter.Visibility = Visibility.Collapsed;
                PreviewSidebarBorder.Visibility = Visibility.Collapsed;
            }

            ContentSurfaceGrid.UpdateLayout();
            _selectedTab?.BrowserHost.SyncBrowserRect();

            if (_showPreviewPane && _selectedTab != null)
            {
                UpdateCommandBarAndStatus(computeBytes: true);
            }
        }

        private void CancelPendingPreviewLoad()
        {
            _previewRequestVersion++;
            if (_previewLoadCts != null)
            {
                try { _previewLoadCts.Cancel(); } catch { }
                _previewLoadCts.Dispose();
                _previewLoadCts = null;
            }
        }

        private void ClearPreviewBodyContent()
        {
            PreviewHeaderIcon.Source = null;
            PreviewHeaderTitle.Text = "预览";
            PreviewHeaderSubtitle.Text = "未选择任何项目";
            PreviewRawToggleButton.Visibility = Visibility.Collapsed;
            PreviewNoticeBanner.Visibility = Visibility.Collapsed;
            PreviewNoticeText.Text = string.Empty;
            PreviewDocumentViewer.Document = null;
            PreviewTextBox.Text = string.Empty;
            PreviewImageControl.Source = null;
            PreviewCardIcon.Source = null;
            PreviewMetadataRowsPanel.Children.Clear();
            PreviewCardNote.Text = string.Empty;
            PreviewCardNote.Visibility = Visibility.Collapsed;
            PreviewOpenDefaultButton.Visibility = Visibility.Collapsed;
            PreviewEmptyText.Text = "选择一个文件以预览内容";
            ShowPreviewBodyElement(PreviewEmptyPanel);
        }

        private void ShowPreviewBodyElement(UIElement activeElement)
        {
            bool hadPreviewTextFocus = PreviewTextBox.IsKeyboardFocusWithin || PreviewDocumentViewer.IsKeyboardFocusWithin;
            PreviewEmptyPanel.Visibility = ReferenceEquals(activeElement, PreviewEmptyPanel) ? Visibility.Visible : Visibility.Collapsed;
            PreviewDocumentViewer.Visibility = ReferenceEquals(activeElement, PreviewDocumentViewer) ? Visibility.Visible : Visibility.Collapsed;
            PreviewTextBox.Visibility = ReferenceEquals(activeElement, PreviewTextBox) ? Visibility.Visible : Visibility.Collapsed;
            PreviewImagePanel.Visibility = ReferenceEquals(activeElement, PreviewImagePanel) ? Visibility.Visible : Visibility.Collapsed;
            PreviewMetadataScroll.Visibility = ReferenceEquals(activeElement, PreviewMetadataScroll) ? Visibility.Visible : Visibility.Collapsed;

            if (hadPreviewTextFocus &&
                !ReferenceEquals(activeElement, PreviewTextBox) &&
                !ReferenceEquals(activeElement, PreviewDocumentViewer) &&
                !AddressTextBox.IsKeyboardFocusWithin &&
                !SearchTextBox.IsKeyboardFocusWithin)
            {
                _selectedTab?.BrowserHost.FocusNativeView();
            }
        }

        private void RefreshPreviewPaneForSelection(
            SelectionStatusInfo sel,
            (string? FileSystemPath, string? DisplayName, bool IsFolder) single)
        {
            if (!_showPreviewPane || _isClosingWindow)
                return;

            string key;
            if (sel.SelectedItems <= 0)
            {
                key = "EMPTY";
            }
            else if (sel.SelectedItems > 1)
            {
                key = $"MULTI|{sel.SelectedItems}|{sel.SelectedSizeBytes}|{sel.HasSizedFileSelection}";
            }
            else
            {
                key = $"ONE|{single.FileSystemPath ?? string.Empty}|{single.DisplayName ?? string.Empty}|{single.IsFolder}";
            }

            if (string.Equals(key, _lastPreviewSelectionKey, StringComparison.Ordinal))
                return;

            _lastPreviewSelectionKey = key;
            _previewMarkdownUserOverrideRaw = null;
            CancelPendingPreviewLoad();

            if (sel.SelectedItems <= 0)
            {
                _currentPreviewData = null;
                PreviewHeaderIcon.Source = _selectedTab?.Icon;
                PreviewHeaderTitle.Text = "预览";
                PreviewHeaderSubtitle.Text = "未选择任何项目";
                PreviewRawToggleButton.Visibility = Visibility.Collapsed;
                PreviewNoticeBanner.Visibility = Visibility.Collapsed;
                PreviewDocumentViewer.Document = null;
                PreviewTextBox.Text = string.Empty;
                PreviewImageControl.Source = null;
                PreviewEmptyText.Text = "选择一个文件以预览内容";
                ShowPreviewBodyElement(PreviewEmptyPanel);
                return;
            }

            if (sel.SelectedItems > 1)
            {
                _currentPreviewData = null;
                PreviewHeaderIcon.Source = null;
                PreviewHeaderTitle.Text = $"已选择 {sel.SelectedItems} 个项目";
                PreviewHeaderSubtitle.Text = sel.HasSizedFileSelection
                    ? $"总大小 {PidlManager.FormatByteSize(sel.SelectedSizeBytes)}"
                    : "多项选择";
                PreviewRawToggleButton.Visibility = Visibility.Collapsed;
                PreviewNoticeBanner.Visibility = Visibility.Collapsed;
                PreviewDocumentViewer.Document = null;
                PreviewTextBox.Text = string.Empty;
                PreviewImageControl.Source = null;
                PreviewEmptyText.Text = sel.HasSizedFileSelection
                    ? $"已选择 {sel.SelectedItems} 个项目（共 {PidlManager.FormatByteSize(sel.SelectedSizeBytes)}）"
                    : $"已选择 {sel.SelectedItems} 个项目";
                ShowPreviewBodyElement(PreviewEmptyPanel);
                return;
            }

            int requestVersion = _previewRequestVersion;
            var cts = new CancellationTokenSource();
            _previewLoadCts = cts;
            _ = LoadAndRenderSingleItemPreviewAsync(
                single.FileSystemPath,
                single.DisplayName,
                single.IsFolder,
                requestVersion,
                cts);
        }

        private async Task LoadAndRenderSingleItemPreviewAsync(
            string? filePath,
            string? displayName,
            bool isFolder,
            int requestVersion,
            CancellationTokenSource cts)
        {
            try
            {
                Task<FilePreviewData> loadTask = FilePreviewService.LoadPreviewAsync(
                    filePath,
                    displayName,
                    isFolder,
                    typeName: null,
                    cts.Token);

                FilePreviewData data;
                if (IsRemoteNetworkPath(filePath))
                {
                    Task completed = await Task.WhenAny(
                        loadTask,
                        Task.Delay(RemotePreviewTimeoutMs, cts.Token)).ConfigureAwait(true);

                    if (!ReferenceEquals(completed, loadTask))
                    {
                        if (_isClosingWindow || !_showPreviewPane || requestVersion != _previewRequestVersion || cts.IsCancellationRequested)
                            return;

                        data = BuildRemotePreviewTimeoutFallback(filePath!, displayName, isFolder);
                    }
                    else
                    {
                        data = await loadTask.ConfigureAwait(true);
                    }
                }
                else
                {
                    data = await loadTask.ConfigureAwait(true);
                }

                if (_isClosingWindow || !_showPreviewPane || requestVersion != _previewRequestVersion || cts.IsCancellationRequested)
                    return;

                _currentPreviewData = data;
                RenderLoadedPreviewData(data, data.HeaderIcon);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static bool IsRemoteNetworkPath(string? path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   path!.StartsWith(@"\\", StringComparison.Ordinal) &&
                   !path.StartsWith(@"\\wsl", StringComparison.OrdinalIgnoreCase);
        }

        private static FilePreviewData BuildRemotePreviewTimeoutFallback(
            string filePath,
            string? displayName,
            bool isFolder)
        {
            string title = !string.IsNullOrWhiteSpace(displayName)
                ? displayName!
                : Path.GetFileName(filePath);
            if (string.IsNullOrWhiteSpace(title))
                title = filePath;

            string subtitle = isFolder ? "网络文件夹" : "网络项目";
            var card = new FilePreviewData
            {
                Kind = FilePreviewKind.MetadataCard,
                FilePath = filePath,
                Title = title,
                Subtitle = subtitle,
                StatusMessage = "网络路径响应较慢，已跳过深层内容读取以保持窗口流畅。"
            };
            card.MetadataRows.Add(new FilePreviewMetadataRow { Label = "类型", Value = subtitle });
            card.MetadataRows.Add(new FilePreviewMetadataRow { Label = "路径", Value = filePath });
            return card;
        }

        private void RenderLoadedPreviewData(FilePreviewData data, ImageSource? largeIcon)
        {
            PreviewHeaderIcon.Source = largeIcon ?? _selectedTab?.Icon;
            PreviewHeaderTitle.Text = data.Title;
            PreviewHeaderSubtitle.Text = data.Subtitle;

            bool markdownUseRaw = data.Kind == FilePreviewKind.Markdown &&
                (_previewMarkdownUserOverrideRaw ?? (_previewMarkdownRawMode || data.MarkdownAutoRawFallback));

            string notice = string.Empty;
            if (data.IsTruncated)
            {
                notice = $"文件较大（{PidlManager.FormatByteSize((ulong)Math.Max(0, data.TotalFileBytes))}），仅展示前 96 KB 预览内容";
            }
            if (data.Kind == FilePreviewKind.Markdown && !string.IsNullOrWhiteSpace(data.MarkdownComplexityNotice))
            {
                string mdNote = markdownUseRaw
                    ? data.MarkdownComplexityNotice
                    : $"文档结构较密集，当前仅渲染前部节选排版（点击「源码」可查看完整预览文本）";
                notice = string.IsNullOrEmpty(notice) ? mdNote : $"{notice}；{mdNote}";
            }

            if (!string.IsNullOrEmpty(notice))
            {
                PreviewNoticeBanner.Visibility = Visibility.Visible;
                PreviewNoticeText.Text = notice;
            }
            else
            {
                PreviewNoticeBanner.Visibility = Visibility.Collapsed;
                PreviewNoticeText.Text = string.Empty;
            }

            switch (data.Kind)
            {
                case FilePreviewKind.Markdown:
                    PreviewImageControl.Source = null;
                    PreviewRawToggleButton.Visibility = Visibility.Visible;
                    PreviewRawToggleButton.Content = markdownUseRaw ? "渲染" : "源码";

                    if (markdownUseRaw)
                    {
                        PreviewDocumentViewer.Document = null;
                        PreviewTextBox.Text = data.TextContent;
                        PreviewTextBox.ScrollToHome();
                        ShowPreviewBodyElement(PreviewTextBox);
                    }
                    else
                    {
                        try
                        {
                            MarkdownParsedDocument parsed = data.ParsedMarkdown
                                ?? FilePreviewService.ParseMarkdownStructure(data.TextContent);
                            PreviewDocumentViewer.Document = FilePreviewService.BuildMarkdownDocument(parsed, OpenSafePreviewUri);
                            ShowPreviewBodyElement(PreviewDocumentViewer);
                        }
                        catch
                        {
                            PreviewDocumentViewer.Document = null;
                            PreviewTextBox.Text = data.TextContent;
                            PreviewTextBox.ScrollToHome();
                            ShowPreviewBodyElement(PreviewTextBox);
                        }
                    }
                    break;

                case FilePreviewKind.Text:
                    PreviewImageControl.Source = null;
                    PreviewRawToggleButton.Visibility = Visibility.Collapsed;
                    PreviewDocumentViewer.Document = null;
                    PreviewTextBox.Text = data.TextContent;
                    PreviewTextBox.ScrollToHome();
                    ShowPreviewBodyElement(PreviewTextBox);
                    break;

                case FilePreviewKind.Image:
                    PreviewRawToggleButton.Visibility = Visibility.Collapsed;
                    PreviewDocumentViewer.Document = null;
                    PreviewTextBox.Text = string.Empty;
                    PreviewImageControl.Source = data.Image;
                    ShowPreviewBodyElement(PreviewImagePanel);
                    break;

                default:
                    PreviewImageControl.Source = null;
                    PreviewRawToggleButton.Visibility = Visibility.Collapsed;
                    PreviewDocumentViewer.Document = null;
                    PreviewTextBox.Text = string.Empty;
                    PopulatePreviewMetadataCard(data, largeIcon);
                    ShowPreviewBodyElement(PreviewMetadataScroll);
                    break;
            }
        }

        private void PopulatePreviewMetadataCard(FilePreviewData data, ImageSource? largeIcon)
        {
            PreviewCardIcon.Source = largeIcon ?? _selectedTab?.Icon;
            PreviewCardTitle.Text = data.Title;
            PreviewCardType.Text = data.Subtitle;

            PreviewMetadataRowsPanel.Children.Clear();
            Brush labelBrush = UiBrushes.Get(Color.FromRgb(0x64, 0x74, 0x8B));
            Brush valueBrush = UiBrushes.Get(Color.FromRgb(0x0F, 0x17, 0x2A));

            for (int i = 0; i < data.MetadataRows.Count; i++)
            {
                FilePreviewMetadataRow meta = data.MetadataRows[i];
                var rowGrid = new Grid
                {
                    Margin = i < data.MetadataRows.Count - 1 ? new Thickness(0, 0, 0, 7) : new Thickness(0)
                };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var lbl = new TextBlock
                {
                    Text = meta.Label,
                    FontSize = 11.5,
                    Foreground = labelBrush,
                    VerticalAlignment = VerticalAlignment.Top
                };
                Grid.SetColumn(lbl, 0);

                var val = new TextBlock
                {
                    Text = meta.Value,
                    FontSize = 11.5,
                    Foreground = valueBrush,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Top
                };
                Grid.SetColumn(val, 1);

                rowGrid.Children.Add(lbl);
                rowGrid.Children.Add(val);
                PreviewMetadataRowsPanel.Children.Add(rowGrid);
            }

            if (!string.IsNullOrWhiteSpace(data.StatusMessage))
            {
                PreviewCardNote.Text = data.StatusMessage;
                PreviewCardNote.Visibility = Visibility.Visible;
            }
            else
            {
                PreviewCardNote.Text = string.Empty;
                PreviewCardNote.Visibility = Visibility.Collapsed;
            }

            PreviewOpenDefaultButton.Visibility = data.CanOpenWithDefaultApp ? Visibility.Visible : Visibility.Collapsed;
            PreviewMetadataScroll.ScrollToTop();
        }

        private static void OpenSafePreviewUri(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri)
                return;

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch
            {
            }
        }

        private void UpdatePreviewButtonVisual()
        {
            if (_showPreviewPane)
            {
                PreviewPaneButton.Background = UiBrushes.Get(Color.FromArgb(0x22, 0x00, 0x7A, 0xFF));
                PreviewPaneButton.BorderBrush = UiBrushes.Get(Color.FromArgb(0x48, 0x00, 0x7A, 0xFF));
                PreviewPaneText.Foreground = UiBrushes.Get(Color.FromRgb(0x00, 0x62, 0xCC));
                PreviewPaneText.FontWeight = FontWeights.Medium;
            }
            else
            {
                PreviewPaneButton.Background = Brushes.Transparent;
                PreviewPaneButton.BorderBrush = Brushes.Transparent;
                PreviewPaneText.Foreground = UiBrushes.Get(Color.FromRgb(0x1E, 0x29, 0x3B));
                PreviewPaneText.FontWeight = FontWeights.Normal;
            }
        }
    }
}
