using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using LeafTab.Controls;
using LeafTab.Interop;
using LeafTab.Models;
using LeafTab.Services;

namespace LeafTab.RegressionTests
{
    internal static partial class Program
    {
        private static void RunSearchFixTests()
        {
            Test("LeafTab filters keep identical semantics with an available Everything server", () =>
            {
                string root = MakeDirectory("search-fix-foo");
                string sub = Path.Combine(root, "sub");
                Directory.CreateDirectory(sub);
                string code = Path.Combine(sub, "alpha.cs");
                string text = Path.Combine(root, "beta.txt");
                File.WriteAllText(code, "// code");
                File.WriteAllBytes(text, new byte[1024]);
                File.WriteAllText(Path.Combine(root, "photo.jpg"), "image fixture");
                File.WriteAllText(Path.Combine(root, "pack.zip"), "archive fixture");
                File.WriteAllText(Path.Combine(root, ".gitignore"), "dotfile fixture");
                Directory.CreateDirectory(Path.Combine(root, "folder.txt"));
                string ipcClass = "LeafTabSemanticFix_" + Guid.NewGuid().ToString("N");
                using var server = new MockEverythingIpcServer(ipcClass, root, code, text, text, sub);
                server.Mode = MockEverythingMode.PagedDataset;
                server.PagedItems = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                    .Select(path => Directory.Exists(path) ? (1u, path, 0UL) : (0u, path, (ulong)new FileInfo(path).Length)).ToArray();
                using var available = new EverythingIpcClient(ipcClass);
                using var disabled = new EverythingIpcClient("LeafTabMissing_" + Guid.NewGuid().ToString("N"));
                Check(available.IsAvailable, "Mock Everything is unavailable.");
                var indexed = new FastFileSearchService(available);
                var native = new FastFileSearchService(disabled);
                foreach (string query in new[]
                {
                    "date:today", "kind:code", "dir:", "type:code", "type:archive", "type:image", "type:doc",
                    "path:foo", @"path:regex:^sub\\alpha\.cs$", "size:1kb", "dm:today", "ext:.cs",
                    "size:>1000b | ext:cs", "alpha | beta", @"sub\alpha", @"regex:^alpha\.cs$",
                    @"regex:^(alpha|beta)\.(cs|txt)$", @"path:regex:^sub/alpha\.cs$", "ext:cs,txt"
                })
                {
                    string[] expected = SearchFixPaths(native, query, root, out _);
                    string[] actual = SearchFixPaths(indexed, query, root, out SearchExecutionReport report);
                    Check(actual.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase), "Backend mismatch for " + query);
                    Check(report.Backend != SearchBackendKind.Everything, "An incompatible IPC prefilter was used for " + query);
                }
                Check(server.QueryCount == 0, "Incompatible queries should not first fetch a lossy candidate set.");
                foreach (string query in new[] { "ext:cs;txt", "ext:gitignore", "ext:txt", "folder:", "file:", "file:alpha", "folder:sub", "ext:cs alpha" })
                {
                    string[] expected = SearchFixPaths(native, query, root, out _);
                    string[] actual = SearchFixPaths(indexed, query, root, out SearchExecutionReport report);
                    Check(report.Backend == SearchBackendKind.Everything, "Indexed acceleration was lost for " + query);
                    Check(actual.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase), "Indexed filter mismatch for " + query);
                }
                SearchFixPaths(indexed, "alpha", root, out SearchExecutionReport plain);
                Check(plain.Backend == SearchBackendKind.Everything && server.QueryCount > 0,
                    "Ordinary filename searches must retain the indexed backend.");
                server.ResetQueryCount();
                SearchFixPaths(indexed, "content:fixture", root, out SearchExecutionReport advanced);
                Check(advanced.Backend == SearchBackendKind.Everything && server.QueryCount > 0,
                    "Everything-only functions must still reach Everything.");
            });

            Test("Everything scope groups the complete advanced OR expression", () =>
            {
                string query = FastFileSearchService.BuildEverythingScopedQuery(@"D:\scope", "content:a | content:b");
                Check(query == "\"D:\\scope\\\" <content:a | content:b>", query);
            });

            Test("Single-root global scans deduplicate priority folders and do not exhaust the result budget twice", () =>
            {
                string root = MakeDirectory("single-root-priority");
                string priority = Path.Combine(root, "priority");
                Directory.CreateDirectory(priority);
                File.WriteAllText(Path.Combine(priority, "one.txt"), "one");
                File.WriteAllText(Path.Combine(priority, "two.txt"), "two");
                File.WriteAllText(Path.Combine(root, "three.txt"), "three");
                using var disabled = new EverythingIpcClient("LeafTabMissing_" + Guid.NewGuid().ToString("N"));
                var service = new FastFileSearchService(disabled);
                var hits = new List<SearchResultEntry>();
                SearchExecutionReport report = service.ScanRootsWithWin32(new[] { root + "\\" }, null,
                    true, 3, FastFileSearchService.ParseQuery("ext:txt"),
                    (items, _) => hits.AddRange(items), Stopwatch.StartNew(), CancellationToken.None,
                    new[] { priority });
                Check(hits.Count == 3 && hits.Select(x => x.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 3);
                Check(!report.IsTruncated, "Duplicate traversal falsely consumed the three-file result limit.");
            });

            Test("Virtual-child filtering uses actual kinds, sizes and modified dates", () =>
            {
                string root = MakeDirectory("virtual-filter-fix");
                Directory.CreateDirectory(Path.Combine(root, "folder"));
                string old = Path.Combine(root, "old.txt");
                string empty = Path.Combine(root, "empty.txt");
                File.WriteAllBytes(old, new byte[1024]);
                File.SetLastWriteTime(old, new DateTime(2001, 1, 2, 12, 0, 0));
                File.WriteAllText(empty, string.Empty);
                IntPtr pidl = PidlManager.TryParseLocalOrShellFast(root);
                try
                {
                    Check(pidl != IntPtr.Zero);
                    Check(ExplorerBrowserHost.CollectMatchingVirtualChildren(pidl, "folder:").Items.Count == 1);
                    Check(ExplorerBrowserHost.CollectMatchingVirtualChildren(pidl, "file:").Items.Count == 2);
                    Check(ExplorerBrowserHost.CollectMatchingVirtualChildren(pidl, "size:1kb").Items.Count == 1);
                    Check(ExplorerBrowserHost.CollectMatchingVirtualChildren(pidl, "size:empty").Items.Count == 1);
                    Check(ExplorerBrowserHost.CollectMatchingVirtualChildren(pidl, "size:>999tb").Items.Count == 0);
                    Check(ExplorerBrowserHost.CollectMatchingVirtualChildren(pidl, "dm:2001").Items.Count == 1);
                    Check(ExplorerBrowserHost.CollectMatchingVirtualChildren(pidl, "folder: | size:1kb").Items.Count == 2);
                }
                finally { PidlManager.Free(ref pidl); }
                var unknown = new SearchResultEntry("unknown.txt", NativeMethods.FILE_ATTRIBUTE_NORMAL, 0, default,
                    hasFileSize: false, hasLastWriteTime: false);
                Check(!FastFileSearchService.ParseQuery("size:empty").MatchesEntry("unknown.txt", "unknown.txt", in unknown));
                Check(!FastFileSearchService.ParseQuery("dm:<2020").MatchesEntry("unknown.txt", "unknown.txt", in unknown));
            });
        }

        private static string[] SearchFixPaths(FastFileSearchService service, string query, string root,
            out SearchExecutionReport report)
        {
            var hits = new List<SearchResultEntry>();
            report = service.SearchAsync(query, root, true, 100, (items, _) => hits.AddRange(items), CancellationToken.None)
                .GetAwaiter().GetResult();
            return hits.Select(x => x.FullPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        // Runs only when explicitly requested; no Window.Show or desktop input.
        private static int RunSearchHistoryFixNativeTests()
        {
            int oleHr = SearchNavNative.OleInitialize(IntPtr.Zero);
            if (oleHr < 0) throw new COMException("Cannot initialize Shell test apartment.", oleHr);
            SynchronizationContext? previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var frame = new DispatcherFrame();
            using var deadline = new Timer(_ => Environment.Exit(1), null, 60000, Timeout.Infinite);
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
            {
                try { await VerifySearchHistoryFixesAsync(); }
                catch (Exception ex) { _failed++; Console.WriteLine("FAIL: History regression — " + ex); }
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

        private static async Task VerifySearchHistoryFixesAsync()
        {
            string source = MakeDirectory("history-fix-source");
            string destination = MakeDirectory("history-fix-destination");
            File.WriteAllText(Path.Combine(source, "match.txt"), "history fixture");
            IntPtr sourcePidl = PidlManager.TryParseLocalOrShellFast(source);
            IntPtr destinationPidl = PidlManager.TryParseLocalOrShellFast(destination);
            IntPtr parent = IntPtr.Zero;
            IntPtr built = IntPtr.Zero;
            ExplorerTabItem? tab = null;
            try
            {
                parent = NativeMethods.CreateWindowExW(0x08000080, "STATIC", "LeafTab hidden history regression",
                    unchecked((int)0x82000000), -20000, -20000, 960, 640, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Check(parent != IntPtr.Zero && sourcePidl != IntPtr.Zero && destinationPidl != IntPtr.Zero);
                tab = new ExplorerTabItem(sourcePidl);
                ExplorerTabItem current = tab;
                ExplorerBrowserHost host = tab.BrowserHost;
                int navigationCount = 0;
                host.NavigationCompleted += (_, args) => { current.UpdateFromNavigation(args.FolderPidl); navigationCount++; };
                host.Measure(new Size(960, 640));
                host.Arrange(new Rect(0, 0, 960, 640));
                built = ((HandleRef)typeof(ExplorerBrowserHost).GetMethod("BuildWindowCore", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(host, new object[] { new HandleRef(null, parent) })!).Handle;
                Check(await WaitNativeConditionAsync(() => navigationCount > 0));

                tab.RestoreHistory(new[] { source, destination }, 0);
                tab.SearchQuery = "match";
                await host.StartSearchAsync("match", sourcePidl, source);
                Test("Search history rejects forward shortcuts even when a forward entry exists", () =>
                {
                    Check(!tab.NavigateHistoryStep(+1) && host.IsShowingSearchResults);
                });
                string? querySeenOnExit = null;
                host.SearchStateChanged += (_, _) => { if (!host.IsShowingSearchResults) querySeenOnExit = tab.SearchQuery; };
                Test("Search back publishes exit before clearing the model query", () =>
                {
                    Check(tab.NavigateHistoryStep(-1));
                    Check(querySeenOnExit == "match" && tab.SearchQuery == string.Empty && !host.IsShowingSearchResults);
                });

                tab.RestoreHistory(new[] { @"\\leaftab-fixture\share", source }, 1);
                var firstResolution = new TaskCompletionSource<(IntPtr, string?)>();
                CancellationToken oldToken = default;
                Task<bool> stale = tab.NavigateHistoryStepAsync(-1, (_, token) => { oldToken = token; return firstResolution.Task; });
                Check(!stale.IsCompleted, "History blocked on an unresolved network path.");
                host.NavigateToPidl(destinationPidl);
                Check(await WaitNativeConditionAsync(() => PidlManager.AreEqual(tab.CurrentPidl, destinationPidl)));
                firstResolution.SetResult((PidlManager.Clone(sourcePidl), null));
                bool staleApplied = await stale;
                Test("A new folder navigation cancels pending UNC history and rejects its late PIDL", () =>
                {
                    Check(oldToken.IsCancellationRequested && !staleApplied);
                    Check(PidlManager.AreEqual(tab.CurrentPidl, destinationPidl));
                });

                tab.RestoreHistory(new[] { @"\\leaftab-fixture\share", destination }, 1);
                bool accepted = await tab.NavigateHistoryStepAsync(-1,
                    (_, _) => Task.FromResult((PidlManager.Clone(sourcePidl), (string?)null)));
                Check(await WaitNativeConditionAsync(() => PidlManager.AreEqual(tab.CurrentPidl, sourcePidl)));
                Test("Restored UNC history resolves asynchronously and restores its history position", () =>
                {
                    Check(accepted && !tab.CanNavigateBack && tab.CanNavigateForward);
                });

                tab.RestoreHistory(new[] { @"\\leaftab-fixture\share", source }, 1);
                var finalResolution = new TaskCompletionSource<(IntPtr, string?)>();
                CancellationToken finalToken = default;
                Task<bool> closed = tab.NavigateHistoryStepAsync(-1, (_, token) => { finalToken = token; return finalResolution.Task; });
                typeof(ExplorerBrowserHost).GetMethod("DestroyWindowCore", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(host, new object[] { new HandleRef(host, built) });
                built = IntPtr.Zero;
                tab.Dispose();
                finalResolution.SetResult((PidlManager.Clone(destinationPidl), null));
                bool appliedAfterClose = await closed;
                Test("Closing a tab cancels pending network history without applying a late navigation", () =>
                    Check(finalToken.IsCancellationRequested && !appliedAfterClose));
                tab = null;
            }
            finally
            {
                if (tab != null)
                {
                    if (built != IntPtr.Zero)
                        typeof(ExplorerBrowserHost).GetMethod("DestroyWindowCore", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(tab.BrowserHost, new object[] { new HandleRef(tab.BrowserHost, built) });
                    tab.Dispose();
                }
                if (parent != IntPtr.Zero) NativeMethods.DestroyWindow(parent);
                PidlManager.Free(ref sourcePidl);
                PidlManager.Free(ref destinationPidl);
            }
        }
    }
}
