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

            Test("An 8.3 search root accepts indexed results with long paths", () =>
            {
                string root = MakeDirectory("indexed long path fixture");
                string? shortRoot = TryGetShortTestPath(root);
                if (shortRoot == null)
                {
                    Console.WriteLine("8.3 aliases are unavailable on the fixture volume.");
                    return;
                }

                string file = Path.Combine(root, "scoped-match.txt");
                File.WriteAllText(file, "match");
                string ipcClass = "LeafTabShortScope_" + Guid.NewGuid().ToString("N");
                using var server = new MockEverythingIpcServer(ipcClass, root, file, file, file, root)
                {
                    Mode = MockEverythingMode.PagedDataset,
                    PagedItems = new[] { (0u, file, 5UL) }
                };
                using var client = new EverythingIpcClient(ipcClass);
                string[] paths = SearchFixPaths(new FastFileSearchService(client), "match", shortRoot, out SearchExecutionReport report);
                Check(report.Backend == SearchBackendKind.Everything && paths.Length == 1 &&
                    string.Equals(paths[0], file, StringComparison.OrdinalIgnoreCase),
                    "The long indexed path was lost under an 8.3 search scope.");
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

            Test("This PC matches paths relative to each drive, including priority folders", () =>
            {
                string[] roots = { MakeDirectory("global-drive-A"), MakeDirectory("global-drive-B") };
                string[] expected = roots.Select(r => Path.Combine(r, "reports", "annual.txt")).ToArray();
                foreach (string file in expected)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllText(file, "annual");
                }
                using var disabled = new EverythingIpcClient("LeafTabMissing_" + Guid.NewGuid().ToString("N"));
                var service = new FastFileSearchService(disabled);
                foreach (string query in new[] { @"path:reports\annual.txt", @"path:regex:^reports\\annual\.txt$", @"reports\annual.txt" })
                {
                    var paths = new List<string>();
                    service.ScanRootsWithWin32(roots, null, true, 10, FastFileSearchService.ParseQuery(query),
                        (items, _) => paths.AddRange(items.Select(item => item.FullPath)), Stopwatch.StartNew(),
                        CancellationToken.None, new[] { Path.GetDirectoryName(expected[1])! });
                    Check(paths.Count == expected.Length && new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase).SetEquals(expected),
                        "Wrong multi-drive results for " + query);
                }
            });

            Test("A flat scan streams its first batch, honors cancellation, and caches only complete scans", () =>
            {
                string root = MakeDirectory("flat-stream");
                const int files = 180;
                for (int i = 0; i < files; i++)
                    File.WriteAllText(Path.Combine(root, i.ToString("D3") + ".txt"), string.Empty);

                int examined = 0;
                var query = new FastFileSearchService.ParsedSearchQuery(new[]
                {
                    new FastFileSearchService.SearchClause((_, _) => { Interlocked.Increment(ref examined); return true; },
                        null, requireDirectory: false, requireFile: true)
                }, false, null);
                using var disabled = new EverythingIpcClient("LeafTabMissing_" + Guid.NewGuid().ToString("N"));
                var service = new FastFileSearchService(disabled);
                using var cancel = new CancellationTokenSource();
                SearchExecutionReport stopped = service.ScanRootsWithWin32(new[] { root }, root, true, 100, query,
                    (items, index) =>
                    {
                        // One flat directory has exactly one enumerator. The number of
                        // predicates evaluated tells us how far it got before delivery.
                        Check(index == 0 && items.Length == 48 && examined == items.Length);
                        cancel.Cancel();
                    }, Stopwatch.StartNew(), cancel.Token);
                Check(examined == 48 && stopped.MatchedCount == 48);

                examined = 0;
                var sizes = new List<int>();
                SearchExecutionReport limited = service.ScanRootsWithWin32(new[] { root }, root, true, 100, query,
                    (items, index) => { Check(index == sizes.Count); sizes.Add(items.Length); },
                    Stopwatch.StartNew(), CancellationToken.None);
                Check(examined == 101 && limited.IsTruncated && limited.MatchedCount == 100 &&
                    sizes.SequenceEqual(new[] { 48, 52 }));

                SearchExecutionReport exact = service.SearchAsync("ext:txt", root, true, files, (_, _) => { },
                    CancellationToken.None).GetAwaiter().GetResult();
                Check(exact.Backend == SearchBackendKind.NativeFastScan && exact.MatchedCount == files && !exact.IsTruncated);
                SearchExecutionReport warm = service.SearchAsync("ext:txt", root, true, files, (_, _) => { },
                    CancellationToken.None).GetAwaiter().GetResult();
                Check(warm.Backend == SearchBackendKind.MemoryCache && warm.MatchedCount == files && !warm.IsTruncated);
            });

            Test("Concurrent directories deliver complete numbered batches in order", () =>
            {
                string root = MakeDirectory("parallel-batches");
                for (int d = 0; d < 8; d++)
                {
                    string directory = Path.Combine(root, d.ToString());
                    Directory.CreateDirectory(directory);
                    for (int i = 0; i < 75; i++)
                        File.WriteAllText(Path.Combine(directory, i.ToString() + ".txt"), string.Empty);
                }
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var sizes = new List<int>();
                using var disabled = new EverythingIpcClient("LeafTabMissing_" + Guid.NewGuid().ToString("N"));
                var service = new FastFileSearchService(disabled);
                SearchExecutionReport report = service.ScanRootsWithWin32(new[] { root }, root, true, 400,
                    FastFileSearchService.ParseQuery("ext:txt"), (items, index) =>
                    {
                        Check(index == sizes.Count, "Out-of-order batch index " + index);
                        sizes.Add(items.Length);
                        foreach (SearchResultEntry item in items) Check(paths.Add(item.FullPath));
                    }, Stopwatch.StartNew(), CancellationToken.None);
                Check(report.IsTruncated && report.MatchedCount == 400 && paths.Count == 400);
                Check(sizes.SequenceEqual(new[] { 48, 96, 96, 96, 64 }));
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

            Test("Slow batch delivery does not block parallel workers or reorder batches", () =>
            {
                string root = MakeDirectory("nonblocking-batches");
                for (int d = 0; d < 8; d++)
                {
                    string directory = Path.Combine(root, d.ToString());
                    Directory.CreateDirectory(directory);
                    for (int i = 0; i < 75; i++)
                        File.WriteAllText(Path.Combine(directory, i.ToString() + ".txt"), string.Empty);
                }
                int evaluated = 0;
                int evaluatedDuringSlowBatch0 = 0;
                var query = new FastFileSearchService.ParsedSearchQuery(new[]
                {
                    new FastFileSearchService.SearchClause((_, _) => { Interlocked.Increment(ref evaluated); return true; },
                        null, requireDirectory: false, requireFile: true)
                }, false, null);
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var batchIndices = new List<int>();
                using var disabled = new EverythingIpcClient("LeafTabMissing_" + Guid.NewGuid().ToString("N"));
                var service = new FastFileSearchService(disabled);
                SearchExecutionReport report = service.ScanRootsWithWin32(new[] { root }, root, true, 400, query,
                    (items, index) =>
                    {
                        batchIndices.Add(index);
                        foreach (SearchResultEntry item in items) Check(paths.Add(item.FullPath));
                        if (index == 0)
                        {
                            Thread.Sleep(120);
                            evaluatedDuringSlowBatch0 = Volatile.Read(ref evaluated);
                        }
                    }, Stopwatch.StartNew(), CancellationToken.None);
                Check(report.IsTruncated && report.MatchedCount == 400 && paths.Count == 400);
                Check(batchIndices.SequenceEqual(Enumerable.Range(0, batchIndices.Count)));
                Check(evaluatedDuringSlowBatch0 > 48, "Parallel scanning stalled behind slow batch 0 delivery.");
            });

            Test("ScanRootsWithWin32 returns cleanly when pre-canceled", () =>
            {
                string root = MakeDirectory("precanceled-scan");
                File.WriteAllText(Path.Combine(root, "a.txt"), "a");
                using var disabled = new EverythingIpcClient("LeafTabMissing_" + Guid.NewGuid().ToString("N"));
                var service = new FastFileSearchService(disabled);
                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                SearchExecutionReport report = service.ScanRootsWithWin32(new[] { root }, root, true, 100,
                    FastFileSearchService.ParseQuery("ext:txt"), (_, _) => Check(false, "Canceled scan should not emit batches."),
                    Stopwatch.StartNew(), canceled.Token);
                Check(report.MatchedCount == 0 && !report.IsTruncated);
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

                await CheckSearchTimeoutAndRetryAsync(host, sourcePidl, source);
                await CheckRapidSearchCancelSafetyAsync(host, sourcePidl, source);
                await CheckFirstInitSearchTimeoutRebuildAsync(parent, sourcePidl, destinationPidl, source);
                await CheckVirtualSearchLoadMoreAsync(host);
                await CheckConcurrentVirtualSearchAsync(host);

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

        private static async Task CheckSearchTimeoutAndRetryAsync(ExplorerBrowserHost host, IntPtr folder, string path)
        {
            // Hold the results browser out of view while the normal search entry point
            // waits for its Shell folder. Restore the same COM object for the retry.
            var field = typeof(ExplorerBrowserHost).GetField("_searchBrowser", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var resultsBrowser = field.GetValue(host);
            CancellationToken worker = default;
            bool observedTimeout = false;
            void OnSearchStateChanged(object? sender, EventArgs e)
            {
                if (host.IsSearchRunning && !worker.CanBeCanceled)
                {
                    worker = NativeField<CancellationTokenSource>(host, "_searchCts").Token;
                    field.SetValue(host, null);
                }
                else if (!host.IsSearchRunning && host.LastSearchNotice != null)
                    observedTimeout = true;
            }
            host.SearchStateChanged += OnSearchStateChanged;
            try
            {
                int found = await host.StartSearchAsync("ext:txt", folder, path);
                Test("A search view timeout cancels the scanner and publishes a completed retryable state", () =>
                    Check(found == 0 && observedTimeout && worker.IsCancellationRequested && !host.IsSearchRunning));
            }
            finally
            {
                host.SearchStateChanged -= OnSearchStateChanged;
                field.SetValue(host, resultsBrowser);
            }
            int retry = await host.StartSearchAsync("match", folder, path);
            Test("A search can be retried after the view times out", () =>
                Check(retry == 1 && host.LastSearchNotice == null && !host.IsSearchRunning));
            host.ExitSearchMode();
        }

        private static async Task CheckRapidSearchCancelSafetyAsync(ExplorerBrowserHost host, IntPtr folder, string path)
        {
            Task<int> t1 = host.StartSearchAsync("nonexistent-a", folder, path);
            Task<int> t2 = host.StartSearchAsync("ext:txt", folder, path);
            Task<int> t3 = host.StartSearchAsync("match", folder, path);
            await Task.WhenAll(t1, t2, t3);
            int finalCount = await t3;
            Test("Rapid consecutive searches cancel cleanly without ObjectDisposedException", () =>
                Check(finalCount == 1 && host.IsShowingSearchResults && !host.IsSearchRunning && host.SearchMatchCount == 1));
            host.ExitSearchMode();
        }

        private static async Task CheckFirstInitSearchTimeoutRebuildAsync(IntPtr parent, IntPtr sourcePidl, IntPtr destinationPidl, string sourcePath)
        {
            IntPtr freshBuilt = IntPtr.Zero;
            var freshTab = new ExplorerTabItem(sourcePidl);
            ExplorerBrowserHost freshHost = freshTab.BrowserHost;
            try
            {
                int navCount = 0;
                freshHost.NavigationCompleted += (_, args) => { freshTab.UpdateFromNavigation(args.FolderPidl); navCount++; };
                freshHost.Measure(new Size(960, 640));
                freshHost.Arrange(new Rect(0, 0, 960, 640));
                freshBuilt = ((HandleRef)typeof(ExplorerBrowserHost).GetMethod("BuildWindowCore", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(freshHost, new object[] { new HandleRef(null, parent) })!).Handle;
                Check(await WaitNativeConditionAsync(() => navCount > 0));

                freshHost.SimulateSearchViewTimeoutForTest = true;
                int first = await freshHost.StartSearchAsync("match", sourcePidl, sourcePath);
                Check(first == 0 && freshHost.LastSearchNotice != null && !freshHost.IsSearchRunning);

                // 验证当 ResultsFolder 未完成初始化时，迟到的 OnNavigationPending 不会把 PIDL 路由给主浏览器
                var resultsPidlField = typeof(ExplorerBrowserHost).GetField("_searchResultsPidl", BindingFlags.Instance | BindingFlags.NonPublic)!;
                IntPtr savedResultsPidl = (IntPtr)resultsPidlField.GetValue(freshHost)!;
                resultsPidlField.SetValue(freshHost, IntPtr.Zero);
                PidlManager.Free(ref savedResultsPidl);
                object sink = typeof(ExplorerBrowserHost).GetField("_searchEventSink", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(freshHost)!;
                int hr = (int)sink.GetType().GetMethod("OnNavigationPending")!.Invoke(sink, new object[] { destinationPidl })!;
                await Dispatcher.Yield(DispatcherPriority.Background);
                Check(hr != ShellConstants.S_OK && PidlManager.AreEqual(freshTab.CurrentPidl, sourcePidl));

                // 模拟首次 FillFromObject 留下的半初始化 _searchBrowser（无 ResultsFolder 视图），验证重试会自动销毁并重建成功
                var browserField = typeof(ExplorerBrowserHost).GetField("_searchBrowser", BindingFlags.Instance | BindingFlags.NonPublic)!;
                object? halfInitialized = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellConstants.CLSID_ExplorerBrowser, true)!);
                object? previousBrowser = browserField.GetValue(freshHost);
                browserField.SetValue(freshHost, halfInitialized);
                if (previousBrowser != null && Marshal.IsComObject(previousBrowser))
                    Marshal.FinalReleaseComObject(previousBrowser);

                freshHost.SimulateSearchViewTimeoutForTest = false;
                int retry = await freshHost.StartSearchAsync("match", sourcePidl, sourcePath);
                object? rebuiltBrowser = browserField.GetValue(freshHost);
                Test("First search timeout destroys half-initialized browser, guards late callbacks, and rebuilds on retry", () =>
                    Check(retry == 1 && !ReferenceEquals(rebuiltBrowser, halfInitialized) &&
                          freshHost.LastSearchNotice == null && !freshHost.IsSearchRunning));
            }
            finally
            {
                freshHost.SimulateSearchViewTimeoutForTest = false;
                if (freshBuilt != IntPtr.Zero)
                    typeof(ExplorerBrowserHost).GetMethod("DestroyWindowCore", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(freshHost, new object[] { new HandleRef(freshHost, freshBuilt) });
                freshTab.Dispose();
            }
        }

        private static async Task CheckVirtualSearchLoadMoreAsync(ExplorerBrowserHost host)
        {
            string fixture = MakeDirectory("virtual-pagination");
            for (int i = 0; i < 1001; i++)
                File.WriteAllText(Path.Combine(fixture, i.ToString("D4") + ".txt"), string.Empty);
            IntPtr folder = PidlManager.TryParseLocalOrShellFast(fixture);
            try
            {
                // A fixture folder supplies real Shell children without using the user's Recycle Bin.
                int first = await host.StartSearchAsync("file:", folder, folderFileSystemPath: null);
                Check(first == ExplorerBrowserHost.DefaultMaxSearchResults && host.CanLoadMoreSearchResults);
                int expanded = await host.LoadMoreSearchResultsAsync();
                Test("A virtual folder can load search matches beyond the first thousand", () =>
                    Check(expanded == 1001 && !host.CanLoadMoreSearchResults));
            }
            finally
            {
                host.ExitSearchMode();
                PidlManager.Free(ref folder);
            }
        }

        private static async Task CheckConcurrentVirtualSearchAsync(ExplorerBrowserHost host)
        {
            string folders = MakeDirectory("mixed-search");
            string virtualRoot = Directory.CreateDirectory(Path.Combine(folders, "devices")).FullName;
            string device = Path.Combine(virtualRoot, "device.txt");
            string indexed = Path.Combine(folders, "indexed.txt");
            File.WriteAllText(device, "device");
            File.WriteAllText(indexed, "indexed");
            string ipcClass = "LeafTabMixedSearch_" + Guid.NewGuid().ToString("N");
            using var server = new MockEverythingIpcServer(ipcClass, folders, indexed, indexed, indexed, folders)
            {
                Mode = MockEverythingMode.PagedDataset,
                PagedItems = new[] { (0u, indexed, 7UL) }
            };
            using var client = new EverythingIpcClient(ipcClass);
            var originalService = host.FileSearchService;
            var originalProvider = host.VirtualChildrenProvider;
            var originalVisibility = host.SearchHiddenItemsProvider;
            IntPtr pc = PidlManager.CreateThisPcPidl();
            IntPtr fixture = PidlManager.TryParseLocalOrShellFast(virtualRoot);
            byte[] fixtureBytes = PidlManager.ToBytes(fixture);
            using var release = new ManualResetEventSlim();
            try
            {
                host.FileSearchService = new FastFileSearchService(client);
                host.SearchHiddenItemsProvider = () => true;
                host.VirtualChildrenProvider = (_, query, cap, token) =>
                {
                    release.Wait(token);
                    IntPtr copy = PidlManager.FromBytes(fixtureBytes);
                    try { return ExplorerBrowserHost.CollectMatchingVirtualChildren(copy, query, cap, token); }
                    finally { PidlManager.Free(ref copy); }
                };
                Task<bool> delayed = host.StartSearchForMoveAsync("file:", pc, null, 2);
                Check(await WaitNativeConditionAsync(() => host.SearchMatchCount == 1 && host.IsSearchRunning),
                    $"File results did not stream: count={host.SearchMatchCount}, running={host.IsSearchRunning}, IPC queries={server.QueryCount}.");
                await Task.Delay(1600);
                Check(!delayed.IsCompleted && host.LastSearchNotice == null);
                release.Set();
                bool completed = await delayed;
                Test("This PC streams indexed files and merges devices arriving after 1.5 seconds", () =>
                    Check(completed && host.SearchMatchCount == 2 && !host.IsSearchTruncated && host.LastSearchNotice == null));

                int limited = await host.StartSearchAsync("file:", pc, null, 1);
                Check(await host.SelectChildItemWhenReadyAsync(Path.GetFileName(device)));
                Test("Devices and file matches share one limit and loading more returns both", () =>
                    Check(limited == 1 && host.IsSearchTruncated && host.CanLoadMoreSearchResults));
                int expanded = await host.LoadMoreSearchResultsAsync();
                Check(expanded == 2 && !host.IsSearchTruncated);

                release.Reset();
                Task<bool> stale = host.StartSearchForMoveAsync("file:", pc, null, 100);
                Check(await WaitNativeConditionAsync(() => host.SearchMatchCount == 1 && host.IsSearchRunning));
                host.ExitSearchMode();
                Check(!await stale && !host.IsShowingSearchResults);
                release.Set();

                host.VirtualChildrenProvider = (_, _, _, _) => throw new TimeoutException();
                bool partial = await host.StartSearchForMoveAsync("file:", pc, null, 100);
                Test("Device timeout preserves filesystem matches and marks the search incomplete", () =>
                    Check(!partial && host.SearchMatchCount == 1 && host.LastSearchNotice != null && !host.IsSearchRunning));
                host.ExitSearchMode();

                host.VirtualChildrenProvider = (_, _, _, _) => throw new UnauthorizedAccessException();
                bool denied = await host.StartSearchForMoveAsync("file:", fixture, null, 100);
                Check(!denied && host.SearchMatchCount == 0 && host.LastSearchNotice != null);
                host.VirtualChildrenProvider = originalProvider;
                bool noMatches = await host.StartSearchForMoveAsync("missing-name", fixture, null, 100);
                Test("An unreadable virtual folder is distinct from a successful search with zero matches", () =>
                    Check(noMatches && host.SearchMatchCount == 0 && host.LastSearchNotice == null));
                host.ExitSearchMode();
                IntPtr plainFile = PidlManager.TryParseLocalOrShellFast(indexed);
                try
                {
                    bool bound = await host.StartSearchForMoveAsync("file:", plainFile, null, 2);
                    Test("A Shell item that cannot bind as a folder publishes an enumeration failure", () =>
                        Check(!bound && host.LastSearchNotice != null && !host.IsSearchRunning));
                }
                finally { PidlManager.Free(ref plainFile); }
            }
            finally
            {
                release.Set();
                host.ExitSearchMode();
                host.VirtualChildrenProvider = originalProvider;
                host.FileSearchService = originalService;
                host.SearchHiddenItemsProvider = originalVisibility;
                PidlManager.Free(ref pc);
                PidlManager.Free(ref fixture);
            }
        }
    }
}
