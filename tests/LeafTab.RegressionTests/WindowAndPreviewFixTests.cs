using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LeafTab.Controls;
using LeafTab.Interop;
using LeafTab.Models;
using LeafTab.Services;
using LeafTab.Views;

namespace LeafTab.RegressionTests
{
    internal static partial class Program
    {
        private static readonly BindingFlags WindowPrivate = BindingFlags.Instance | BindingFlags.NonPublic;

        private static void RunWindowAndPreviewFixTests()
        {
            Test("A slow network preview renders a timeout card only for the current selection", () => WithReviewWindow(window => RunWindowTask(async () =>
            {
                const string path = @"\\fixture\share\report.txt";
                WindowField("_showPreviewPane").SetValue(window, true);
                FieldInfo current = WindowField("_currentPreviewData");
                int version = (int)WindowField("_previewRequestVersion").GetValue(window)!;
                var selection = new CancellationTokenSource();
                CancellationToken selectionToken = selection.Token;
                CancellationToken readToken = default;
                var pending = new TaskCompletionSource<FilePreviewData>(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    await window.LoadAndRenderSingleItemPreviewAsync(path, "report.txt", false, version, selection,
                        token => { readToken = token; return pending.Task; }, remoteTimeoutMs: 20);
                    var card = (FilePreviewData?)current.GetValue(window);
                    Check(card?.FilePath == path && card.StatusMessage.Contains("网络路径响应较慢"));
                    Check(!selectionToken.IsCancellationRequested && readToken.WaitHandle.WaitOne(0));
                }
                finally { pending.TrySetResult(new FilePreviewData()); }

                object? previous = current.GetValue(window);
                var nextSelection = new CancellationTokenSource();
                var nextRead = new TaskCompletionSource<FilePreviewData>(TaskCreationOptions.RunContinuationsAsynchronously);
                CancellationToken nextReadToken = default;
                try
                {
                    WindowField("_previewLoadCts").SetValue(window, nextSelection);
                    Task stale = window.LoadAndRenderSingleItemPreviewAsync(path, "report.txt", false, version, nextSelection,
                        token => { nextReadToken = token; return nextRead.Task; });
                    WindowMethod("CancelPendingPreviewLoad").Invoke(window, null);
                    await stale;
                    Check(nextReadToken.IsCancellationRequested);
                    nextRead.SetException(new IOException("The canceled read finished late."));
                    Check(ReferenceEquals(previous, current.GetValue(window)));
                }
                finally { nextRead.TrySetResult(new FilePreviewData()); }
            })));
            Test("A retiring STA exits whether its last callback finished before or after retirement", () =>
            {
                foreach (bool finishFirst in new[] { true, false })
                {
                    var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var worker = new PidlManager.ShellStaLane.LaneWorker("retirement regression", _ => exited.TrySetResult(true));
                    Dispatcher dispatcher = worker.EnsureDispatcher();
                    try
                    {
                        if (finishFirst) finished.SetResult(true);
                        Check(worker.TryRetireAfter(finished.Task));
                        if (!finishFirst)
                        {
                            dispatcher.Invoke(new Action(() => Check(!dispatcher.HasShutdownStarted)));
                            finished.SetResult(true);
                        }
                        Check(exited.Task.Wait(3000), "Retired worker did not exit.");
                    }
                    finally
                    {
                        finished.TrySetResult(true);
                        if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
                    }
                }
            });
            Test("A Shell error after a child PIDL is reported instead of ending the folder listing", () =>
            {
                const int denied = unchecked((int)0x80070005);
                var interrupted = new FolderEnumeratorStub(denied);
                Check(PidlManager.NextChildPidl(interrupted) == new IntPtr(1));
                bool failed = false;
                try { PidlManager.NextChildPidl(interrupted); }
                catch (Exception ex) when (ex.HResult == denied) { failed = true; }
                Check(failed, "The Shell enumeration failure was swallowed.");
                var complete = new FolderEnumeratorStub(ShellConstants.S_FALSE);
                Check(PidlManager.NextChildPidl(complete) == new IntPtr(1));
                Check(PidlManager.NextChildPidl(complete) == IntPtr.Zero);
            });
            Test("A full-length plain Markdown paragraph parses within the preview budget", () =>
            {
                string paragraph = new string('a', FilePreviewService.MaxTextPreviewBytes);
                FilePreviewService.ParseMarkdownStructure("warmup");
                var timer = Stopwatch.StartNew();
                MarkdownParsedDocument document = FilePreviewService.ParseMarkdownStructure(paragraph);
                timer.Stop();
                Check(((MdParagraphBlock)document.Blocks.Single()).Inlines.Single().Text == paragraph);
                Check(timer.ElapsedMilliseconds < 1500, "Parsing plain Markdown took " + timer.ElapsedMilliseconds + " ms");
            });
            Test("Pathological Markdown punctuation inputs parse in linear time without OOM", () =>
            {
                string[] inputs =
                {
                    new string('<', FilePreviewService.MaxTextPreviewBytes - 1) + ">",
                    string.Concat(Enumerable.Repeat(" * ", 32000)),
                    new string('[', 40000) + "](https://example.com)",
                    string.Concat(Enumerable.Repeat("**a ", 20000)) + string.Concat(Enumerable.Repeat("~~a ", 20000))
                };
                foreach (string input in inputs)
                {
                    var sw = Stopwatch.StartNew();
                    MarkdownParsedDocument parsed = FilePreviewService.ParseMarkdownStructure(input);
                    sw.Stop();
                    Check(parsed.Blocks.Length >= 1);
                    Check(sw.ElapsedMilliseconds < 1500, "Pathological Markdown took " + sw.ElapsedMilliseconds + " ms");
                }
                FlowDocument semantic = FilePreviewService.BuildMarkdownDocument("a * b * c and *em* and <https://example.com>");
                var para = semantic.Blocks.OfType<Paragraph>().Single();
                Check(para.Inlines.OfType<Hyperlink>().Count() == 1);
                Check(para.Inlines.OfType<Span>().Any(s => s.FontStyle == FontStyles.Italic));
            });
            Test("Rapid preview cancellation does not log ObjectDisposedException and click rejects stale data", () => RunWindowTask(async () =>
            {
                var window = new MainWindow(true, true);
                try
                {
                    string folder = MakeDirectory("preview-cts-guard");
                    string fileA = Path.Combine(folder, "a.txt");
                    string fileB = Path.Combine(folder, "b.txt");
                    File.WriteAllText(fileA, "alpha");
                    File.WriteAllText(fileB, "beta");

                    WindowField("_showPreviewPane").SetValue(window, true);
                    MethodInfo refresh = WindowMethod("RefreshPreviewPaneForSelection");
                    MethodInfo cancel = WindowMethod("CancelPendingPreviewLoad");
                    for (int i = 0; i < 16; i++)
                    {
                        string path = (i & 1) == 0 ? fileA : fileB;
                        refresh.Invoke(window, new object[] { new SelectionStatusInfo(2, 1, 5, true), (path, Path.GetFileName(path), false) });
                        cancel.Invoke(window, null);
                    }
                    await Task.Delay(120);

                    string logDir = Path.Combine(Root, "logs");
                    if (Directory.Exists(logDir))
                    {
                        foreach (string logFile in Directory.GetFiles(logDir))
                            Check(!File.ReadAllText(logFile).Contains("preview-load-failed"), "Unexpected preview-load-failed logged during cancellation.");
                    }

                    // 切换为空选择并校验 PreviewOpenDefaultButton_Click 在更新选区后拒绝执行陈旧动作
                    ExplorerTabItem tab = window.OpenNewThisPcTab();
                    WindowField("_selectedTab").SetValue(window, tab);
                    var stale = new FilePreviewData
                    {
                        Kind = FilePreviewKind.MetadataCard,
                        FilePath = Path.Combine(folder, "missing-never-launch.exe"),
                        CanOpenWithDefaultApp = true
                    };
                    WindowField("_lastPreviewSelectionKey").SetValue(window, "ONE|stale|stale|False");
                    WindowField("_currentPreviewData").SetValue(window, stale);
                    WindowMethod("PreviewOpenDefaultButton_Click").Invoke(window, new object?[] { window, new RoutedEventArgs() });
                    Check(WindowField("_currentPreviewData").GetValue(window) == null);
                }
                finally { window.PrepareForForceExit(); window.Close(); }
            }));
            Test("Breadcrumb lane stays responsive after canceled slow work, enforces cap, and handles concurrent close", () => WithReviewWindow(window => RunWindowTask(async () =>
            {
                string folder = MakeDirectory("breadcrumb-lane-cap");
                for (int i = 0; i < 6; i++) Directory.CreateDirectory(Path.Combine(folder, "dir-" + i));
                IntPtr pidl = PidlManager.TryParseLocalOrShellFast(folder);
                try
                {
                    using var slowCts = new CancellationTokenSource();
                    var slowSw = Stopwatch.StartNew();
                    Task slowTask = PidlManager.RunBreadcrumbFolderLaneAsync(_ =>
                    {
                        Thread.Sleep(2000);
                        return 1;
                    }, slowCts.Token, timeoutMs: 8000);
                    slowCts.Cancel();
                    bool canceled = false;
                    try { await slowTask; }
                    catch (OperationCanceledException) { canceled = true; }
                    slowSw.Stop();
                    Check(canceled && slowSw.ElapsedMilliseconds < 800, "Canceled breadcrumb lane took too long: " + slowSw.ElapsedMilliseconds + " ms");

                    var fastSw = Stopwatch.StartNew();
                    BreadcrumbChildrenResult truncated = await PidlManager.GetChildFoldersResultAsync(PidlManager.ToBytes(pidl), CancellationToken.None, maxCount: 5);
                    fastSw.Stop();
                    Check(truncated.IsTruncated && truncated.Children.Count == 5);
                    Check(fastSw.ElapsedMilliseconds < 1500, "Breadcrumb lane stayed blocked behind retired worker.");

                    var menu = new ContextMenu();
                    using var lifetime = new CancellationTokenSource();
                    CancellationToken lifetimeToken = lifetime.Token;
                    WindowMethod("ShowBreadcrumbFolderPagesCore").Invoke(window, new object?[] { menu, truncated.Children, lifetimeToken, true, null, null });
                    Check(menu.Items.Count == 6 && ((MenuItem)menu.Items[5]).Header.ToString()!.Contains("已达安全上限"));
                    await Task.Delay(50);
                    lifetime.Dispose();
                    // 即使外部 CTS 已被释放，ShowBreadcrumbFolderPagesCore 进入 CreateLinkedTokenSource 也必须安全吞掉 ObjectDisposedException
                    WindowMethod("ShowBreadcrumbFolderPagesCore").Invoke(window, new object?[] { menu, truncated.Children, lifetimeToken, true, null, null });
                }
                finally { PidlManager.Free(ref pidl); }
            })));
            Test("Changing the preview selection immediately revokes the previous file action", () => RunWindowTask(async () =>
            {
                var window = new MainWindow(true, true);
                try
                {
                    string folder = MakeDirectory("preview-action");
                    string next = Path.Combine(folder, "new.txt");
                    File.WriteAllText(next, "new content");
                    var previous = new FilePreviewData { Kind = FilePreviewKind.MetadataCard, FilePath = Path.Combine(folder, "old.pdf"), CanOpenWithDefaultApp = true };
                    WindowField("_showPreviewPane").SetValue(window, true);
                    FieldInfo current = WindowField("_currentPreviewData");
                    current.SetValue(window, previous);
                    WindowMethod("RenderLoadedPreviewData").Invoke(window, new object?[] { previous, null });
                    var action = (Button)window.FindName("PreviewOpenDefaultButton");
                    Check(action.Visibility == Visibility.Visible);
                    WindowMethod("RefreshPreviewPaneForSelection").Invoke(window, new object[]
                    {
                        new SelectionStatusInfo(2, 1, 0, false), (next, "new.txt", false)
                    });
                    Check(!ReferenceEquals(current.GetValue(window), previous) && action.Visibility == Visibility.Collapsed);
                    for (int i = 0; i < 200 && current.GetValue(window) == null; i++) await Task.Delay(10);
                    Check(current.GetValue(window) is FilePreviewData loaded && loaded.FilePath == next && loaded.TextContent == "new content");
                }
                finally { window.PrepareForForceExit(); window.Close(); }
            }));
            Test("Breadcrumb menus include every sorted child and load icons only for the requested page", () => WithReviewWindow(window =>
            {
                string folder = MakeDirectory("breadcrumb-pages");
                for (int i = 82; i >= 0; i--) Directory.CreateDirectory(Path.Combine(folder, "folder-" + i.ToString("D2")));
                File.WriteAllText(Path.Combine(folder, "not-a-folder.txt"), "");
                IntPtr pidl = PidlManager.TryParseLocalOrShellFast(folder);
                try
                {
                    var children = PidlManager.GetChildFoldersAsync(PidlManager.ToBytes(pidl), CancellationToken.None).GetAwaiter().GetResult();
                    Check(children.Count == 83 && children[0].Title == "folder-00" && children[82].Title == "folder-82");
                    Check(children.All(child => child.Icon == null));
                    var icons = PidlManager.GetChildFolderIconsAsync(children.GetRange(80, 3), CancellationToken.None).GetAwaiter().GetResult();
                    Check(icons.Length == 3 && icons.All(icon => icon?.IsFrozen == true));
                    using var lifetime = new CancellationTokenSource();
                    try
                    {
                        var menu = new ContextMenu();
                        WindowMethod("ShowBreadcrumbFolderPages").Invoke(window, new object[] { menu, children, lifetime.Token });
                        Check(menu.Items.Count == 81);
                        ((MenuItem)menu.Items[80]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                        Check(menu.Items.Count == 4 && ((MenuItem)menu.Items[3]).Header.Equals("folder-82"));
                        ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                        Check(menu.Items.Count == 81 && ((MenuItem)menu.Items[0]).Header.Equals("folder-00"));
                    }
                    finally { lifetime.Cancel(); }
                }
                finally { PidlManager.Free(ref pidl); }
            }));
            Test("Address completion and Shell parsing accept both forms of a fixture path", () =>
            {
                string parent = MakeDirectory("address path with spaces");
                string child = Directory.CreateDirectory(Path.Combine(parent, "Folder With Spaces")).FullName;
                foreach (string input in new[] { parent, TryGetShortTestPath(parent) ?? parent }.Distinct())
                {
                    var matches = QuickLaunchAndInstallService.BuildAddressSuggestions(Path.Combine(input, "Folder With"), false, Array.Empty<string>(), Array.Empty<string>(), 12);
                    Check(matches.Any(item => Path.GetFullPath(item.InsertText).TrimEnd('\\') == child));
                    IntPtr parsed = PidlManager.TryParseLocalOrShellFast(Path.Combine(input, "Folder With Spaces"));
                    IntPtr expected = PidlManager.TryParseLocalOrShellFast(child);
                    try { Check(parsed != IntPtr.Zero && PidlManager.AreEqual(expected, parsed)); }
                    finally { PidlManager.Free(ref parsed); PidlManager.Free(ref expected); }
                }
            });
            Test("Markdown code spans retain embedded backticks and unmatched delimiters", () =>
            {
                FlowDocument doc = FilePreviewService.BuildMarkdownDocument("``foo ` bar`` and ` x ` and ``unclosed");
                Check(new TextRange(doc.ContentStart, doc.ContentEnd).Text.TrimEnd() == "foo ` bar and x and ``unclosed");
                Check(doc.Blocks.OfType<Paragraph>().Single().Inlines.OfType<Span>().Count() == 2);
            });
            Test("Markdown links accept titles and angle destinations without opening title text", () =>
            {
                FlowDocument doc = FilePreviewService.BuildMarkdownDocument(
                    "[site](https://example.com \"title (extra)\") [two]( <https://example.com/a(b> 'caption') <https://example.com/three>");
                var links = doc.Blocks.OfType<Paragraph>().Single().Inlines.OfType<Hyperlink>().ToArray();
                Check(links.Length == 3);
                Check((string)links[0].ToolTip == "https://example.com/" &&
                    (string)links[1].ToolTip == "https://example.com/a(b" &&
                    (string)links[2].ToolTip == "https://example.com/three");
            });
            Test("Unsupported Markdown link destinations retain formatted labels as inert text", () =>
            {
                FlowDocument doc = FilePreviewService.BuildMarkdownDocument("[**bold** and `code`](#anchor)");
                var paragraph = doc.Blocks.OfType<Paragraph>().Single();
                Check(new TextRange(doc.ContentStart, doc.ContentEnd).Text.TrimEnd() == "bold and code");
                Check(!paragraph.Inlines.OfType<Hyperlink>().Any());
                Check(paragraph.Inlines.OfType<Span>().Any(s => s.FontWeight.ToOpenTypeWeight() > FontWeights.Normal.ToOpenTypeWeight()));
            });
            Test("Image preview applies all eight EXIF transforms without changing file pixels", () =>
            {
                string folder = MakeDirectory("exif-orientation");
                byte[] pixels = new byte[3 * 2 * 4];
                for (int i = 0; i < 6; i++) { pixels[i * 4] = (byte)(i + 1); pixels[i * 4 + 3] = 255; }
                var source = BitmapSource.Create(3, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 12);
                byte[][] expected = {
                    new byte[] { 1,2,3,4,5,6 }, new byte[] { 3,2,1,6,5,4 },
                    new byte[] { 6,5,4,3,2,1 }, new byte[] { 4,5,6,1,2,3 },
                    new byte[] { 1,4,2,5,3,6 }, new byte[] { 4,1,5,2,6,3 },
                    new byte[] { 6,3,5,2,4,1 }, new byte[] { 3,6,2,5,1,4 }
                };
                for (ushort orientation = 1; orientation <= 8; orientation++)
                {
                    var metadata = new BitmapMetadata("tiff");
                    metadata.SetQuery("/ifd/{ushort=274}", orientation);
                    var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.None };
                    encoder.Frames.Add(BitmapFrame.Create(source, null, metadata, null));
                    string path = Path.Combine(folder, orientation + ".tif");
                    using (var output = File.Create(path)) encoder.Save(output);
                    var data = FilePreviewService.LoadPreviewAsync(path, null, false).GetAwaiter().GetResult();
                    Check(data.Image != null, "TIFF decode failed for orientation " + orientation);
                    int width = orientation < 5 ? 3 : 2, height = orientation < 5 ? 2 : 3;
                    Check(data.ImagePixelWidth == width && data.ImagePixelHeight == height);
                    var converted = new FormatConvertedBitmap(data.Image!, PixelFormats.Bgra32, null, 0);
                    byte[] result = new byte[24];
                    converted.CopyPixels(result, width * 4, 0);
                    Check(Enumerable.Range(0, 6).Select(i => result[i * 4]).SequenceEqual(expected[orientation - 1]),
                        "Wrong pixel orientation " + orientation);
                    File.Delete(path); // The preview must not retain the source handle.
                }
            });
            Test("Closing address edit cancels navigation and hides its pending UI", () => WithReviewWindow(win =>
            {
                var cancellation = new CancellationTokenSource();
                WindowField("_addressParseCts").SetValue(win, cancellation);
                ((FrameworkElement)win.FindName("AddressBusyIndicator")).Visibility = Visibility.Visible;
                win.ExitAddressEditMode();
                Check(cancellation.IsCancellationRequested);
                Check(((FrameworkElement)win.FindName("AddressBusyIndicator")).Visibility == Visibility.Collapsed);
            }));
            Test("A late native interaction cannot close a newly opened address editor", () => WithReviewWindow(win =>
            {
                ExplorerTabItem tab = win.OpenNewThisPcTab();
                var edit = (FrameworkElement)win.FindName("AddressEditGrid");
                edit.Visibility = Visibility.Visible;
                var cancellation = new CancellationTokenSource();
                WindowField("_addressParseCts").SetValue(win, cancellation);
                var pendingInteraction = (EventHandler)typeof(LeafTab.Controls.ExplorerBrowserHost)
                    .GetField("ViewInteractionOccurred", WindowPrivate)!.GetValue(tab.BrowserHost)!;
                pendingInteraction(tab.BrowserHost, EventArgs.Empty);
                Check(edit.Visibility == Visibility.Visible && !cancellation.IsCancellationRequested);
            }));
            Test("Address completion awaits new suggestions and never applies a stale folder", () => RunWindowTask(async () =>
            {
                var win = new MainWindow(true, true);
                try
                {
                    string folder = MakeDirectory("address-completion-current");
                    string target = Path.Combine(folder, "new-target");
                    Directory.CreateDirectory(target);
                    ((FrameworkElement)win.FindName("AddressEditGrid")).Visibility = Visibility.Visible;
                    var text = (TextBox)win.FindName("AddressTextBox");
                    text.Text = Path.Combine(folder, "new-");
                    ((ListBox)win.FindName("AddressSuggestListBox")).ItemsSource = new[] {
                        new AddressSuggestionItem { InsertText = Path.Combine(folder, "old-wrong-folder") }
                    };
                    await (Task)WindowMethod("ApplyAddressSuggestionAsync").Invoke(win, new object[] { Key.Tab, false })!;
                    Check(text.Text == target + "\\", "Applied stale suggestion: " + text.Text);
                    text.Text = Path.Combine(folder, "missing-");
                    WindowMethod("RefreshAddressSuggestions").Invoke(win, new object[] { false });
                    Task pending = (Task)WindowMethod("ApplyAddressSuggestionAsync").Invoke(win, new object[] { Key.Tab, false })!;
                    win.ExitAddressEditMode();
                    await pending;
                    Check(text.Text.EndsWith("missing-"), "Completion changed text after editing was cancelled.");
                    ((FrameworkElement)win.FindName("AddressEditGrid")).Visibility = Visibility.Visible;
                    var bookmarks = (System.Collections.ObjectModel.ObservableCollection<BookmarkItemViewModel>)WindowField("_bookmarks").GetValue(win)!;
                    bookmarks.Add(new BookmarkItemViewModel { Title = "此电脑", TargetPath = "此电脑" });
                    text.Text = string.Empty;
                    WindowMethod("RefreshAddressSuggestions").Invoke(win, new object[] { true });
                    await (Task)WindowMethod("ApplyAddressSuggestionAsync").Invoke(win, new object[] { Key.Tab, false })!;
                    Check(text.Text == "此电脑", "A virtual folder alias acquired a filesystem separator.");
                }
                finally { win.PrepareForForceExit(); win.Close(); }
            }));
            Test("Folder navigation cancels queued search and clears its text", () => WithReviewWindow(win =>
            {
                IntPtr pidl = PidlManager.CreateThisPcPidl();
                using var tab = new ExplorerTabItem(pidl);
                PidlManager.Free(ref pidl);
                WindowField("_selectedTab").SetValue(win, tab);
                WindowField("_pendingSearchTab").SetValue(win, tab);
                tab.SearchQuery = "old-query";
                WindowMethod("SetSearchBoxTextSilently").Invoke(win, new object[] { "old-query" });
                var timer = (DispatcherTimer)WindowField("_searchDebounceTimer").GetValue(win)!;
                timer.Start();
                WindowMethod("CancelSearchForNavigation").Invoke(win, new object[] { tab });
                Check(!timer.IsEnabled && WindowField("_pendingSearchTab").GetValue(win) == null);
                Check(tab.SearchQuery == "" && ((TextBox)win.FindName("SearchTextBox")).Text == "");
                WindowField("_selectedTab").SetValue(win, null);
            }));
            Test("Preview pane recovers its preferred width after narrowing the window", () => WithReviewWindow(win =>
            {
                WindowField("_showPreviewPane").SetValue(win, true);
                WindowField("_previewPaneWidthDip").SetValue(win, 600.0);
                WindowMethod("ApplyPreviewPaneLayout").Invoke(win, null);
                var column = (ColumnDefinition)win.FindName("PreviewPaneColumn");
                foreach (double width in new[] { 720.0, 1160.0 })
                {
                    win.Width = width;
                    Type infoType = typeof(UIElement).Assembly.GetType("System.Windows.SizeChangedInfo")!;
                    object info = infoType.GetConstructors(WindowPrivate | BindingFlags.Public).Single()
                        .Invoke(new object[] { win, new Size(1160, 740), true, false });
                    var args = (SizeChangedEventArgs)typeof(SizeChangedEventArgs).GetConstructors(WindowPrivate).Single()
                        .Invoke(new[] { (object)win, info });
                    args.RoutedEvent = FrameworkElement.SizeChangedEvent;
                    win.RaiseEvent(args);
                    Check(column.Width.Value == (width == 720 ? 390 : 600));
                }
                Check(win.PreviewPaneWidthDip == 600);
            }));
        }

        private sealed class FolderEnumeratorStub : IEnumIDList
        {
            private readonly int _lastResult;
            private bool _first = true;

            public FolderEnumeratorStub(int lastResult) => _lastResult = lastResult;
            public int Next(uint count, out IntPtr item, out uint fetched)
            {
                item = _first ? new IntPtr(1) : IntPtr.Zero;
                fetched = _first ? 1u : 0u;
                _first = false;
                return fetched == 1 ? ShellConstants.S_OK : _lastResult;
            }
            public int Skip(uint count) => throw new NotSupportedException();
            public int Reset() => throw new NotSupportedException();
            public int Clone(out IEnumIDList? copy) => throw new NotSupportedException();
        }

        private static FieldInfo WindowField(string name) => typeof(MainWindow).GetField(name, WindowPrivate)!;
        private static MethodInfo WindowMethod(string name) => typeof(MainWindow).GetMethod(name, WindowPrivate)!;
        private static void WithReviewWindow(Action<MainWindow> action)
        {
            EnsureFocusTestApplication();
            var win = new MainWindow(true, true);
            try { action(win); }
            finally { win.PrepareForForceExit(); win.Close(); }
        }
        private static void RunWindowTask(Func<Task> action)
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var frame = new DispatcherFrame();
            Exception? error = null;
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            timeout.Tick += (_, _) => { error = new TimeoutException("Window regression timed out."); frame.Continue = false; };
            timeout.Start();
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); }
                catch (Exception ex) { error = ex; }
                finally { frame.Continue = false; }
            }));
            try { Dispatcher.PushFrame(frame); }
            finally { timeout.Stop(); SynchronizationContext.SetSynchronizationContext(previous); }
            if (error != null) throw error;
        }
    }
}
