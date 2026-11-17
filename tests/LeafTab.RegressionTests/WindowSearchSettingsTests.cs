using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LeafTab.Controls;
using LeafTab.Interop;
using LeafTab.Models;
using LeafTab.Views;

namespace LeafTab.RegressionTests
{
    internal static partial class Program
    {
        private sealed class WindowSearchTestApplication : LeafTab.App
        {
            // Creating an Application schedules Startup on the dispatcher. The
            // fixture must not run the real startup/COM/installed association path.
            protected override void OnStartup(StartupEventArgs e) { }
        }

        private static int RunWindowSearchSettingsNativeTests()
        {
            int oleHr = SearchNavNative.OleInitialize(IntPtr.Zero);
            if (oleHr < 0) throw new COMException("Cannot initialize Shell test apartment.", oleHr);
            var application = new WindowSearchTestApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("pack://application:,,,/LeafTab;component/Views/Icons.xaml", UriKind.Absolute) });
            SynchronizationContext? previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var frame = new DispatcherFrame();
            using var deadline = new Timer(_ => Environment.Exit(1), null, 60000, Timeout.Infinite);
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
            {
                try { await VerifyWindowSearchSettingsAsync(application); }
                catch (Exception ex) { _failed++; Console.WriteLine("FAIL: Window search settings — " + ex); }
                finally { frame.Continue = false; }
            }));
            try { Dispatcher.PushFrame(frame); }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
                SearchNavNative.OleUninitialize();
            }
            Console.WriteLine($"RESULT: {_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        private static async Task VerifyWindowSearchSettingsAsync(LeafTab.App application)
        {
            var initialShellFlags = PidlManager.GetShellVisibilityFlags();
            var windows = new List<MainWindow>();
            var hosts = new List<(ExplorerBrowserHost Host, IntPtr Built, IntPtr Parent)>();
            var tabs = new List<ExplorerTabItem>();
            bool includeHidden = false;
            try
            {
                var first = new MainWindow(skipSessionPersistence: true, deferInitialFallbackTab: true);
                var second = new MainWindow(skipSessionPersistence: true, deferInitialFallbackTab: true);
                windows.Add(first);
                windows.Add(second);
                application.RegisterWindow(first);
                application.RegisterWindow(second);

                async Task<ExplorerTabItem> AddSearchTab(MainWindow window, string key, bool activate)
                {
                    string root = MakeDirectory("window-search-" + key);
                    File.WriteAllText(Path.Combine(root, key + "_visible.txt"), "visible");
                    string hiddenFile = Path.Combine(root, key + "_hidden.txt");
                    File.WriteAllText(hiddenFile, "hidden");
                    File.SetAttributes(hiddenFile, FileAttributes.Hidden);
                    IntPtr pidl = PidlManager.TryParseLocalOrShellFast(root);
                    try
                    {
                        ExplorerTabItem tab = window.AddNewTab(pidl, activate, animateOpen: false);
                        tabs.Add(tab);
                        ExplorerBrowserHost host = tab.BrowserHost;
                        host.SearchHiddenItemsProvider = () => includeHidden;
                        int completed = 0;
                        host.NavigationCompleted += (_, _) => completed++;
                        IntPtr parent = NativeMethods.CreateWindowExW(0x08000080, "STATIC", "LeafTab hidden settings regression",
                            unchecked((int)0x82000000), -20000, -20000, 960, 640,
                            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                        Check(parent != IntPtr.Zero);
                        host.Measure(new Size(960, 640));
                        host.Arrange(new Rect(0, 0, 960, 640));
                        IntPtr built = ((HandleRef)typeof(ExplorerBrowserHost).GetMethod("BuildWindowCore", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(host, new object[] { new HandleRef(null, parent) })!).Handle;
                        hosts.Add((host, built, parent));
                        Check(await WaitNativeConditionAsync(() => completed > 0 && host.DirectUiHwnd != IntPtr.Zero));
                        // A native metadata query avoids dependence on indexing latency.
                        string query = "size:>=0 " + key + "_";
                        await (Task<int>)typeof(MainWindow).GetMethod("ExecuteSearchOnTabAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(window, new object[] { tab, query })!;
                        if (activate)
                            typeof(MainWindow).GetMethod("SetSearchBoxTextSilently", BindingFlags.Instance | BindingFlags.NonPublic)!
                                .Invoke(window, new object[] { query });
                        Check(host.IsShowingSearchResults && host.SearchMatchCount == 1,
                            "Initial hidden-filtered search must contain one visible fixture: " + key);
                        return tab;
                    }
                    finally { PidlManager.Free(ref pidl); }
                }

                ExplorerTabItem selectedFirst = await AddSearchTab(first, "first", true);
                ExplorerTabItem backgroundFirst = await AddSearchTab(first, "background", false);
                ExplorerTabItem selectedSecond = await AddSearchTab(second, "second", true);
                string firstQuery = selectedFirst.SearchQuery;
                string backgroundQuery = backgroundFirst.SearchQuery;
                string secondQuery = selectedSecond.SearchQuery;

                async Task RefreshFromFirstWindow()
                {
                    typeof(MainWindow).GetMethod("RefreshAllTabs", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(first, null);
                    Check(await WaitNativeConditionAsync(() => tabs.TrueForAll(t => !t.BrowserHost.IsSearchRunning)));
                    // Catch delayed browse refresh/navigation callbacks that could
                    // otherwise clear the just-restarted search after it finishes.
                    await Task.Delay(600);
                }

                includeHidden = true;
                await RefreshFromFirstWindow();
                Test("Shell setting refresh reruns selected and background searches in every managed window", () =>
                {
                    foreach (ExplorerTabItem tab in tabs)
                        Check(tab.BrowserHost.IsShowingSearchResults && tab.BrowserHost.SearchMatchCount == 2,
                            "The restarted search lost state or did not add the hidden fixture: " + tab.SearchQuery);
                    Check(selectedFirst.SearchQuery == firstQuery && backgroundFirst.SearchQuery == backgroundQuery && selectedSecond.SearchQuery == secondQuery);
                    Check(((TextBox)first.FindName("SearchTextBox")).Text == firstQuery);
                    Check(((TextBox)second.FindName("SearchTextBox")).Text == secondQuery);
                });

                includeHidden = false;
                await RefreshFromFirstWindow();
                Test("Turning hidden results off again preserves each window's own query", () =>
                {
                    foreach (ExplorerTabItem tab in tabs)
                        Check(tab.BrowserHost.IsShowingSearchResults && tab.BrowserHost.SearchMatchCount == 1);
                    Check(selectedFirst.SearchQuery == firstQuery && backgroundFirst.SearchQuery == backgroundQuery && selectedSecond.SearchQuery == secondQuery);
                    Check(((TextBox)first.FindName("SearchTextBox")).Text == firstQuery);
                    Check(((TextBox)second.FindName("SearchTextBox")).Text == secondQuery);
                });

                Test("Search back clears the selected window textbox without changing another window", () =>
                {
                    Check(selectedFirst.NavigateHistoryStep(-1));
                    Check(selectedFirst.SearchQuery == string.Empty && ((TextBox)first.FindName("SearchTextBox")).Text == string.Empty);
                    Check(selectedSecond.SearchQuery == secondQuery && ((TextBox)second.FindName("SearchTextBox")).Text == secondQuery);
                });

                string destination = MakeDirectory("window-search-navigation-target");
                IntPtr destinationPidl = PidlManager.TryParseLocalOrShellFast(destination);
                try
                {
                    ((TextBox)first.FindName("SearchTextBox")).Text = "abandoned-search";
                    Check(selectedFirst.BrowserHost.NavigateToPidl(destinationPidl));
                    Check(await WaitNativeConditionAsync(() => PidlManager.AreEqual(selectedFirst.CurrentPidl, destinationPidl)));
                    await Task.Delay(250);
                    Test("Folder navigation cancels an outstanding window search debounce", () =>
                    {
                        Check(!selectedFirst.BrowserHost.IsShowingSearchResults && selectedFirst.SearchQuery == string.Empty);
                        Check(((TextBox)first.FindName("SearchTextBox")).Text == string.Empty);
                        Check(selectedSecond.SearchQuery == secondQuery && backgroundFirst.SearchQuery == backgroundQuery);
                    });
                }
                finally { PidlManager.Free(ref destinationPidl); }

                Test("Window search regression changes no real Explorer visibility settings and shows no windows", () =>
                {
                    Check(PidlManager.GetShellVisibilityFlags() == initialShellFlags);
                    foreach (MainWindow window in windows) Check(!window.IsVisible);
                    foreach (var host in hosts) Check(!NativeMethods.IsWindowVisible(host.Parent));
                });
            }
            finally
            {
                typeof(LeafTab.App).GetField("_isShuttingDownAll", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(application, true);
                foreach (var host in hosts)
                {
                    try
                    {
                        typeof(ExplorerBrowserHost).GetMethod("DestroyWindowCore", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(host.Host, new object[] { new HandleRef(host.Host, host.Built) });
                    }
                    finally { NativeMethods.DestroyWindow(host.Parent); }
                }
                foreach (MainWindow window in windows)
                {
                    window.PrepareForForceExit(skipPerWindowSave: true);
                    window.Close();
                }
            }
        }
    }
}
