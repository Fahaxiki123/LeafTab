using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LeafTab.Controls;
using LeafTab.Interop;
using LeafTab.Models;
using LeafTab.Services;
using LeafTab.Views;

namespace LeafTab.RegressionTests
{
    internal static partial class Program
    {
        private static void RunStartupAndTabMoveTests()
        {
            Test("Closing a tab before the first layout releases its cloned folder identifier", () =>
            {
                IntPtr folder = PidlManager.TryParseLocalOrShellFast(MakeDirectory("unmounted-tab"));
                try
                {
                    var pending = typeof(ExplorerBrowserHost).GetField("_pendingInitialPidl", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var destroyed = typeof(ExplorerBrowserHost).GetField("_isDestroyed", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var host = new ExplorerBrowserHost(folder);
                    Check((IntPtr)pending.GetValue(host)! != IntPtr.Zero);
                    host.Dispose();
                    Check((IntPtr)pending.GetValue(host)! == IntPtr.Zero && (bool)destroyed.GetValue(host)!);
                }
                finally { PidlManager.Free(ref folder); }
            });

            Test("Closing a window before Loaded resolves WaitUntilLoadedAsync instead of hanging", () => RunWindowTask(async () =>
            {
                var win = new MainWindow(true, true);
                win.PrepareForForceExit(skipPerWindowSave: true);
                win.Close();
                Task waitTask = win.WaitUntilLoadedAsync();
                Task completed = await Task.WhenAny(waitTask, Task.Delay(2000));
                Check(ReferenceEquals(completed, waitTask));
                await waitTask;
            }));

            Test("Startup routes every open received before, during and after session restoration", () => RunWindowTask(async () =>
            {
                var requests = Enumerable.Range(0, 5).Select(_ => new LaunchActionRequest()).ToArray();
                var delivered = new List<LaunchActionRequest>();
                var restoring = new TaskCompletionSource<bool>();
                var routing = new TaskCompletionSource<bool>();
                var queue = new StartupLaunchQueue(request =>
                {
                    delivered.Add(request);
                    return request == requests[1] ? routing.Task : Task.FromResult(true);
                });
                queue.Accept(requests[0]);
                Task startup = queue.CompleteAsync(() => restoring.Task);
                queue.Accept(requests[1]);
                queue.Accept(requests[2]);
                Check(delivered.SequenceEqual(requests.Take(1)));
                restoring.SetResult(true);
                Check(await WaitNativeConditionAsync(() => delivered.Count == 2));
                queue.Accept(requests[3]);
                routing.SetResult(true);
                await startup;
                queue.Accept(requests[4]);
                Check(delivered.SequenceEqual(requests) && !queue.HasPending);

                var faultedDelivered = new List<LaunchActionRequest>();
                var faultedQueue = new StartupLaunchQueue(request =>
                {
                    faultedDelivered.Add(request);
                    if (ReferenceEquals(request, requests[0]))
                        throw new InvalidOperationException("Simulated route fault.");
                    return Task.FromResult(true);
                });
                faultedQueue.Accept(requests[0]);
                faultedQueue.Accept(requests[1]);
                await faultedQueue.CompleteAsync(() => throw new InvalidOperationException("Simulated finish fault."));
                faultedQueue.Accept(requests[2]);
                await Task.Yield();
                Check(faultedDelivered.SequenceEqual(requests.Take(3)) && !faultedQueue.HasPending);
            }));

            Test("A failed or repeated move leaves the source tab available for retry", () => WithReviewWindow(source => RunWindowTask(async () =>
            {
                IntPtr pidl = PidlManager.TryParseLocalOrShellFast(MakeDirectory("retained-source"));
                try
                {
                    source.AddNewTab(pidl, true, false);
                    var tab = source.AddNewTab(pidl, true, false);
                    tab.SearchQuery = "pending search";
                    var opening = new TaskCompletionSource<(MainWindow Window, ExplorerTabItem Tab)>();
                    Task<MainWindow?> moving = source.TransferTabAsync(tab, (_, _) => opening.Task);
                    Check(!moving.IsCompleted && source.Tabs.Contains(tab));
                    bool duplicateOpened = false;
                    Check(await source.TransferTabAsync(tab, (_, _) => { duplicateOpened = true; return opening.Task; }) == null && !duplicateOpened);
                    opening.SetException(new InvalidOperationException("Simulated window creation failure."));
                    Check(await moving == null && source.Tabs.Contains(tab) && tab.SearchQuery == "pending search");
                    bool retried = false;
                    await source.TransferTabAsync(tab, (_, _) => { retried = true; return opening.Task; });
                    Check(retried && source.Tabs.Contains(tab));
                }
                finally { PidlManager.Free(ref pidl); }
            })));

            Test("Rolling back a move after the user replaces its target tab preserves their new tab", () =>
                WithReviewWindow(source => RunWindowTask(async () =>
                {
                    var destination = new MainWindow(true, true);
                    IntPtr folder = PidlManager.TryParseLocalOrShellFast(MakeDirectory("pending-transfer"));
                    IntPtr other = PidlManager.TryParseLocalOrShellFast(MakeDirectory("user-tab"));
                    try
                    {
                        source.AddNewTab(folder, false, false);
                        var original = source.AddNewTab(folder, true, false);
                        // An unmounted target keeps the initial navigation pending without a network dependency.
                        var movingTab = destination.AddNewTab(folder, false, false, deferHostMount: true);
                        var transfer = source.TransferTabAsync(original, (_, _) => Task.FromResult((destination, movingTab)),
                            closeNewWindowOnFailure: true);
                        Check(!transfer.IsCompleted);
                        var userTab = destination.AddNewTab(other, true, false);
                        destination.CloseTab(movingTab);
                        Check(await transfer == null && source.Tabs.Contains(original));
                        Check(!destination.IsClosingOrClosed && destination.Tabs.SequenceEqual(new[] { userTab }));
                    }
                    finally
                    {
                        PidlManager.Free(ref folder);
                        PidlManager.Free(ref other);
                        if (!destination.IsClosingOrClosed) { destination.PrepareForForceExit(true); destination.Close(); }
                    }
                })));

            Test("A failed move resumes the pending search without replacing a newer source edit", () =>
                WithReviewWindow(source => RunWindowTask(async () =>
                {
                    IntPtr folder = PidlManager.TryParseLocalOrShellFast(MakeDirectory("suspended-search"));
                    try
                    {
                        source.AddNewTab(folder, false, false);
                        var original = source.AddNewTab(folder, true, false);
                        var box = (TextBox)source.FindName("SearchTextBox");
                        var timer = (System.Windows.Threading.DispatcherTimer)WindowField("_searchDebounceTimer").GetValue(source)!;
                        int version = original.BrowserHost.SearchRequestVersion;
                        box.Text = "first ";
                        Check(timer.IsEnabled);
                        await source.TransferTabAsync(original, (_, _) => throw new InvalidOperationException("Simulated move failure."));
                        Check(timer.IsEnabled && original.SearchQuery == "first ");
                        Check(await WaitNativeConditionAsync(() => original.BrowserHost.SearchRequestVersion > version));
                        Check(original.SearchQuery == "first");

                        box.Text = "older";
                        var opening = new TaskCompletionSource<(MainWindow Window, ExplorerTabItem Tab)>();
                        Task<MainWindow?> moving = source.TransferTabAsync(original, (_, _) => opening.Task);
                        Check(!timer.IsEnabled);
                        box.Text = "newer";
                        version = original.BrowserHost.SearchRequestVersion;
                        opening.SetException(new InvalidOperationException("Simulated delayed move failure."));
                        Check(await moving == null && timer.IsEnabled);
                        Check(await WaitNativeConditionAsync(() => original.BrowserHost.SearchRequestVersion > version));
                        Check(original.SearchQuery == "newer" && !timer.IsEnabled);
                    }
                    finally { PidlManager.Free(ref folder); }
                })));

            Test("Moved view restores its query, matching results and expanded result limit", () => WithReviewWindow(window => RunWindowTask(async () =>
            {
                string folder = MakeDirectory("moved-search");
                File.WriteAllText(Path.Combine(folder, "match.txt"), "match");
                File.WriteAllText(Path.Combine(folder, "other.txt"), "other");
                IntPtr pidl = PidlManager.TryParseLocalOrShellFast(folder);
                IntPtr parent = IntPtr.Zero, built = IntPtr.Zero;
                var tab = window.AddNewTab(pidl, true, false);
                var host = tab.BrowserHost;
                try
                {
                    // Mount only the explorer control in an off-screen, non-activating test window.
                    parent = NativeMethods.CreateWindowExW(0x08000080, "STATIC", "LeafTab moved search regression",
                        unchecked((int)0x82000000), -20000, -20000, 960, 640, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    Check(parent != IntPtr.Zero);
                    host.Measure(new Size(960, 640));
                    host.Arrange(new Rect(0, 0, 960, 640));
                    built = ((HandleRef)typeof(ExplorerBrowserHost).GetMethod("BuildWindowCore", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(host, new object[] { new HandleRef(null, parent) })!).Handle;
                    const string query = "size:>=0 match";
                    Check(await window.RestoreMovedTabViewAsync(tab, pidl, query, 800));
                    Check(host.IsShowingSearchResults && host.SearchMatchCount == 1 && host.CurrentSearchMaxResults == 800);
                    Check(tab.SearchQuery == query && ((TextBox)window.FindName("SearchTextBox")).Text == query);

                    host.ExitSearchMode();
                    var source = new MainWindow(true, true);
                    var searchBrowser = typeof(ExplorerBrowserHost).GetField("_searchBrowser", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    object? withheld = null;
                    void DelayResultsView(object? sender, EventArgs e)
                    {
                        if (host.IsSearchRunning && withheld == null)
                        {
                            withheld = searchBrowser.GetValue(host);
                            searchBrowser.SetValue(host, null);
                        }
                    }
                    try
                    {
                        source.AddNewTab(pidl, false, false);
                        var original = source.AddNewTab(pidl, true, false);
                        original.SearchQuery = query;
                        host.SearchStateChanged += DelayResultsView;
                        var transfer = await source.TransferTabAsync(original, (_, _) => Task.FromResult((window, tab)));
                        Check(withheld != null && transfer == null && source.Tabs.Contains(original),
                            "A timed-out destination search must retain the source tab.");

                        host.SearchStateChanged -= DelayResultsView;
                        searchBrowser.SetValue(host, withheld);
                        withheld = null;
                        host.ExitSearchMode();

                        // 模拟用户在 110ms 防抖期含尾随空格拖出标签，确认防抖被取消、规范化并完成源标签移除
                        original.SearchQuery = "match ";
                        var pendingField = typeof(MainWindow).GetField("_pendingSearchTab", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        var timerField = typeof(MainWindow).GetField("_searchDebounceTimer", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        pendingField.SetValue(source, original);
                        var timer = (System.Windows.Threading.DispatcherTimer)timerField.GetValue(source)!;
                        timer.Start();
                        var trimmedMove = await source.TransferTabAsync(original, (_, _) => Task.FromResult((window, tab)));
                        Check(trimmedMove == window && !timer.IsEnabled && !source.Tabs.Contains(original) &&
                              tab.SearchQuery == "match" && host.SearchMatchCount == 1);

                        host.ExitSearchMode();
                        var failedSource = source.AddNewTab(pidl, true, false);
                        failedSource.SearchQuery = "keep my results";
                        var hwndField = typeof(ExplorerBrowserHost).GetField("_hostHwnd", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        IntPtr hwnd = (IntPtr)hwndField.GetValue(host)!;
                        Check(hwnd != IntPtr.Zero && host.IsFolderNavigationComplete(pidl));
                        try
                        {
                            // Only the search view fails to initialize. No user input changes the target query.
                            hwndField.SetValue(host, IntPtr.Zero);
                            var failedMove = await source.TransferTabAsync(failedSource, (_, _) => Task.FromResult((window, tab)));
                            Check(failedMove == null && source.Tabs.Contains(failedSource) && failedSource.SearchQuery == "keep my results");
                        }
                        finally { hwndField.SetValue(host, hwnd); }

                        var targetSearchBox = (TextBox)window.FindName("SearchTextBox");
                        bool edited = false;
                        void EditTargetQuery(object? sender, EventArgs args)
                        {
                            if (edited || !host.IsSearchRunning) return;
                            edited = true;
                            targetSearchBox.Text = "other";
                        }
                        host.SearchStateChanged += EditTargetQuery;
                        try
                        {
                            var takenOver = await source.TransferTabAsync(failedSource, (_, _) => Task.FromResult((window, tab)));
                            Check(edited && takenOver == window && !source.Tabs.Contains(failedSource) && tab.SearchQuery == "other");
                        }
                        finally { host.SearchStateChanged -= EditTargetQuery; }

                        host.ExitSearchMode();
                        var editedSource = source.AddNewTab(pidl, true, false);
                        var sourceBox = (TextBox)source.FindName("SearchTextBox");
                        sourceBox.Text = "match";
                        var movedWithEdits = await source.TransferTabAsync(editedSource, (_, _) =>
                        {
                            sourceBox.Text = "changed";
                            sourceBox.Text = "match";
                            return Task.FromResult((window, tab));
                        });
                        Check(movedWithEdits == window && source.Tabs.Contains(editedSource) && editedSource.SearchQuery == "match");
                    }
                    finally
                    {
                        host.SearchStateChanged -= DelayResultsView;
                        if (withheld != null) searchBrowser.SetValue(host, withheld);
                        source.PrepareForForceExit();
                        source.Close();
                    }
                }
                finally
                {
                    if (built != IntPtr.Zero)
                        typeof(ExplorerBrowserHost).GetMethod("DestroyWindowCore", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(host, new object[] { new HandleRef(null, built) });
                    if (parent != IntPtr.Zero) NativeMethods.DestroyWindow(parent);
                    PidlManager.Free(ref pidl);
                }
            })));
        }
    }
}
