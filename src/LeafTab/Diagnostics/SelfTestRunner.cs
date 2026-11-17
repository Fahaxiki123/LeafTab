using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LeafTab.Controls;
using LeafTab.Interop;
using LeafTab.Models;
using LeafTab.Services;
using LeafTab.Shared;
using LeafTab.Views;
using Microsoft.Win32;

namespace LeafTab.Diagnostics
{
    internal static class SelfTestRunner
    {
        private static async Task VerifyBackgroundActivationAsync(MainWindow window, StringBuilder log, Action<bool, string> assert)
        {
            if (window.SelectedTab == null) { assert(false, "ActivationGuardTabMissing"); return; }
            var editor = new TextBox { Text = "Isolated typing target", AcceptsReturn = true };
            var typingWindow = new Window { Title = "LeafTab focus regression", Content = editor, Width = 480, Height = 180 };
            WindowState previousState = window.WindowState;
            try
            {
                window.WindowState = WindowState.Minimized;
                typingWindow.Show();
                await Task.Delay(150);
                typingWindow.Activate();
                IntPtr typingHwnd = new System.Windows.Interop.WindowInteropHelper(typingWindow).Handle;
                editor.Focus();
                await Task.Delay(120);
                bool typingReady = editor.IsKeyboardFocusWithin && typingWindow.IsActive;
                IntPtr foregroundBefore = NativeMethods.GetForegroundWindow();
                log.AppendLine($"- ActivationProbeSetup: EditorActive={typingWindow.IsActive}, EditorFocus={editor.IsKeyboardFocusWithin}, EditorIsForeground={foregroundBefore == typingHwnd}");
                int tabsBefore = window.Tabs.Count;

                window.ActivateTab(window.SelectedTab, focusNativeView:true);
                await Task.Delay(100);
                window.SelectedTab.BrowserHost.FocusNativeView();
                await window.HandleExternalLaunchRequestAsync(new LaunchActionRequest { IsComServerLaunch=true }, false);
                await window.HandleExternalLaunchRequestAsync(new LaunchActionRequest { IsBackgroundWarmup=true }, false);
                await Task.Delay(100);
                bool minimizedSafe = window.WindowState == WindowState.Minimized && window.Tabs.Count == tabsBefore &&
                                     editor.IsKeyboardFocusWithin && NativeMethods.GetForegroundWindow() == foregroundBefore;
                assert(typingReady && minimizedSafe, "MinimizedWindowPreservesTypingFocus");
                log.AppendLine($"- MinimizedWindowPreservesTypingFocus: TypingReady={typingReady}, Preserved={minimizedSafe}, NoAddedTabs={window.Tabs.Count == tabsBefore}");

                window.WindowState = WindowState.Normal;
                await Task.Delay(120);
                typingWindow.Activate();
                editor.Focus();
                await Task.Delay(100);
                foregroundBefore = NativeMethods.GetForegroundWindow();
                window.SelectedTab.BrowserHost.FocusNativeView();
                bool inactiveSafe = editor.IsKeyboardFocusWithin && NativeMethods.GetForegroundWindow() == foregroundBefore;
                assert(inactiveSafe, "InactiveWindowPreservesTypingFocus");
                log.AppendLine($"- InactiveWindowPreservesTypingFocus: Preserved={inactiveSafe}");

                window.WindowState = WindowState.Minimized;
                await window.HandleExternalLaunchRequestAsync(new LaunchActionRequest(), false);
                await Task.Delay(120);
                bool explicitOpenWorks = window.WindowState != WindowState.Minimized && window.Tabs.Count == tabsBefore + 1;
                assert(explicitOpenWorks, "ExplicitOpenStillRestoresWindow");
                log.AppendLine($"- ExplicitOpenStillRestoresWindow: Restored={explicitOpenWorks}");
            }
            finally
            {
                typingWindow.Close();
                window.WindowState = previousState == WindowState.Minimized ? WindowState.Normal : previousState;
                window.BringWindowToFront();
            }
        }

        private sealed class ResourceSnapshot
        {
            public string Label { get; set; } = string.Empty;
            public int TabCount { get; set; }
            public double WorkingSetMb { get; set; }
            public double PrivateBytesMb { get; set; }
            public uint GdiObjects { get; set; }
            public uint UserObjects { get; set; }
        }

        public static async Task RunAndExitAsync(MainWindow window, string outputPath)
        {
            DiagnosticLog.DirectoryPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!, "self-test-logs");
            var log = new StringBuilder();
            var failures = new List<string>();

            void AssertCheck(bool condition, string name)
            {
                if (!condition)
                    failures.Add(name);
            }

            try
            {
                await Task.Delay(900);

                string exePath = Assembly.GetExecutingAssembly().Location;
                long exeBytes = File.Exists(exePath) ? new FileInfo(exePath).Length : 0;
                string projectDir = ResolveProjectDirectory();

                log.AppendLine("# LeafTab v0.6.9 Native Regression Diagnostics");
                log.AppendLine($"- Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                log.AppendLine($"- OSVersion: {Environment.OSVersion} (64BitOS={Environment.Is64BitOperatingSystem}, 64BitProc={Environment.Is64BitProcess})");
                log.AppendLine($"- CLR Version: {Environment.Version}");
                log.AppendLine($"- Executable: {exePath} ({exeBytes / 1024.0:F1} KB)");
                log.AppendLine($"- ResolvedProjectDir: {projectDir}");
                log.AppendLine();

                // ==================== 1. SPECIAL & VIRTUAL FOLDERS ====================
                log.AppendLine("## 1. Folder & Namespace Navigation Checks");

                ResourceSnapshot? coldStartupSnapshot = null;
                ExplorerTabItem? tab1 = window.SelectedTab;
                if (tab1 != null)
                {
                    await WaitForDirectUiAsync(tab1.BrowserHost, 2500);
                    coldStartupSnapshot = CaptureSnapshot("冷启动首屏纯净基准 (1 个标签 · 此电脑，零操作前)", window.Tabs.Count);
                    log.AppendLine($"- [冷启动首屏资源基准] Tabs={coldStartupSnapshot.TabCount}, WorkingSet={coldStartupSnapshot.WorkingSetMb:F1} MB, PrivateBytes={coldStartupSnapshot.PrivateBytesMb:F1} MB, GDI={coldStartupSnapshot.GdiObjects}, USER={coldStartupSnapshot.UserObjects}");

                    List<string> thisPcItems = tab1.BrowserHost.GetDisplayedItemNames(30);
                    log.AppendLine($"- [此电脑] Title='{tab1.Title}', WindowTitle='{window.Title}', ItemCount={thisPcItems.Count}, Items=[{string.Join(", ", thisPcItems)}]");
                    AssertCheck(string.Equals(window.Title, "此电脑", StringComparison.Ordinal), "ThisPcWindowTitle");

                    await ProbeThisPcGesturesAsync(tab1.BrowserHost, log);
                }

                bool projNavOk = await TestNavigateFolderAsync(window, projectDir, $"当前项目目录 {projectDir}", log);
                AssertCheck(projNavOk && window.SelectedTab != null, "ProjectNavigation");
                if (projNavOk && window.SelectedTab != null)
                {
                    await ProbeDirectoryGesturesAsync(window.SelectedTab.BrowserHost, FOLDERVIEWMODE.FVM_DETAILS, "详细信息视图 (FVM_DETAILS)", log);
                    await ProbeDirectoryGesturesAsync(window.SelectedTab.BrowserHost, FOLDERVIEWMODE.FVM_ICON, "图标视图 (FVM_ICON)", log);
                    window.SelectedTab.BrowserHost.TrySetViewStyle(ExplorerViewStyle.Details);
                }

                // ==================== 2. STAGE 2 FEATURE VERIFICATION ====================
                log.AppendLine();
                log.AppendLine("## 2. Stage 2 Feature Checks (Breadcrumbs, History Stack, Async Search, Reopen Tab, config.json)");

                if (window.SelectedTab != null)
                {
                    bool titleMatchesFolderOnly = string.Equals(window.Title, window.SelectedTab.Title, StringComparison.Ordinal) &&
                                                  !window.Title.EndsWith(" - LeafTab", StringComparison.OrdinalIgnoreCase);
                    log.AppendLine($"- WindowTitleMatchesFolderOnly: WindowTitle='{window.Title}', TabTitle='{window.SelectedTab.Title}', Matched={titleMatchesFolderOnly}");
                    AssertCheck(titleMatchesFolderOnly, "WindowTitleMatchesFolderOnly");

                    var crumbScroll = (ScrollViewer)window.FindName("BreadcrumbScrollViewer");
                    var addrEditGrid = (Grid)window.FindName("AddressEditGrid");
                    var addrTextBox = (TextBox)window.FindName("AddressTextBox");
                    window.ExitAddressEditMode();
                    var previewEvt = new System.Windows.Input.MouseButtonEventArgs(
                        System.Windows.Input.Mouse.PrimaryDevice,
                        Environment.TickCount,
                        System.Windows.Input.MouseButton.Left)
                    {
                        RoutedEvent = System.Windows.Input.Mouse.PreviewMouseDownEvent,
                        Source = crumbScroll
                    };
                    crumbScroll.RaiseEvent(previewEvt);
                    if (!previewEvt.Handled)
                    {
                        var bubbleEvt = new System.Windows.Input.MouseButtonEventArgs(
                            System.Windows.Input.Mouse.PrimaryDevice,
                            Environment.TickCount,
                            System.Windows.Input.MouseButton.Left)
                        {
                            RoutedEvent = System.Windows.Input.Mouse.MouseDownEvent,
                            Source = crumbScroll
                        };
                        crumbScroll.RaiseEvent(bubbleEvt);
                    }
                    bool addressSelectedOnBlankClick = addrEditGrid.Visibility == Visibility.Visible &&
                                                       string.Equals(addrTextBox.SelectedText, window.SelectedTab.AddressText, StringComparison.Ordinal) &&
                                                       !string.IsNullOrEmpty(addrTextBox.SelectedText);
                    window.ExitAddressEditMode();
                    log.AppendLine($"- AddressBarBlankClickSelectsAll: Handled={previewEvt.Handled}, SelectedText='{addrTextBox.SelectedText}', Matched={addressSelectedOnBlankClick}");
                    AssertCheck(addressSelectedOnBlankClick, "AddressBarBlankClickSelectsAll");

                    // Verify toggling built-in WPF preview sidebar on the same open folder resizes SHELLDLL_DefView, renders selected file content, and keeps selection
                    string? previewTarget = File.Exists(Path.Combine(projectDir, "README.md"))
                        ? "README.md"
                        : (File.Exists(Path.Combine(projectDir, "LeafTab.sln")) ? "LeafTab.sln" : null);
                    string expectedPreviewKind = string.Equals(previewTarget, "README.md", StringComparison.OrdinalIgnoreCase)
                        ? "Markdown"
                        : "Text";
                    if (previewTarget != null)
                    {
                        await window.SelectedTab.BrowserHost.SelectChildItemWhenReadyAsync(previewTarget);
                    }
                    for (int wait = 0; wait < 20 && window.SelectedTab.BrowserHost.GetShellViewWidthPx() <= 300; wait++)
                    {
                        await Task.Delay(50);
                    }
                    int widthBefore = window.SelectedTab.BrowserHost.GetShellViewWidthPx();

                    window.TogglePreviewPane();
                    for (int wait = 0; wait < 50; wait++)
                    {
                        int curW = window.SelectedTab.BrowserHost.GetShellViewWidthPx();
                        if (window.IsPreviewPaneOpen &&
                            window.SelectedTab.BrowserHost.DirectUiHwnd != IntPtr.Zero &&
                            curW > 100 &&
                            curW <= widthBefore - 150 &&
                            (previewTarget == null || string.Equals(window.CurrentPreviewKindName, expectedPreviewKind, StringComparison.Ordinal)))
                        {
                            break;
                        }
                        await Task.Delay(50);
                    }
                    int widthWithPreview = window.SelectedTab.BrowserHost.GetShellViewWidthPx();
                    string renderedKind = window.CurrentPreviewKindName;
                    bool contentRenderedOk = previewTarget == null || string.Equals(renderedKind, expectedPreviewKind, StringComparison.Ordinal);
                    bool keptSelection = previewTarget == null ||
                                         window.SelectedTab.BrowserHost.GetSelectedPaths().Any(p => p.EndsWith(previewTarget, StringComparison.OrdinalIgnoreCase));

                    window.TogglePreviewPane();
                    for (int wait = 0; wait < 40; wait++)
                    {
                        int curW = window.SelectedTab.BrowserHost.GetShellViewWidthPx();
                        if (!window.IsPreviewPaneOpen &&
                            window.SelectedTab.BrowserHost.DirectUiHwnd != IntPtr.Zero &&
                            Math.Abs(curW - widthBefore) <= 8)
                        {
                            break;
                        }
                        await Task.Delay(50);
                    }
                    int widthAfterOff = window.SelectedTab.BrowserHost.GetShellViewWidthPx();
                    bool previewToggleOk = widthBefore > 300 &&
                                           widthWithPreview > 100 &&
                                           (widthBefore - widthWithPreview) >= 150 &&
                                           Math.Abs(widthAfterOff - widthBefore) <= 8 &&
                                           contentRenderedOk &&
                                           keptSelection;
                    log.AppendLine($"- PreviewPaneToggleSameFolder: Target='{previewTarget}', Kind='{renderedKind}' (Expected='{expectedPreviewKind}'), WidthBefore={widthBefore}, WidthOn={widthWithPreview}, WidthOff={widthAfterOff}, KeptSelection={keptSelection}");
                    AssertCheck(previewToggleOk, "PreviewPaneToggleSameFolder");

                    // Verify clicking toolbar RefreshButton reloads preview content after selected file changes on disk
                    string refreshProbeDir = Path.Combine(Path.GetTempPath(), $"leaftab_selftest_refresh_{Guid.NewGuid():N}");
                    Directory.CreateDirectory(refreshProbeDir);
                    string refreshProbeFile = Path.Combine(refreshProbeDir, "refresh_probe.md");
                    File.WriteAllText(refreshProbeFile, "# 初版标题\n初始预览内容_V1", new UTF8Encoding(false));
                    (IntPtr refreshDirPidl, _) = await PidlManager.TryParseAddressAsync(refreshProbeDir, CancellationToken.None);
                    ExplorerTabItem? refreshProbeTab = null;
                    try
                    {
                        if (refreshDirPidl != IntPtr.Zero)
                        {
                            refreshProbeTab = window.AddNewTab(refreshDirPidl, activate: true);
                            await WaitForDirectUiAsync(refreshProbeTab.BrowserHost, 2000);
                            await refreshProbeTab.BrowserHost.SelectChildItemWhenReadyAsync("refresh_probe.md");

                            window.TogglePreviewPane();
                            bool sawInitialContent = false;
                            for (int wait = 0; wait < 40; wait++)
                            {
                                if (window.CurrentPreviewTextContent.Contains("初始预览内容_V1"))
                                {
                                    sawInitialContent = true;
                                    break;
                                }
                                await Task.Delay(50);
                            }

                            File.WriteAllText(refreshProbeFile, "# 刷新后标题\n更新后预览内容_V2", new UTF8Encoding(false));
                            var toolbarRefreshBtn = (Button)window.FindName("RefreshButton");
                            toolbarRefreshBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

                            bool sawUpdatedContent = false;
                            for (int wait = 0; wait < 45; wait++)
                            {
                                if (window.CurrentPreviewTextContent.Contains("更新后预览内容_V2"))
                                {
                                    sawUpdatedContent = true;
                                    break;
                                }
                                await Task.Delay(50);
                            }

                            if (window.IsPreviewPaneOpen)
                                window.TogglePreviewPane();

                            log.AppendLine($"- ToolbarRefreshUpdatesPreviewPane: InitialLoaded={sawInitialContent}, UpdatedAfterToolbarClick={sawUpdatedContent}");
                            AssertCheck(sawInitialContent && sawUpdatedContent, "ToolbarRefreshUpdatesPreviewPane");
                        }
                        else
                        {
                            AssertCheck(false, "ToolbarRefreshUpdatesPreviewPane");
                        }
                    }
                    finally
                    {
                        PidlManager.Free(ref refreshDirPidl);
                        if (window.IsPreviewPaneOpen)
                            window.TogglePreviewPane();
                        if (refreshProbeTab != null && window.Tabs.Count > 1)
                            window.CloseTab(refreshProbeTab);
                        try { Directory.Delete(refreshProbeDir, recursive: true); } catch { }
                    }

                    // Verify preview splitter & scroll wrappers cannot steal focus and arrow keys navigate images instead of resizing preview pane
                    string imgProbeDir = Path.Combine(Path.GetTempPath(), $"leaftab_selftest_imgnav_{Guid.NewGuid():N}");
                    Directory.CreateDirectory(imgProbeDir);
                    (IntPtr imgDirPidl, _) = await PidlManager.TryParseAddressAsync(imgProbeDir, CancellationToken.None);
                    ExplorerTabItem? imgProbeTab = null;
                    try
                    {
                        foreach (string imgName in new[] { "img_01.png", "img_02.png" })
                        {
                            var wb = new WriteableBitmap(16, 16, 96, 96, PixelFormats.Bgra32, null);
                            var enc = new PngBitmapEncoder();
                            enc.Frames.Add(BitmapFrame.Create(wb));
                            using var fs = new FileStream(Path.Combine(imgProbeDir, imgName), FileMode.Create, FileAccess.Write, FileShare.None);
                            enc.Save(fs);
                        }

                        var splitter = (GridSplitter)window.FindName("PreviewGridSplitter");
                        var metaScroll = (ScrollViewer)window.FindName("PreviewMetadataScroll");
                        var tabsScroll = (ScrollViewer)window.FindName("TabsScrollViewer");
                        var tabsItems = (ItemsControl)window.FindName("TabsItemsControl");
                        var crumbScrollCheck = (ScrollViewer)window.FindName("BreadcrumbScrollViewer");
                        var bmItems = (ItemsControl)window.FindName("BookmarksItemsControl");
                        bool nonFocusableChromeOk =
                            splitter != null && !splitter.Focusable && !splitter.IsTabStop &&
                            metaScroll != null && !metaScroll.Focusable && !metaScroll.IsTabStop &&
                            tabsScroll != null && !tabsScroll.Focusable && !tabsScroll.IsTabStop &&
                            tabsItems != null && !tabsItems.Focusable && !tabsItems.IsTabStop &&
                            crumbScrollCheck != null && !crumbScrollCheck.Focusable && !crumbScrollCheck.IsTabStop &&
                            bmItems != null && !bmItems.Focusable && !bmItems.IsTabStop;

                        bool previewImgNavOk = false;
                        bool splitterRefusedFocus = false;
                        bool previewUpdatedToImg1 = false;
                        bool previewUpdatedToImg2 = false;
                        bool hasForeground = false;
                        bool clickRestoredFocus = false;
                        bool arrowMovedToSecondImg = false;
                        bool widthUnchanged = false;

                        if (nonFocusableChromeOk && imgDirPidl != IntPtr.Zero)
                        {
                            imgProbeTab = window.AddNewTab(imgDirPidl, activate: true);
                            await WaitForDirectUiAsync(imgProbeTab.BrowserHost, 2000);
                            imgProbeTab.BrowserHost.TrySetViewStyle(ExplorerViewStyle.Details);
                            imgProbeTab.BrowserHost.TrySetSortState(PropertyKeys.ItemNameDisplay, ShellConstants.SORT_ASCENDING);
                            await Task.Delay(100);
                            await imgProbeTab.BrowserHost.SelectChildItemWhenReadyAsync("img_01.png");

                            if (!window.IsPreviewPaneOpen)
                                window.TogglePreviewPane();

                            for (int wait = 0; wait < 40; wait++)
                            {
                                if (string.Equals(window.CurrentPreviewKindName, "Image", StringComparison.Ordinal) &&
                                    string.Equals(window.CurrentPreviewTitle, "img_01.png", StringComparison.OrdinalIgnoreCase))
                                {
                                    previewUpdatedToImg1 = true;
                                    break;
                                }
                                await Task.Delay(50);
                            }

                            int widthBeforeKey = imgProbeTab.BrowserHost.GetShellViewWidthPx();

                            bool focusAttempt = splitter!.Focus();
                            Keyboard.Focus(splitter);
                            splitterRefusedFocus = !focusAttempt && !splitter.IsKeyboardFocused && !splitter.IsKeyboardFocusWithin;

                            var previewSidebar = (Border)window.FindName("PreviewSidebarBorder");
                            if (PresentationSource.FromVisual(previewSidebar) is PresentationSource visualSource)
                            {
                                previewSidebar.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, visualSource, Environment.TickCount, Key.Left)
                                {
                                    RoutedEvent = Keyboard.KeyDownEvent
                                });
                                previewSidebar.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, visualSource, Environment.TickCount, Key.Right)
                                {
                                    RoutedEvent = Keyboard.KeyDownEvent
                                });
                            }

                            window.BringWindowToFront();
                            window.Activate();
                            await Task.Delay(120);

                            IntPtr mainHwnd = new WindowInteropHelper(window).Handle;
                            hasForeground = imgProbeTab.BrowserHost.CanReceiveKeyboardFocus;

                            var downMsg = new MSG
                            {
                                hwnd = mainHwnd,
                                message = (int)NativeMethods.WM_KEYDOWN,
                                wParam = new IntPtr(0x28), // VK_DOWN
                                lParam = IntPtr.Zero
                            };
                            if (hasForeground)
                            {
                                NativeMethods.SetFocus(mainHwnd);
                                previewSidebar.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                                {
                                    RoutedEvent = UIElement.MouseLeftButtonDownEvent,
                                    Source = previewSidebar
                                });
                                await Task.Delay(80);
                                clickRestoredFocus = imgProbeTab.BrowserHost.IsNativeFocusInside();

                                NativeMethods.SetFocus(mainHwnd);
                                window.SimulatePreprocessMessageForSelfTest(ref downMsg);

                                for (int wait = 0; wait < 35; wait++)
                                {
                                    if (imgProbeTab.BrowserHost.GetSelectedPaths().Any(p => p.EndsWith("img_02.png", StringComparison.OrdinalIgnoreCase)) &&
                                        string.Equals(window.CurrentPreviewKindName, "Image", StringComparison.Ordinal) &&
                                        string.Equals(window.CurrentPreviewTitle, "img_02.png", StringComparison.OrdinalIgnoreCase))
                                    {
                                        arrowMovedToSecondImg = true;
                                        previewUpdatedToImg2 = true;
                                        break;
                                    }
                                    await Task.Delay(50);
                                }
                            }
                            else
                            {
                                // A background run can check selection-to-preview updates,
                                // but cannot verify foreground clicks or keyboard routing.
                                window.SimulatePreprocessMessageForSelfTest(ref downMsg);
                                await imgProbeTab.BrowserHost.SelectChildItemWhenReadyAsync("img_02.png");
                                imgProbeTab.BrowserHost.InvalidateSelectionCache();
                                window.SyncSelectionAndPreviewForSelfTest();
                                for (int wait = 0; wait < 40; wait++)
                                {
                                    if (string.Equals(window.CurrentPreviewKindName, "Image", StringComparison.Ordinal) &&
                                        string.Equals(window.CurrentPreviewTitle, "img_02.png", StringComparison.OrdinalIgnoreCase))
                                    {
                                        previewUpdatedToImg2 = true;
                                        break;
                                    }
                                    await Task.Delay(50);
                                }
                            }

                            int widthAfterKey = imgProbeTab.BrowserHost.GetShellViewWidthPx();
                            widthUnchanged = widthBeforeKey > 100 && Math.Abs(widthAfterKey - widthBeforeKey) <= 2;
                            previewImgNavOk = nonFocusableChromeOk &&
                                              splitterRefusedFocus &&
                                              previewUpdatedToImg1 &&
                                              previewUpdatedToImg2 &&
                                              (!hasForeground || (clickRestoredFocus && arrowMovedToSecondImg)) &&
                                              widthUnchanged;
                            string clickCheck = hasForeground ? clickRestoredFocus.ToString() : "Skipped";
                            string arrowCheck = hasForeground ? arrowMovedToSecondImg.ToString() : "Skipped";
                            log.AppendLine($"- PreviewImageArrowNavigationKeepsWidth: NonFocusableChrome={nonFocusableChromeOk}, SplitterRefusedFocus={splitterRefusedFocus}, Img1={previewUpdatedToImg1}, Img2={previewUpdatedToImg2}, ForegroundOwned={hasForeground}, ClickRestoredFocus={clickCheck}, ArrowMovedToNext={arrowCheck}, WidthBefore={widthBeforeKey}, WidthAfter={widthAfterKey}, Passed={previewImgNavOk}");
                        }
                        else
                        {
                            log.AppendLine($"- PreviewImageArrowNavigationKeepsWidth: NonFocusableChrome={nonFocusableChromeOk}, Passed=False");
                        }

                        AssertCheck(previewImgNavOk, "PreviewImageArrowNavigationKeepsWidth");
                    }
                    finally
                    {
                        PidlManager.Free(ref imgDirPidl);
                        if (window.IsPreviewPaneOpen)
                            window.TogglePreviewPane();
                        if (imgProbeTab != null && window.Tabs.Count > 1)
                            window.CloseTab(imgProbeTab);
                        try { Directory.Delete(imgProbeDir, recursive: true); } catch { }
                    }

                    var crumbs = PidlManager.BuildBreadcrumbChain(window.SelectedTab.CurrentPidl);
                    string crumbText = string.Join(" > ", crumbs.Select(c => c.Title));
                    log.AppendLine($"- BreadcrumbChain: [{crumbText}] (Count={crumbs.Count})");
                    AssertCheck(crumbs.Count > 0, "BreadcrumbChain");

                    bool canBackBefore = window.SelectedTab.CanNavigateBack;
                    window.SelectedTab.NavigateHistoryStep(-1);
                    await Task.Delay(350);
                    bool canFwdAfterBack = window.SelectedTab.CanNavigateForward;
                    window.SelectedTab.NavigateHistoryStep(+1);
                    await Task.Delay(350);
                    log.AppendLine($"- HistoryStack Back/Forward: CanBackBefore={canBackBefore}, CanFwdAfterBack={canFwdAfterBack}, RestoredTitle='{window.SelectedTab.Title}'");
                    AssertCheck(canBackBefore && canFwdAfterBack, "HistoryBackForward");

                    // Async directory search into IResultsFolder + FolderPath column check
                    var searchSw = Stopwatch.StartNew();
                    int foundCs = await window.SelectedTab.BrowserHost.StartSearchAsync(
                        "*.cs",
                        window.SelectedTab.CurrentPidl,
                        window.SelectedTab.FileSystemPath);
                    searchSw.Stop();
                    bool inSearch = window.SelectedTab.BrowserHost.IsShowingSearchResults;
                    bool hasFolderPathCol = false;
                    for (int w = 0; w < 12; w++)
                    {
                        hasFolderPathCol = window.SelectedTab.BrowserHost.IsSearchFolderPathColumnVisible();
                        if (hasFolderPathCol) break;
                        await Task.Delay(40);
                    }
                    SearchBackendKind fsBackend = window.SelectedTab.BrowserHost.LastSearchBackend;
                    long fsBackendMs = window.SelectedTab.BrowserHost.LastSearchElapsedMs;
                    List<string> searchSample = window.SelectedTab.BrowserHost.GetDisplayedItemNames(5);
                    window.SelectedTab.BrowserHost.ExitSearchMode();
                    await Task.Delay(150);
                    log.AppendLine($"- AsyncSearch '*.cs': MatchedCount={foundCs}, Backend={fsBackend}, BackendMs={fsBackendMs}ms, Elapsed={searchSw.ElapsedMilliseconds}ms, EnteredSearchMode={inSearch}, FolderPathColVisible={hasFolderPathCol}, ExitedCleanly={!window.SelectedTab.BrowserHost.IsShowingSearchResults}, Sample=[{string.Join(", ", searchSample)}]");
                    AssertCheck(inSearch && hasFolderPathCol && !window.SelectedTab.BrowserHost.IsShowingSearchResults && (!Directory.Exists(Path.Combine(projectDir, "src", "LeafTab")) || foundCs > 0), "AsyncSearchResults");

                    // Search filter syntax (ext:txt), LoadMore pagination, OpenItemLocation & Toolbar AutomationProperties.Name
                    string searchUiDir = Path.Combine(Path.GetTempPath(), $"leaftab_selftest_searchui_{Guid.NewGuid():N}");
                    string subLocA = Path.Combine(searchUiDir, "sub_alpha");
                    string subLocB = Path.Combine(searchUiDir, "sub_beta");
                    Directory.CreateDirectory(subLocA);
                    Directory.CreateDirectory(subLocB);
                    File.WriteAllText(Path.Combine(subLocA, "locate_me_alpha.txt"), "alpha");
                    File.WriteAllText(Path.Combine(subLocB, "locate_me_beta.txt"), "beta");
                    File.WriteAllText(Path.Combine(subLocB, "ignored_note.log"), "log");
                    (IntPtr searchUiPidl, _) = await PidlManager.TryParseAddressAsync(searchUiDir, CancellationToken.None);
                    ExplorerTabItem? searchUiTab = null;
                    try
                    {
                        string backAutoName = System.Windows.Automation.AutomationProperties.GetName(window.BackButton);
                        string moreAutoName = System.Windows.Automation.AutomationProperties.GetName(window.MoreMenuButton);
                        bool a11yOk = !string.IsNullOrWhiteSpace(backAutoName) && !string.IsNullOrWhiteSpace(moreAutoName);

                        bool loadMoreOk = false;
                        bool openLocOk = false;
                        if (searchUiPidl != IntPtr.Zero)
                        {
                            searchUiTab = window.AddNewTab(searchUiPidl, activate: true);
                            await WaitForDirectUiAsync(searchUiTab.BrowserHost, 2000);

                            int initialCapped = await searchUiTab.BrowserHost.StartSearchAsync("ext:txt", searchUiTab.CurrentPidl, searchUiDir, maxResults: 1);
                            bool wasTruncated = searchUiTab.BrowserHost.IsSearchTruncated && searchUiTab.BrowserHost.CanLoadMoreSearchResults;
                            int expandedCount = await window.LoadMoreSearchResultsInSelectedTabAsync();
                            loadMoreOk = initialCapped == 1 && wasTruncated && expandedCount == 2 && !searchUiTab.BrowserHost.IsSearchTruncated;

                            bool selectedInSearch = await searchUiTab.BrowserHost.SelectChildItemWhenReadyAsync("locate_me_alpha.txt");
                            bool openedParent = selectedInSearch && await window.OpenSelectedSearchItemLocationAsync();
                            ExplorerTabItem? parentTab = window.SelectedTab;
                            bool selectedInParent = parentTab != null &&
                                !ReferenceEquals(parentTab, searchUiTab) &&
                                (parentTab.FileSystemPath?.EndsWith("sub_alpha", StringComparison.OrdinalIgnoreCase) ?? false) &&
                                parentTab.BrowserHost.GetSelectedPaths().Any(p => p.EndsWith("locate_me_alpha.txt", StringComparison.OrdinalIgnoreCase));
                            openLocOk = openedParent && selectedInParent;

                            if (parentTab != null && !ReferenceEquals(parentTab, searchUiTab) && window.Tabs.Count > 1)
                            {
                                window.CloseTab(parentTab);
                            }
                        }

                        log.AppendLine($"- SearchLoadMoreAndOpenLocation: LoadMoreOk={loadMoreOk}, OpenItemLocationOk={openLocOk}, ToolbarA11yOk={a11yOk}");
                        AssertCheck(loadMoreOk && openLocOk && a11yOk, "SearchLoadMoreAndOpenLocation");
                    }
                    finally
                    {
                        PidlManager.Free(ref searchUiPidl);
                        if (searchUiTab != null && window.Tabs.Contains(searchUiTab) && window.Tabs.Count > 1)
                        {
                            window.CloseTab(searchUiTab);
                        }
                        try { Directory.Delete(searchUiDir, recursive: true); } catch { }
                    }

                    // Async search from '此电脑' (virtual root -> drive letters + file results)
                    ExplorerTabItem thisPcSearchTab = window.OpenNewThisPcTab();
                    await WaitForDirectUiAsync(thisPcSearchTab.BrowserHost, 1500);
                    var pcSearchSw = Stopwatch.StartNew();
                    int pcFound = await thisPcSearchTab.BrowserHost.StartSearchAsync(
                        "C:",
                        thisPcSearchTab.CurrentPidl,
                        thisPcSearchTab.FileSystemPath);
                    pcSearchSw.Stop();
                    bool pcInSearch = thisPcSearchTab.BrowserHost.IsShowingSearchResults;
                    bool pcExited = thisPcSearchTab.BrowserHost.ExitSearchMode();
                    window.CloseTab(thisPcSearchTab);
                    log.AppendLine($"- ThisPcAsyncSearch 'C:': MatchedCount={pcFound}, Elapsed={pcSearchSw.ElapsedMilliseconds}ms, EnteredSearchMode={pcInSearch}, Exited={pcExited}");
                    AssertCheck(pcInSearch && pcExited && pcFound > 0, "ThisPcAsyncSearchResults");

                    // Reopen closed tab (Ctrl+Shift+T)
                    (IntPtr dlPidl, _) = await PidlManager.TryParseAddressAsync("下载", CancellationToken.None);
                    try
                    {
                        ExplorerTabItem tempDlTab = window.AddNewTab(dlPidl, activate: true);
                        await Task.Delay(250);
                        string closedTitle = tempDlTab.Title;
                        window.CloseTab(tempDlTab);
                        bool reopened = window.ReopenLastClosedTab();
                        await Task.Delay(250);
                        log.AppendLine($"- ReopenClosedTab (Ctrl+Shift+T): Closed='{closedTitle}', Reopened={reopened}, ActiveTitle='{window.SelectedTab?.Title}'");
                        AssertCheck(reopened && window.SelectedTab?.Title == closedTitle, "ReopenClosedTab");
                    }
                    finally
                    {
                        PidlManager.Free(ref dlPidl);
                    }

                    // Pre-warmed background '此电脑' tab pool instant switch + reopen history retention check
                    window.EnsurePrewarmedThisPcHostReady();
                    await Task.Delay(350);
                    bool poolArmed = window.HasPrewarmedHost;
                    var prewarmSw = Stopwatch.StartNew();
                    ExplorerTabItem instantTab = window.OpenNewThisPcTab();
                    prewarmSw.Stop();
                    bool directUiImmediate = instantTab.BrowserHost.DirectUiHwnd != IntPtr.Zero;
                    log.AppendLine($"- PrewarmedThisPcTabPool: Armed={poolArmed}, SwitchLatency={prewarmSw.Elapsed.TotalMilliseconds:F2}ms, DirectUiImmediate={directUiImmediate}, ConsumedCleanly={!window.HasPrewarmedHost}");
                    AssertCheck(poolArmed && directUiImmediate && !window.HasPrewarmedHost, "PrewarmedThisPcTabPool");

                    window.EnsurePrewarmedThisPcHostReady();
                    await Task.Delay(300);
                    window.CloseTab(instantTab);
                    bool reopenedThisPc = window.ReopenLastClosedTab();
                    await Task.Delay(200);
                    bool navFromReopened = await window.NavigateAddressInputAsync(projectDir);
                    await Task.Delay(350);
                    bool canBackAfterPrewarmReopen = window.SelectedTab?.CanNavigateBack ?? false;
                    log.AppendLine($"- PrewarmedReopenHistoryRetention: Reopened={reopenedThisPc}, NavigatedProject={navFromReopened}, CanNavigateBack={canBackAfterPrewarmReopen}");
                    AssertCheck(reopenedThisPc && navFromReopened && canBackAfterPrewarmReopen, "PrewarmedReopenHistoryRetention");
                    if (window.SelectedTab != null && window.Tabs.Count > 1)
                    {
                        window.CloseTab(window.SelectedTab);
                    }
                    window.DiscardPrewarmedHost();
                }

                // config.json atomic replace + .bak corruption recovery check
                string tempCfgPath = Path.Combine(Path.GetTempPath(), $"leaftab_selftest_{Guid.NewGuid():N}.json");
                try
                {
                    var v1Cfg = new AppConfig
                    {
                        ShowBookmarkBar = false,
                        ShowPreviewPane = false,
                        SelectedTabIndex = 0
                    };
                    v1Cfg.Bookmarks.Add(new BookmarkConfigItem { Title = "LeafTab_V1", TargetPath = projectDir });
                    v1Cfg.OpenTabs.Add(new SavedTabConfigItem
                    {
                        CurrentPath = projectDir,
                        HistoryPaths = new List<string> { "此电脑", projectDir },
                        HistoryIndex = 1
                    });
                    bool savedV1 = AppConfigService.Save(v1Cfg, tempCfgPath);

                    var v2Cfg = new AppConfig
                    {
                        ShowBookmarkBar = true,
                        ShowPreviewPane = true,
                        SelectedTabIndex = 0
                    };
                    v2Cfg.Bookmarks.Add(new BookmarkConfigItem { Title = "LeafTab_V2", TargetPath = projectDir });
                    bool savedV2 = AppConfigService.Save(v2Cfg, tempCfgPath);
                    AppConfig loadedV2 = AppConfigService.Load(tempCfgPath);

                    // Corrupt primary config.json and verify automatic fallback to config.json.bak (which holds v1Cfg)
                    File.WriteAllText(tempCfgPath, "{ corrupted_json_payload", Encoding.UTF8);
                    AppConfig recoveredFromBak = AppConfigService.Load(tempCfgPath);
                    bool bakRecoveredOk = recoveredFromBak.Bookmarks.Count == 1 &&
                                          recoveredFromBak.Bookmarks[0].Title == "LeafTab_V1" &&
                                          recoveredFromBak.OpenTabs.Count == 1;

                    log.AppendLine($"- ConfigJsonAtomicAndBackupRecovery: SavedV1={savedV1}, SavedV2={savedV2}, LoadedV2Bar={loadedV2.ShowBookmarkBar}, BakRecovered={bakRecoveredOk}");
                    AssertCheck(savedV1 && savedV2 && loadedV2.ShowBookmarkBar && bakRecoveredOk, "ConfigJsonAtomicAndBackupRecovery");
                }
                finally
                {
                    if (File.Exists(tempCfgPath)) File.Delete(tempCfgPath);
                    if (File.Exists(tempCfgPath + ".bak")) File.Delete(tempCfgPath + ".bak");
                    if (File.Exists(tempCfgPath + ".tmp")) File.Delete(tempCfgPath + ".tmp");
                }

                // ==================== 2B. STAGE 3 + v0.6.0 SYSTEM INTEGRATION & UX CHECKS ====================
                log.AppendLine();
                log.AppendLine("## 2B. Stage 3 & v0.6.0 Checks (CLI Parser, Multi-Window, Side Buttons, Smart Reuse, Address Autocomplete, Volatile Guard, HKCU Sandbox)");

                LaunchActionRequest pQuote = LaunchRequestParser.Parse(new[] { "D:\"", "C:\\ " });
                LaunchActionRequest pSelect = LaunchRequestParser.Parse(new[] { "/n,/select,D:\\Work, 2026\\report.pdf" });
                string nonLauncherPath = Path.Combine(Path.GetTempPath(), "LeafTab-non-launcher-" + Guid.NewGuid().ToString("N"), "File Explorer.lnk");
                LaunchActionRequest pExplorerClsid = LaunchRequestParser.Parse(new[] { "::{52205fd8-5dfb-447d-801a-d0b52f2e83e1}", nonLauncherPath });
                LaunchActionRequest pBgQuit = LaunchRequestParser.Parse(new[] { "--background", "--quit", "--new-window", "--handoff" });
                string slnFile = Path.Combine(projectDir, "LeafTab.sln");
                string csprojFile = Path.Combine(projectDir, "src", "LeafTab", "LeafTab.csproj");
                LaunchActionRequest pDirectFile = File.Exists(slnFile)
                    ? LaunchRequestParser.Parse(new[] { slnFile })
                    : LaunchRequestParser.Parse(new[] { $"/select,{slnFile}" });

                bool parserOk = pQuote.Targets.Count == 2 &&
                                pQuote.Targets[0].FolderOrAlias == "D:\\" &&
                                pQuote.Targets[1].FolderOrAlias == "C:\\" &&
                                pSelect.Targets.Count == 1 &&
                                pSelect.Targets[0].FolderOrAlias == "D:\\Work, 2026" &&
                                pSelect.Targets[0].SelectItemName == "report.pdf" &&
                                pDirectFile.Targets.Count == 1 &&
                                pDirectFile.Targets[0].SelectItemName == "LeafTab.sln" &&
                                pExplorerClsid.Targets.Count == 2 &&
                                pExplorerClsid.Targets[0].FolderOrAlias == "此电脑" &&
                                pExplorerClsid.Targets[1].FolderOrAlias == nonLauncherPath &&
                                pBgQuit.IsBackgroundWarmup &&
                                pBgQuit.IsQuitProcess &&
                                pBgQuit.OpenInNewWindow &&
                                pBgQuit.WaitMutexHandoff;
                log.AppendLine($"- LaunchRequestParserChecks: Passed={parserOk} (QuoteFix='{pQuote.Targets[0].FolderOrAlias}', CommaSelect='{pSelect.Targets[0].FolderOrAlias}' -> '{pSelect.Targets[0].SelectItemName}', ExplorerLauncherRedirect='{pExplorerClsid.Targets[0].FolderOrAlias}', Flags={pBgQuit.IsBackgroundWarmup && pBgQuit.IsQuitProcess && pBgQuit.OpenInNewWindow && pBgQuit.WaitMutexHandoff})");
                AssertCheck(parserOk, "LaunchRequestParserChecks");

                // Verify File Explorer launcher stub CLSID & display alias normalize to This PC PIDL
                IntPtr expectedThisPc = PidlManager.CreateThisPcPidl();
                (IntPtr parsedClsidPidl, _) = await PidlManager.TryParseAddressAsync("::{52205fd8-5dfb-447d-801a-d0b52f2e83e1}", CancellationToken.None);
                (IntPtr parsedLabelPidl, _) = await PidlManager.TryParseAddressAsync("文件资源管理器", CancellationToken.None);
                try
                {
                    bool launcherRedirectOk = PidlManager.AreEqual(parsedClsidPidl, expectedThisPc) &&
                                              PidlManager.AreEqual(parsedLabelPidl, expectedThisPc);
                    log.AppendLine($"- ExplorerLauncherClsidRedirect: Passed={launcherRedirectOk}");
                    AssertCheck(launcherRedirectOk, "ExplorerLauncherClsidRedirect");
                }
                finally
                {
                    PidlManager.Free(ref expectedThisPc);
                    PidlManager.Free(ref parsedClsidPidl);
                    PidlManager.Free(ref parsedLabelPidl);
                }

                // Verify DirectUIHWND UISF_HIDEFOCUS + focus-parking on _hostHwnd when 0 items are selected (eliminates 1px black item box)
                if (window.SelectedTab != null)
                {
                    await WaitForDirectUiAsync(window.SelectedTab.BrowserHost, 1500);
                    window.SelectedTab.BrowserHost.FocusNativeView();
                    IntPtr dui = window.SelectedTab.BrowserHost.DirectUiHwnd;
                    long uiState = dui != IntPtr.Zero
                        ? NativeMethods.SendMessageW(dui, NativeMethods.WM_QUERYUISTATE, IntPtr.Zero, IntPtr.Zero).ToInt64()
                        : 0;
                    bool hideFocusSet = (uiState & NativeMethods.UISF_HIDEFOCUS) != 0;
                    int selCount = window.SelectedTab.BrowserHost.GetItemCounts().SelectedItems;
                    IntPtr curFocus = NativeMethods.GetFocus();
                    bool parkedAwayFromDirectUi = selCount > 0 || (curFocus != dui);
                    log.AppendLine($"- DirectUiFocusSuppression: DirectUIHWND=0x{dui.ToInt64():X}, FocusHwnd=0x{curFocus.ToInt64():X}, Selected={selCount}, UIState=0x{uiState:X}, HideFocusActive={hideFocusSet}, ParkedAwayFromDirectUI={parkedAwayFromDirectUi}");
                    AssertCheck(hideFocusSet && parkedAwayFromDirectUi, "DirectUiFocusSuppression");
                }

                // Activate Tab 0 (projectDir, which navigated from '此电脑' -> projectDir) to test Mouse Side Buttons & History-Preserving Tear-Out
                if (window.Tabs.Count > 0)
                {
                    window.ActivateTab(window.Tabs[0], focusNativeView: true);
                    await Task.Delay(150);
                }

                // Verify Mouse Side Buttons (XBUTTON1 = Back, XBUTTON2 = Forward)
                if (window.SelectedTab != null)
                {
                    string beforeSideBtnTitle = window.SelectedTab.Title;
                    bool sideBackOk = window.HandleMouseSideButton(NativeMethods.XBUTTON1);
                    await Task.Delay(300);
                    bool sideFwdOk = window.HandleMouseSideButton(NativeMethods.XBUTTON2);
                    await Task.Delay(300);
                    bool restoredAfterSideBtn = string.Equals(window.SelectedTab.Title, beforeSideBtnTitle, StringComparison.Ordinal);
                    log.AppendLine($"- MouseSideButtons (XBUTTON1/XBUTTON2): Back={sideBackOk}, Forward={sideFwdOk}, RestoredTitle='{window.SelectedTab.Title}' (Matched={restoredAfterSideBtn})");
                    AssertCheck(sideBackOk && sideFwdOk && restoredAfterSideBtn, "MouseSideButtons");
                }

                // Verify Tab Duplication + Tear-Out to Independent Secondary Window + Secondary Window Clean Close
                if (window.SelectedTab != null && App.CurrentApp != null)
                {
                    ExplorerTabItem? dupTab = window.DuplicateTab(window.SelectedTab);
                    await Task.Delay(200);
                    bool dupOk = dupTab != null && window.Tabs.Count >= 2;
                    MainWindow? secondWin = dupTab != null
                        ? await window.MoveTabToNewWindowAsync(dupTab)
                        : null;
                    await Task.Delay(250);
                    bool multiWinOpened = secondWin != null &&
                                          App.CurrentApp.ManagedWindows.Count == 2 &&
                                          secondWin.SelectedTab != null &&
                                          secondWin.SelectedTab.CanNavigateBack;
                    secondWin?.Close();
                    await Task.Delay(150);
                    bool secondaryDisposedCleanly = App.CurrentApp.ManagedWindows.Count == 1 && !window.IsClosingOrClosed;
                    window.BringWindowToFront();
                    log.AppendLine($"- MultiWindowSplitAndClose: Duplicated={dupOk}, SecondWindowOpenedWithHistory={multiWinOpened}, SecondaryClosedCleanly={secondaryDisposedCleanly}");
                    AssertCheck(dupOk && multiWinOpened && secondaryDisposedCleanly, "MultiWindowSplitAndClose");
                }

                // Verify External /select Launch + Smart Duplicate Folder Reuse
                if (File.Exists(csprojFile) && File.Exists(slnFile))
                {
                    int tabsBeforeExt = window.Tabs.Count;
                    // 1) Subfolder src\LeafTab is NOT currently open -> should open a new tab and select LeafTab.csproj
                    var extNewFolderReq = LaunchRequestParser.Parse(new[] { $"/select,{csprojFile}" });
                    await window.HandleExternalLaunchRequestAsync(extNewFolderReq, isInitialLaunch: false);
                    await Task.Delay(280);
                    List<string> selectedCsproj = window.SelectedTab?.BrowserHost.GetSelectedPaths() ?? new List<string>();
                    bool openedNewForUnopenFolder = window.Tabs.Count == tabsBeforeExt + 1 &&
                                                    selectedCsproj.Any(p => p.EndsWith("LeafTab.csproj", StringComparison.OrdinalIgnoreCase));

                    // 2) Re-launching the ALREADY-OPEN src\LeafTab folder -> should REUSE that tab without piling up duplicates!
                    int tabsBeforeReuse = window.Tabs.Count;
                    var extDuplicateFolderReq = LaunchRequestParser.Parse(new[] { Path.GetDirectoryName(csprojFile)! });
                    await window.HandleExternalLaunchRequestAsync(extDuplicateFolderReq, isInitialLaunch: false);
                    await Task.Delay(180);
                    bool reusedExistingTab = window.Tabs.Count == tabsBeforeReuse;

                    // 3) /select on LeafTab.sln whose parent (projectDir) is ALSO already open in tab 0 -> switches back & selects LeafTab.sln!
                    var extSelectExistingTabReq = LaunchRequestParser.Parse(new[] { $"/select,{slnFile}" });
                    await window.HandleExternalLaunchRequestAsync(extSelectExistingTabReq, isInitialLaunch: false);
                    await Task.Delay(250);
                    List<string> selectedSln = window.SelectedTab?.BrowserHost.GetSelectedPaths() ?? new List<string>();
                    bool reusedAndSelectedSln = window.Tabs.Count == tabsBeforeReuse &&
                                                selectedSln.Any(p => p.EndsWith("LeafTab.sln", StringComparison.OrdinalIgnoreCase));

                    log.AppendLine($"- ExternalLaunchAndSmartReuse: NewTabForUnopen={openedNewForUnopenFolder}, ReusedDuplicateFolder={reusedExistingTab}, ReusedAndSelectedSln={reusedAndSelectedSln}");
                    AssertCheck(openedNewForUnopenFolder && reusedExistingTab && reusedAndSelectedSln, "ExternalLaunchAndSmartReuse");

                    while (window.Tabs.Count > tabsBeforeExt)
                    {
                        window.CloseTab(window.Tabs[window.Tabs.Count - 1]);
                    }
                }

                // Verify Address Bar Tool Commands, Subdirectory Autocomplete & Volatile Folder Guard
                bool toolCmdWt = QuickLaunchAndInstallService.IsAddressBarToolCommand("wt", out string parsedTool, out _) && parsedTool == "wt";
                bool toolCmdCode = QuickLaunchAndInstallService.IsAddressBarToolCommand("code .", out string parsedCode, out _) && parsedCode == "code";
                bool toolCmdRejectPath = !QuickLaunchAndInstallService.IsAddressBarToolCommand(@"C:\wt", out _, out _);

                string acQuery = Path.Combine(projectDir, "sr");
                List<AddressSuggestionItem> acItems = QuickLaunchAndInstallService.BuildAddressSuggestions(
                    acQuery,
                    showDefaultHistory: false,
                    window.RecentFolders,
                    Array.Empty<string>());
                bool acMatchedSrc = acItems.Any(s => s.InsertText.EndsWith(@"\src", StringComparison.OrdinalIgnoreCase));

                string volatileTempExe = Path.Combine(Path.GetTempPath(), "LeafTab.exe");
                string fixedInstallExe = QuickLaunchAndInstallService.GetRecommendedInstallExePath();
                bool volatileGuardOk = QuickLaunchAndInstallService.IsVolatileLaunchLocation(volatileTempExe) &&
                                       !QuickLaunchAndInstallService.IsVolatileLaunchLocation(fixedInstallExe);

                log.AppendLine($"- AddressAutocompleteAndVolatileGuard: ToolCmds={toolCmdWt && toolCmdCode && toolCmdRejectPath}, AutocompleteMatchedSrc={acMatchedSrc}, VolatileGuard={volatileGuardOk}");
                AssertCheck(toolCmdWt && toolCmdCode && toolCmdRejectPath && acMatchedSrc && volatileGuardOk, "AddressAutocompleteAndVolatileGuard");

                // Verify Status Bar Timer Throttling when Minimized
                WindowState prevWinState = window.WindowState;
                window.WindowState = WindowState.Minimized;
                await Task.Delay(80);
                bool pausedWhenMinimized = !window.IsStatusPollingActive;
                window.WindowState = prevWinState == WindowState.Minimized ? WindowState.Normal : prevWinState;
                window.BringWindowToFront();
                await Task.Delay(80);
                log.AppendLine($"- MinimizedStatusPollingThrottle: PausedWhenMinimized={pausedWhenMinimized}");
                AssertCheck(pausedWhenMinimized, "MinimizedStatusPollingThrottle");

                await VerifyBackgroundActivationAsync(window, log, AssertCheck);

                // Isolated HKCU sandbox test (strictly NEVER touches real Software\Classes!)
                string sandboxBase = $@"Software\LeafTab\SelfTestSandbox_{Guid.NewGuid():N}";
                string sandboxClasses = $@"{sandboxBase}\Classes";
                string sandboxBackup = $@"{sandboxBase}\Backup";
                try
                {
                    using (RegistryKey priorKey = Registry.CurrentUser.CreateSubKey($@"{sandboxClasses}\Directory\shell"))
                    {
                        priorKey.SetValue(null, "CustomPriorVerb", RegistryValueKind.String);
                    }

                    var ctxOn = ShellIntegrationService.SetContextMenuEnabledCore(true, exePath, sandboxClasses, notifyShell: false);
                    var defOn = ShellIntegrationService.SetDefaultExplorerEnabledCore(true, exePath, sandboxClasses, sandboxBackup, notifyShell: false);
                    bool enabledBoth = ctxOn.Ok && defOn.Ok &&
                                       ShellIntegrationService.IsContextMenuEnabledCore(sandboxClasses) &&
                                       ShellIntegrationService.IsDefaultExplorerEnabledCore(sandboxClasses);

                    // Simulate missing Folder\shell\open\command (schema upgrade) + moved exe and verify startup self-healing
                    Registry.CurrentUser.DeleteSubKeyTree($@"{sandboxClasses}\Folder", throwOnMissingSubKey: false);
                    using (RegistryKey? verbCmd = Registry.CurrentUser.OpenSubKey($@"{sandboxClasses}\Directory\shell\{ShellIntegrationService.DefaultVerbKeyName}\command", writable: true))
                    {
                        verbCmd?.SetValue(null, "\"C:\\OldMovedFolder\\LeafTab.exe\" \"%V \"", RegistryValueKind.String);
                    }
                    bool selfHealed = ShellIntegrationService.RepairRegisteredPathsIfMovedCore(exePath, sandboxClasses, sandboxBackup, notifyShell: false);
                    bool folderOpenUpgraded;
                    using (RegistryKey? folderOpenCmd = Registry.CurrentUser.OpenSubKey($@"{sandboxClasses}\Folder\shell\open\command"))
                    using (RegistryKey? folderNewWinCmd = Registry.CurrentUser.OpenSubKey($@"{sandboxClasses}\Folder\shell\opennewwindow\command"))
                    {
                        folderOpenUpgraded =
                            string.Equals(
                                folderOpenCmd?.GetValue("DelegateExecute") as string,
                                ShellIntegrationService.LeafTabExecuteClsidString,
                                StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(
                                folderNewWinCmd?.GetValue("DelegateExecute") as string,
                                ShellIntegrationService.LeafTabNewWindowExecuteClsidString,
                                StringComparison.OrdinalIgnoreCase);
                    }

                    // Verify volatile Temp copy refuses to overwrite an existing valid non-volatile registration
                    bool volatileRefusedClobber = !ShellIntegrationService.RepairRegisteredPathsIfMovedCore(
                        volatileTempExe,
                        sandboxClasses,
                        sandboxBackup,
                        notifyShell: false);

                    // Disable while owned by LeafTab and verify clean rollback back to "CustomPriorVerb" AND complete cleanup of Folder\shell
                    ShellIntegrationService.SetDefaultExplorerEnabledCore(false, exePath, sandboxClasses, sandboxBackup, notifyShell: false);
                    ShellIntegrationService.SetContextMenuEnabledCore(false, exePath, sandboxClasses, notifyShell: false);

                    string? restoredPrior;
                    using (RegistryKey? dirShell = Registry.CurrentUser.OpenSubKey($@"{sandboxClasses}\Directory\shell"))
                    {
                        restoredPrior = dirShell?.GetValue(null) as string;
                    }
                    bool folderCleanedUp;
                    using (RegistryKey? folderShell = Registry.CurrentUser.OpenSubKey($@"{sandboxClasses}\Folder\shell"))
                    {
                        folderCleanedUp = folderShell == null;
                    }
                    bool cleanRollback = string.Equals(restoredPrior, "CustomPriorVerb", StringComparison.Ordinal) &&
                                         folderCleanedUp &&
                                         !ShellIntegrationService.IsContextMenuEnabledCore(sandboxClasses) &&
                                         !ShellIntegrationService.IsDefaultExplorerEnabledCore(sandboxClasses);

                    // Re-enable, then simulate a third-party file manager taking over (Default); verify ownership protection refuses to clobber it
                    ShellIntegrationService.SetDefaultExplorerEnabledCore(true, exePath, sandboxClasses, sandboxBackup, notifyShell: false);
                    using (RegistryKey? dirShell = Registry.CurrentUser.OpenSubKey($@"{sandboxClasses}\Directory\shell", writable: true))
                    {
                        dirShell?.SetValue(null, "OtherFileManagerVerb", RegistryValueKind.String);
                    }
                    ShellIntegrationService.SetDefaultExplorerEnabledCore(false, exePath, sandboxClasses, sandboxBackup, notifyShell: false);
                    string? afterProtectedUnreg;
                    using (RegistryKey? dirShell = Registry.CurrentUser.OpenSubKey($@"{sandboxClasses}\Directory\shell"))
                    {
                        afterProtectedUnreg = dirShell?.GetValue(null) as string;
                    }
                    bool ownershipProtected = string.Equals(afterProtectedUnreg, "OtherFileManagerVerb", StringComparison.Ordinal);

                    log.AppendLine($"- ShellIntegrationSandbox: Enabled={enabledBoth}, SelfHealed={selfHealed}, FolderOpenAndNewWinUpgraded={folderOpenUpgraded}, VolatileRefusedClobber={volatileRefusedClobber}, OwnershipProtected={ownershipProtected}, RestoredPriorVerb='{restoredPrior}' (CleanRollback={cleanRollback})");
                    AssertCheck(enabledBoth && selfHealed && folderOpenUpgraded && volatileRefusedClobber && ownershipProtected && cleanRollback, "ShellIntegrationSandbox");
                }
                finally
                {
                    Registry.CurrentUser.DeleteSubKeyTree(sandboxBase, throwOnMissingSubKey: false);
                }

                await TestNavigateFolderAsync(window, "回收站", "虚拟目录：回收站", log);
                await TestNavigateFolderAsync(window, @"\\wsl.localhost", "WSL 命名空间 (\\\\wsl.localhost)", log);

                // ==================== 3. UNREACHABLE UNC ASYNC PRE-CHECK ====================
                log.AppendLine();
                log.AppendLine("## 3. Unreachable Network Path Async Pre-Check");
                int uiHeartbeats = 0;
                var heartbeatTimer = new DispatcherTimer(DispatcherPriority.Normal)
                {
                    Interval = TimeSpan.FromMilliseconds(100)
                };
                heartbeatTimer.Tick += (_, _) => uiHeartbeats++;
                heartbeatTimer.Start();

                var uncSw = Stopwatch.StartNew();
                bool uncNavResult = await window.NavigateAddressInputAsync(@"\\192.0.2.1\nonexistent_share");
                uncSw.Stop();
                heartbeatTimer.Stop();

                log.AppendLine($"- UNC \\\\192.0.2.1\\nonexistent_share: NavSucceeded={uncNavResult}, ElapsedMs={uncSw.ElapsedMilliseconds}ms, UiDispatcherHeartbeatsDuringWait={uiHeartbeats}");

                // ==================== 4. 1 / 10 / 20 TABS RESOURCE BENCHMARK ====================
                log.AppendLine();
                log.AppendLine("## 4. Multi-Tab Resource Benchmark (1 / 10 / 20 Tabs)");

                while (window.Tabs.Count > 1)
                {
                    window.CloseTab(window.Tabs[window.Tabs.Count - 1]);
                }
                await window.NavigateAddressInputAsync("此电脑");
                await Task.Delay(500);
                ForceGcAndPump();

                var snapshots = new List<ResourceSnapshot>();
                if (coldStartupSnapshot != null)
                {
                    snapshots.Add(coldStartupSnapshot);
                }
                snapshots.Add(CaptureSnapshot("自检前序操作后回落 1 个标签 (此电脑)", window.Tabs.Count));

                var candidateFolders = new List<string>
                {
                    @"C:\",
                    projectDir,
                    @"C:\Windows",
                    @"C:\Windows\System32",
                    @"C:\Program Files",
                    @"C:\Users",
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                };
                if (Directory.Exists(@"D:\")) candidateFolders.Insert(1, @"D:\");

                string[] sampleFolders = candidateFolders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

                int folderIdx = 0;
                while (window.Tabs.Count < 10)
                {
                    string path = sampleFolders[folderIdx % sampleFolders.Length];
                    folderIdx++;
                    (IntPtr pidl, _) = await PidlManager.TryParseAddressAsync(path, CancellationToken.None);
                    try
                    {
                        ExplorerTabItem t = window.AddNewTab(pidl, activate: true);
                        await WaitForDirectUiAsync(t.BrowserHost, 1500);
                    }
                    finally
                    {
                        PidlManager.Free(ref pidl);
                    }
                }
                await Task.Delay(500);
                ForceGcAndPump();
                snapshots.Add(CaptureSnapshot("10 个标签 (多磁盘与系统目录混合)", window.Tabs.Count));

                while (window.Tabs.Count < 20)
                {
                    string path = sampleFolders[folderIdx % sampleFolders.Length];
                    folderIdx++;
                    (IntPtr pidl, _) = await PidlManager.TryParseAddressAsync(path, CancellationToken.None);
                    try
                    {
                        ExplorerTabItem t = window.AddNewTab(pidl, activate: true);
                        await WaitForDirectUiAsync(t.BrowserHost, 1500);
                    }
                    finally
                    {
                        PidlManager.Free(ref pidl);
                    }
                }
                await Task.Delay(600);
                ForceGcAndPump();
                snapshots.Add(CaptureSnapshot("20 个标签 (满载保活状态)", window.Tabs.Count));

                while (window.Tabs.Count > 2)
                {
                    window.CloseTab(window.Tabs[window.Tabs.Count - 1]);
                }
                await Task.Delay(400);
                ForceGcAndPump();
                snapshots.Add(CaptureSnapshot("从 20 标签关闭回落至 2 标签 (压测起点)", window.Tabs.Count));

                // ==================== 5. 50x OPEN / CLOSE STABILITY CURVE ====================
                for (int i = 1; i <= 50; i++)
                {
                    string path = sampleFolders[i % sampleFolders.Length];
                    (IntPtr pidl, _) = await PidlManager.TryParseAddressAsync(path, CancellationToken.None);
                    ExplorerTabItem tempTab;
                    try
                    {
                        tempTab = window.AddNewTab(pidl, activate: true);
                    }
                    finally
                    {
                        PidlManager.Free(ref pidl);
                    }

                    await WaitForDirectUiAsync(tempTab.BrowserHost, 1200);
                    await Task.Delay(40);
                    window.CloseTab(tempTab);
                    await Task.Delay(25);

                    if (i % 10 == 0)
                    {
                        ForceGcAndPump();
                        snapshots.Add(CaptureSnapshot($"反复开关第 {i}/50 次后 (保持 2 标签)", window.Tabs.Count));
                    }
                }

                await Task.Delay(600);
                ForceGcAndPump();
                snapshots.Add(CaptureSnapshot("50 次开关静置收敛后 (保持 2 标签)", window.Tabs.Count));

                foreach (ResourceSnapshot s in snapshots)
                {
                    log.AppendLine($"- [{s.Label}] Tabs={s.TabCount}, WorkingSet={s.WorkingSetMb:F1} MB, PrivateBytes={s.PrivateBytesMb:F1} MB, GDI={s.GdiObjects}, USER={s.UserObjects}");
                }

                // Dedicated native selection fixture: no user documents are modified.
                string selectionDir = Path.Combine(Path.GetTempPath(), "LeafTab-selection-" + Guid.NewGuid().ToString("N"));
                ExplorerTabItem? selectionTab = null;
                try
                {
                    Directory.CreateDirectory(selectionDir);
                    for (int i = 0; i < 1000; i++) File.WriteAllText(Path.Combine(selectionDir, $"item-{i:D4}.txt"), string.Empty);
                    selectionTab = window.AddNewTab(IntPtr.Zero, activate: true, animateOpen: false);
                    bool selectionNavigation = await TestNavigateFolderAsync(window, selectionDir, "全选测试目录", log);
                    if (selectionNavigation && window.SelectedTab != null)
                    {
                        var host = window.SelectedTab.BrowserHost;
                        for (int i = 0; i < 50 && host.GetItemCounts().TotalItems != 1000; i++) await Task.Delay(100);
                        var timer = Stopwatch.StartNew();
                        host.SelectAllItems();
                        for (int i = 0; i < 100 && host.GetItemCounts().SelectedItems != 1000; i++) await Task.Delay(50);
                        timer.Stop();
                        bool allSelected = host.GetItemCounts().SelectedItems == 1000;
                        log.AppendLine($"- NativeSelectAll1000: Selected={host.GetItemCounts().SelectedItems}, ElapsedMs={timer.ElapsedMilliseconds}");
                        AssertCheck(allSelected, "NativeSelectAll1000");
                    }
                    else AssertCheck(false, "NativeSelectAllNavigation");
                }
                finally
                {
                    // Release the native folder view before deleting its fixture, including on failure.
                    try { if (selectionTab != null) window.CloseTab(selectionTab); }
                    finally
                    {
                        for (int attempt = 0; Directory.Exists(selectionDir); attempt++)
                        {
                            try { Directory.Delete(selectionDir, recursive: true); }
                            catch (IOException) when (attempt < 5) { await Task.Delay(100); }
                        }
                        log.AppendLine("- NativeSelectAllFixtureCleaned: " + !Directory.Exists(selectionDir));
                    }
                }

                double oldWidth = window.Width;
                window.Width = 720;
                var overflowTabs = new List<ExplorerTabItem>();
                IntPtr overflowPidl = PidlManager.CreateThisPcPidl();
                try
                {
                    for (int i = 0; i < 30; i++) overflowTabs.Add(window.AddNewTab(overflowPidl, false, false, deferHostMount: true));
                    window.ActivateTab(overflowTabs[overflowTabs.Count - 1]);
                    await Task.Delay(200);
                    window.UpdateLayout();
                    var scroll = (ScrollViewer)window.FindName("TabsScrollViewer");
                    var items = (ItemsControl)window.FindName("TabsItemsControl");
                    var last = (FrameworkElement)items.ItemContainerGenerator.ContainerFromItem(overflowTabs[overflowTabs.Count - 1]);
                    Point position = last.TranslatePoint(new Point(0, 0), scroll);
                    bool inside = position.X >= -1 && position.X + last.ActualWidth <= scroll.ActualWidth + 1;
                    AssertCheck(scroll.ScrollableWidth > 0 && scroll.ClipToBounds && inside, "NarrowWindowTabOverflow");
                    log.AppendLine($"- NarrowWindowTabOverflow: Inside={inside}, Viewport={scroll.ActualWidth:F1}, LastTabX={position.X:F1}, Width={last.ActualWidth:F1}");
                }
                finally
                {
                    PidlManager.Free(ref overflowPidl);
                    foreach (var tab in overflowTabs) window.CloseTab(tab);
                    window.Width = oldWidth;
                }

                log.AppendLine();
                if (failures.Count == 0)
                {
                    log.AppendLine("## RESULT: PASS (All Automated Assertions Succeeded)");
                }
                else
                {
                    log.AppendLine($"## RESULT: FAIL ({failures.Count} Assertion(s) Failed: {string.Join(", ", failures)})");
                    Environment.ExitCode = 1;
                }
            }
            catch (Exception ex)
            {
                Environment.ExitCode = 1;
                log.AppendLine();
                log.AppendLine("## ERROR");
                log.AppendLine(ex.ToString());
            }

            string fullOut = Path.GetFullPath(outputPath);
            string? outDir = Path.GetDirectoryName(fullOut);
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            File.WriteAllText(fullOut, log.ToString(), Encoding.UTF8);
            window.Close();
        }

        private static string ResolveProjectDirectory()
        {
            string cur = Environment.CurrentDirectory;
            if (Directory.Exists(cur))
                return cur;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            DirectoryInfo? dir = new DirectoryInfo(baseDir);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "LeafTab.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }

            return baseDir;
        }

        private static async Task<bool> TestNavigateFolderAsync(MainWindow window, string address, string description, StringBuilder log)
        {
            var sw = Stopwatch.StartNew();
            bool ok = await window.NavigateAddressInputAsync(address);
            if (ok && window.SelectedTab != null)
            {
                await WaitForDirectUiAsync(window.SelectedTab.BrowserHost, 2000);
                await Task.Delay(250);
                sw.Stop();
                List<string> items = window.SelectedTab.BrowserHost.GetDisplayedItemNames(15);
                log.AppendLine($"- [{description}] Input='{address}' -> OK ({sw.ElapsedMilliseconds}ms), Title='{window.SelectedTab.Title}', Count={items.Count}, Sample=[{string.Join(", ", items)}]");
                return true;
            }

            sw.Stop();
            log.AppendLine($"- [{description}] Input='{address}' -> FAILED/UNAVAILABLE ({sw.ElapsedMilliseconds}ms)");
            return false;
        }

        private static async Task ProbeDirectoryGesturesAsync(
            ExplorerBrowserHost host,
            FOLDERVIEWMODE mode,
            string modeLabel,
            StringBuilder log)
        {
            host.TrySetViewMode(mode);
            await Task.Delay(350);
            host.AttachDirectUiSubclassIfNeeded();

            IntPtr directUi = host.DirectUiHwnd;
            if (directUi == IntPtr.Zero)
            {
                log.AppendLine($"  - GestureProbe [{modeLabel}]: DirectUIHWND not found");
                return;
            }

            var elements = await Task.Run(() => InspectDirectUiElementsFromMta(directUi));
            log.AppendLine($"  - GestureProbe [{modeLabel}]: DirectUIHWND=0x{directUi.ToInt64():X}, DiscoveredElements={elements.Count}");

            int checkedCount = 0;
            foreach (var el in elements)
            {
                if (string.Equals(el.ClassName, "UIItem", StringComparison.Ordinal) &&
                    el.Bounds.Width > 8 && el.Bounds.Height > 8 &&
                    checkedCount < 4)
                {
                    checkedCount++;
                    POINT center = new POINT((el.Bounds.Left + el.Bounds.Right) / 2, (el.Bounds.Top + el.Bounds.Bottom) / 2);

                    UiaHitResult midHit = await Task.Run(() =>
                        UiaGestureDetector.Shared.ProbePointFromMta(GestureRequestKind.MiddleClickFolder, directUi, center));

                    IntPtr resolvedPidl = host.TryResolveUnambiguousFolderPidl(midHit);
                    string resolvedInfo = "REJECTED (Non-folder or ambiguous)";
                    if (resolvedPidl != IntPtr.Zero)
                    {
                        FolderDisplayInfo info = PidlManager.GetDisplayInfo(resolvedPidl);
                        resolvedInfo = $"FOLDER_RESOLVED='{info.AddressText}'";
                        PidlManager.Free(ref resolvedPidl);
                    }

                    UiaHitResult dblHitOnItem = await Task.Run(() =>
                        UiaGestureDetector.Shared.ProbePointFromMta(GestureRequestKind.DoubleClickBlank, directUi, center));

                    log.AppendLine($"    * Item '{el.Name}' (AutoId='{el.AutoId}'): MiddleClick -> {midHit.Outcome} ({midHit.ElapsedMs:F2}ms, {resolvedInfo}); DoubleClickOnItem -> {dblHitOnItem.Outcome} (Leaf='{dblHitOnItem.RawLeafClassName}')");
                }
            }

            if (NativeMethods.GetWindowRect(directUi, out RECT viewRect) && viewRect.Width > 120 && viewRect.Height > 120)
            {
                POINT blankPt = new POINT(viewRect.Left + 160, viewRect.Bottom - 30);
                UiaHitResult blankHit = await Task.Run(() =>
                    UiaGestureDetector.Shared.ProbePointFromMta(GestureRequestKind.DoubleClickBlank, directUi, blankPt));
                log.AppendLine($"    * BlankArea ({blankPt.X},{blankPt.Y}): DoubleClick -> {blankHit.Outcome} ({blankHit.ElapsedMs:F2}ms, Leaf='{blankHit.RawLeafClassName}')");
            }
        }

        private static async Task ProbeThisPcGesturesAsync(ExplorerBrowserHost host, StringBuilder log)
        {
            IntPtr directUi = host.DirectUiHwnd;
            if (directUi == IntPtr.Zero) return;

            var elements = await Task.Run(() => InspectDirectUiElementsFromMta(directUi));
            foreach (var el in elements)
            {
                if (string.Equals(el.ClassName, "UIGroupItem", StringComparison.Ordinal))
                {
                    POINT headerPt = new POINT(el.Bounds.Left + 45, el.Bounds.Top + 12);
                    UiaHitResult groupHdrDbl = await Task.Run(() =>
                        UiaGestureDetector.Shared.ProbePointFromMta(GestureRequestKind.DoubleClickBlank, directUi, headerPt));
                    log.AppendLine($"  - [此电脑 GroupHeader '{el.Name}'] DoubleClick -> {groupHdrDbl.Outcome} (Leaf='{groupHdrDbl.RawLeafClassName}')");
                    break;
                }
            }

            foreach (var el in elements)
            {
                if (string.Equals(el.ClassName, "UIItem", StringComparison.Ordinal) && el.Bounds.Width > 10 && el.Bounds.Height > 10)
                {
                    POINT center = new POINT((el.Bounds.Left + el.Bounds.Right) / 2, (el.Bounds.Top + el.Bounds.Bottom) / 2);
                    UiaHitResult midHit = await Task.Run(() =>
                        UiaGestureDetector.Shared.ProbePointFromMta(GestureRequestKind.MiddleClickFolder, directUi, center));
                    IntPtr resolvedPidl = host.TryResolveUnambiguousFolderPidl(midHit);
                    bool resolved = resolvedPidl != IntPtr.Zero;
                    string targetAddr = resolved ? PidlManager.GetDisplayInfo(resolvedPidl).AddressText : "NONE";
                    PidlManager.Free(ref resolvedPidl);

                    log.AppendLine($"  - [此电脑 Item '{el.Name}'] MiddleClick -> {midHit.Outcome} ({midHit.ElapsedMs:F2}ms), Resolved={resolved} ('{targetAddr}')");
                }
            }
        }

        private static List<(string ClassName, string Name, string AutoId, RECT Bounds)> InspectDirectUiElementsFromMta(IntPtr directUiHwnd)
        {
            var results = new List<(string, string, string, RECT)>();
            Type? t8 = Type.GetTypeFromCLSID(new Guid("e22ad333-b25f-460c-83d0-0581107395c9"), false);
            if (t8 == null || Activator.CreateInstance(t8) is not IUIAutomation uia)
                return results;

            try
            {
                if (uia.ElementFromHandle(directUiHwnd, out IUIAutomationElement? root) != ShellConstants.S_OK || root == null)
                    return results;

                try
                {
                    uia.get_RawViewWalker(out IUIAutomationTreeWalker? walker);
                    if (walker == null) return results;
                    try
                    {
                        WalkElements(walker, root, 0, results);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(walker);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(root);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(uia);
            }

            return results;
        }

        private static void WalkElements(
            IUIAutomationTreeWalker walker,
            IUIAutomationElement parent,
            int depth,
            List<(string, string, string, RECT)> sink)
        {
            if (depth > 5 || sink.Count > 50) return;

            if (walker.GetFirstChildElement(parent, out IUIAutomationElement? child) != ShellConstants.S_OK || child == null)
                return;

            IUIAutomationElement? current = child;
            while (current != null && sink.Count <= 40)
            {
                current.get_CurrentClassName(out string? cls);
                current.get_CurrentName(out string? name);
                current.get_CurrentAutomationId(out string? autoId);
                current.get_CurrentBoundingRectangle(out RECT bounds);

                if (string.Equals(cls, "UIItem", StringComparison.Ordinal) ||
                    string.Equals(cls, "UIGroupItem", StringComparison.Ordinal))
                {
                    sink.Add((cls ?? string.Empty, name ?? string.Empty, autoId ?? string.Empty, bounds));
                }

                WalkElements(walker, current, depth + 1, sink);

                int hr = walker.GetNextSiblingElement(current, out IUIAutomationElement? next);
                Marshal.ReleaseComObject(current);
                current = hr == ShellConstants.S_OK ? next : null;
            }
        }

        private static async Task WaitForDirectUiAsync(ExplorerBrowserHost host, int maxWaitMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < maxWaitMs)
            {
                host.AttachDirectUiSubclassIfNeeded();
                if (host.DirectUiHwnd != IntPtr.Zero && host.GetItemCounts().TotalItems >= 0)
                {
                    await Task.Delay(120);
                    host.AttachDirectUiSubclassIfNeeded();
                    if (host.DirectUiHwnd != IntPtr.Zero)
                        return;
                }
                await Task.Delay(80);
            }
        }

        private static void ForceGcAndPump()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Application.Current?.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        }

        private static ResourceSnapshot CaptureSnapshot(string label, int tabCount)
        {
            using Process proc = Process.GetCurrentProcess();
            proc.Refresh();
            uint gdi = NativeMethods.GetGuiResources(proc.Handle, NativeMethods.GR_GDIOBJECTS);
            uint user = NativeMethods.GetGuiResources(proc.Handle, NativeMethods.GR_USEROBJECTS);

            return new ResourceSnapshot
            {
                Label = label,
                TabCount = tabCount,
                WorkingSetMb = proc.WorkingSet64 / (1024.0 * 1024.0),
                PrivateBytesMb = proc.PrivateMemorySize64 / (1024.0 * 1024.0),
                GdiObjects = gdi,
                UserObjects = user
            };
        }
    }
}
