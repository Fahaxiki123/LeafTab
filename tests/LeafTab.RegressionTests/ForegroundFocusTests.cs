using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls;
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
        private sealed class FocusFixtureApplication : LeafTab.App
        {
            protected override void OnStartup(StartupEventArgs e) { }
        }

        private static void EnsureFocusTestApplication()
        {
            if (Application.Current != null) return;
            new FocusFixtureApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Application.Current!.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("/LeafTab;component/Views/Icons.xaml", UriKind.Relative) });
        }

        // Explicit opt-in: this test briefly displays its own fixture window.
        private static int RunStandbyRestoreNativeTests()
        {
            EnsureFocusTestApplication();
            return RunFocusDispatcherTest(async () =>
            {
                string a = Path.Combine(Root, "standby-A"), b = Path.Combine(Root, "standby-B");
                Directory.CreateDirectory(a); Directory.CreateDirectory(b);
                var config = new AppConfig { RestorePreviousTabs = true, SelectedTabIndex = 1 };
                config.OpenTabs.Add(new SavedTabConfigItem { CurrentPath = a });
                config.OpenTabs.Add(new SavedTabConfigItem { CurrentPath = b });
                int savedCount = 0;
                var store = new AppConfigStore(config, current => { savedCount = current.OpenTabs.Count; return true; });
                var window = new MainWindow(store);
                var application = (LeafTab.App)Application.Current;
                application.RegisterWindow(window);
                MainWindow? secondary = null;
                try
                {
                    new WindowInteropHelper(window).EnsureHandle();
                    Check(!window.IsVisible && window.Tabs.Count == 0);
                    await window.HandleExternalLaunchRequestAsync(new LaunchActionRequest(), isInitialLaunch: false);
                    Test("First targetless wake of an unloaded standby window restores every saved tab", () =>
                        Check(window.IsVisible && window.Tabs.Count == 2 &&
                            window.Tabs.Select(t => t.FileSystemPath).SequenceEqual(new[] { a, b }) &&
                            window.SelectedTab?.FileSystemPath == b));
                    store.Flush();
                    Test("First standby wake does not replace the saved session with This PC", () => Check(savedCount == 2));

                    secondary = new MainWindow(store);
                    application.RegisterWindow(secondary);
                    secondary.Show();
                    IntPtr otherPidl = PidlManager.TryParseLocalOrShellFast(Root);
                    try { secondary.AddNewTab(otherPidl, activate: true, animateOpen: false); }
                    finally { PidlManager.Free(ref otherPidl); }
                    secondary.Activate();
                    await Task.Delay(100);
                    Check(ReferenceEquals(application.MainWindow, secondary));
                    secondary.Close();
                    store.Flush();
                    Test("Closing the active secondary window preserves the surviving window's nonzero selected tab", () =>
                        Check(config.OpenTabs.Count == 2 && config.SelectedTabIndex == 1 && config.OpenTabs[1].CurrentPath == b));
                }
                finally
                {
                    typeof(LeafTab.App).GetField("_isShuttingDownAll", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(application, true);
                    if (secondary != null && !secondary.IsClosingOrClosed) { secondary.PrepareForForceExit(true); secondary.Close(); }
                    window.PrepareForForceExit(skipPerWindowSave: true); window.Close();
                }
            });
        }

        // Real keyboard input must be supplied by the user or the approved CUA
        // channel. This fixture does not inject keys, access installed config,
        // or manipulate any file outside the generated test directory.
        private static int RunForegroundFocusInputTests()
        {
            EnsureFocusTestApplication();
            var window = new MainWindow(skipSessionPersistence: true, deferInitialFallbackTab: true);
            var frame = new DispatcherFrame();
            int tabChecks = 0, renameTabChecks = 0, editChecks = 0, imageChecks = 0;
            ThreadMessageEventHandler observe = (ref MSG msg, ref bool handled) =>
            {
                if (msg.message != NativeMethods.WM_KEYDOWN || !window.IsActive) return;
                int key = msg.wParam.ToInt32();
                ModifierKeys modifiers = Keyboard.Modifiers;
                bool edit = window.SelectedTab?.BrowserHost.IsNativeEditControlFocused() == true;
                if (key == 9 && (modifiers == ModifierKeys.Control || modifiers == (ModifierKeys.Control | ModifierKeys.Shift)))
                {
                    int before = window.Tabs.IndexOf(window.SelectedTab!);
                    int delta = modifiers == ModifierKeys.Control ? 1 : -1;
                    var expected = window.Tabs[(before + delta + window.Tabs.Count) % window.Tabs.Count];
                    string focus = Keyboard.FocusedElement?.GetType().Name ?? "native";
                    window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        Test("Real Ctrl+Tab switches tabs from " + focus, () => Check(ReferenceEquals(window.SelectedTab, expected)));
                        tabChecks++;
                    }));
                }
                else if (edit && key == 9 && (modifiers == ModifierKeys.None || modifiers == ModifierKeys.Shift))
                {
                    var host = window.SelectedTab!.BrowserHost;
                    window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        Test("Real Tab retains a native inline rename editor", () => Check(host.IsNativeEditControlFocused()));
                        renameTabChecks++;
                    }));
                }
                else if (edit && key == 0x74)
                {
                    var host = window.SelectedTab!.BrowserHost;
                    window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        Test("Real F5 cannot interrupt inline rename", () => Check(host.IsNativeEditControlFocused()));
                        editChecks++;
                    }));
                }
                else if (edit && key == 'L' && modifiers == ModifierKeys.Control)
                {
                    window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
                    {
                        await Task.Delay(150); // Include Shell's posted rename-completion focus restoration.
                        Test("Real Ctrl+L leaves inline rename and keeps the address editor focused", () =>
                            Check(((TextBox)window.FindName("AddressTextBox")).IsKeyboardFocusWithin &&
                                window.SelectedTab?.BrowserHost.IsNativeFocusInside() == false));
                    }));
                }
                else if (!edit && (key == 0x25 || key == 0x27) && modifiers == ModifierKeys.None &&
                    window.CurrentPreviewKindName == "Image" && window.SelectedTab?.BrowserHost.IsNativeFocusInside() == true)
                {
                    string previousImage = window.CurrentPreviewTitle;
                    double previousWidth = ((ColumnDefinition)window.FindName("PreviewPaneColumn")).ActualWidth;
                    window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
                    {
                        await Task.Delay(500);
                        Test("Real image arrow changes the selected image without resizing its preview", () =>
                            Check(window.CurrentPreviewKindName == "Image" && window.CurrentPreviewTitle != previousImage &&
                                Math.Abs(((ColumnDefinition)window.FindName("PreviewPaneColumn")).ActualWidth - previousWidth) < 0.1));
                        imageChecks++;
                    }));
                }
                else if (key == 'D' && modifiers == ModifierKeys.Control && Keyboard.FocusedElement is TextBox)
                {
                    var bookmarks = (ItemsControl)window.FindName("BookmarksItemsControl");
                    int before = bookmarks.Items.Count;
                    window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                        Test("Real Ctrl+D in a text editor preserves bookmarks", () => Check(bookmarks.Items.Count == before))));
                }
            };
            ComponentDispatcher.ThreadFilterMessage += observe;
            window.Loaded += (_, _) =>
            {
                foreach (string name in new[] { "focus-A", "focus-B", "focus-C" })
                {
                    string folder = Path.Combine(Root, name); Directory.CreateDirectory(folder);
                    File.WriteAllText(Path.Combine(folder, "first.txt"), "Focus regression fixture.");
                    File.WriteAllText(Path.Combine(folder, "second.txt"), "Second fixture.");
                    foreach (byte shade in new byte[] { 40, 180 })
                    {
                        byte[] pixels = Enumerable.Repeat(shade, 32 * 32 * 3).ToArray();
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(32, 32, 96, 96, PixelFormats.Rgb24, null, pixels, 96)));
                        using var stream = File.Create(Path.Combine(folder, "image-" + shade + ".png"));
                        encoder.Save(stream);
                    }
                    IntPtr pidl = PidlManager.TryParseLocalOrShellFast(folder);
                    try { window.AddNewTab(pidl, activate: false, animateOpen: false); }
                    finally { PidlManager.Free(ref pidl); }
                }
                window.ActivateTab(window.Tabs[0]);
                if (!window.IsPreviewPaneOpen) window.TogglePreviewPane();
            };
            window.Closed += (_, _) => frame.Continue = false;
            Console.WriteLine("Use real Ctrl+Tab from text/native areas; F2 then Tab and F5; select an image and press an arrow. Close the fixture window to finish.");
            try { window.Show(); Dispatcher.PushFrame(frame); }
            finally { ComponentDispatcher.ThreadFilterMessage -= observe; }
            Test("Foreground input suite received every required kind of real input", () =>
                Check(tabChecks >= 2 && renameTabChecks >= 1 && editChecks >= 1 && imageChecks >= 1,
                    $"Incomplete: tabs={tabChecks}, rename-Tab={renameTabChecks}, edit-F5={editChecks}, image-arrows={imageChecks}."));
            Console.WriteLine($"RESULT: {_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }
    }
}
