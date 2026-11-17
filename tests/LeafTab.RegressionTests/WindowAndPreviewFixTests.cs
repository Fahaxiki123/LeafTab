using System;
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

        private static FieldInfo WindowField(string name) => typeof(MainWindow).GetField(name, WindowPrivate)!;
        private static MethodInfo WindowMethod(string name) => typeof(MainWindow).GetMethod(name, WindowPrivate)!;
        private static void WithReviewWindow(Action<MainWindow> action)
        {
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
