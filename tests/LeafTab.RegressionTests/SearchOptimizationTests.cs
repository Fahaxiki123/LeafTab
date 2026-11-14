using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LeafTab.Interop;
using LeafTab.Services;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace LeafTab.RegressionTests
{
    internal static partial class Program
    {
        static partial void RunSearchOptimizationTests()
        {
            Test("Win32 structs match x64 ABI and FastPidlBinder builds PIDLs without disk stats", () =>
            {
                Check(Marshal.SizeOf<WIN32_FIND_DATAW>() == 592, $"Unexpected WIN32_FIND_DATAW size: {Marshal.SizeOf<WIN32_FIND_DATAW>()}");
                Check(Marshal.SizeOf<COPYDATASTRUCT>() == (IntPtr.Size == 8 ? 24 : 12), $"Unexpected COPYDATASTRUCT size: {Marshal.SizeOf<COPYDATASTRUCT>()}");

                string dir = MakeDirectory("fast-pidl-binder");
                string syntheticLeaf = Path.Combine(dir, "nonexistent_synthetic_leaf.cs");
                Check(!File.Exists(syntheticLeaf), "Synthetic path must not exist on disk for this test.");

                string realSubdir = Path.Combine(dir, "real_folder");
                Directory.CreateDirectory(realSubdir);

                var ft = new FILETIME { dwLowDateTime = 0x12345678, dwHighDateTime = 0x01DB0000 };
                using var binder = new PidlManager.FastPidlBinder();

                IntPtr syntheticPidl = binder.CreatePidlFromMetadata(syntheticLeaf, NativeMethods.FILE_ATTRIBUTE_NORMAL, 4096UL, ft);
                try
                {
                    Check(syntheticPidl != IntPtr.Zero, "FastPidlBinder failed to synthesize PIDL for unstated leaf.");
                    Guid iidShellItem = ShellConstants.IID_IShellItem;
                    Check(NativeMethods.SHCreateItemFromIDList(syntheticPidl, ref iidShellItem, out IShellItem? item) == ShellConstants.S_OK && item != null);
                    try
                    {
                        string? disp = PidlManager.GetShellItemString(item!, SIGDN.SIGDN_NORMALDISPLAY);
                        Check(disp != null && disp.IndexOf("nonexistent_synthetic_leaf", StringComparison.OrdinalIgnoreCase) >= 0,
                            $"Unexpected display name: '{disp}'");
                    }
                    finally
                    {
                        if (item != null) Marshal.ReleaseComObject(item);
                    }
                }
                finally
                {
                    PidlManager.Free(ref syntheticPidl);
                }

                IntPtr folderPidl = binder.CreatePidlFromMetadata(realSubdir, NativeMethods.FILE_ATTRIBUTE_DIRECTORY, 0UL, ft);
                try
                {
                    Check(folderPidl != IntPtr.Zero, "FastPidlBinder failed for directory entry.");
                }
                finally
                {
                    PidlManager.Free(ref folderPidl);
                }
            });

            Test("EverythingIpcClient supports QUERY2W, QUERYW fallback, scoping, and unindexed directory fallback", () =>
            {
                string indexedDir = MakeDirectory("ev-indexed");
                string otherDir = MakeDirectory("ev-other");
                string unindexedDir = MakeDirectory("ev-unindexed");

                string matchFile = Path.Combine(indexedDir, "alpha_match.txt");
                string hiddenFile = Path.Combine(indexedDir, "alpha_hidden.txt");
                string outsideFile = Path.Combine(otherDir, "alpha_outside.txt");
                string matchSubdir = Path.Combine(indexedDir, "alpha_subdir");
                string unindexedFile = Path.Combine(unindexedDir, "usb_only_match.txt");

                File.WriteAllText(matchFile, "hello");
                File.WriteAllText(hiddenFile, "secret");
                File.SetAttributes(hiddenFile, FileAttributes.Hidden);
                File.WriteAllText(outsideFile, "outside");
                Directory.CreateDirectory(matchSubdir);
                File.WriteAllText(unindexedFile, "usb");

                string mockClass = "LeafTabMockEverythingWnd_" + Guid.NewGuid().ToString("N");
                using var server = new MockEverythingIpcServer(mockClass, indexedDir, matchFile, hiddenFile, outsideFile, matchSubdir);
                using var client = new EverythingIpcClient(mockClass);
                var service = new FastFileSearchService(client);

                // 1. Standard QUERY2W search with scope filtering and hidden exclusion
                server.Mode = MockEverythingMode.NormalV2;
                var visibleHits = new List<SearchResultEntry>();
                SearchExecutionReport repVisible = service.SearchAsync(
                    "alpha",
                    indexedDir,
                    includeHidden: false,
                    maxResults: 100,
                    onBatch: (items, _) => { lock (visibleHits) visibleHits.AddRange(items); },
                    cancellationToken: CancellationToken.None).GetAwaiter().GetResult();

                Check(repVisible.Backend == SearchBackendKind.Everything, $"Expected Everything backend, got {repVisible.Backend}");
                Check(server.SawValidV2Header, "Mock server did not observe valid x64 QUERY2W header.");
                Check(visibleHits.Count == 2, $"Expected 2 visible scoped items, got {visibleHits.Count}");
                Check(visibleHits.Exists(e => string.Equals(e.FullPath, matchFile, StringComparison.OrdinalIgnoreCase) && e.FileSize == 12345UL),
                    "Expected alpha_match.txt with IPC FileSize=12345.");
                Check(visibleHits.Exists(e => string.Equals(e.FullPath, matchSubdir, StringComparison.OrdinalIgnoreCase)),
                    "Expected alpha_subdir in QUERY2W results.");

                // 2. Hidden files are surfaced when includeHidden is enabled
                var allHits = new List<SearchResultEntry>();
                SearchExecutionReport repAll = service.SearchAsync(
                    "alpha",
                    indexedDir,
                    includeHidden: true,
                    maxResults: 100,
                    onBatch: (items, _) => { lock (allHits) allHits.AddRange(items); },
                    cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                Check(repAll.Backend == SearchBackendKind.Everything && allHits.Count == 3,
                    $"Expected 3 items with hidden enabled, got {allHits.Count}");

                // 3. Unindexed directory (totitems=0 on query AND coverage probe) falls back to Tier 2 Win32 scan
                server.Mode = MockEverythingMode.UnindexedZero;
                var fallbackHits = new List<SearchResultEntry>();
                SearchExecutionReport repFallback = service.SearchAsync(
                    "usb_only",
                    unindexedDir,
                    includeHidden: false,
                    maxResults: 100,
                    onBatch: (items, _) => { lock (fallbackHits) fallbackHits.AddRange(items); },
                    cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                Check(repFallback.Backend == SearchBackendKind.NativeFastScan, $"Expected NativeFastScan fallback, got {repFallback.Backend}");
                Check(fallbackHits.Count == 1 && string.Equals(fallbackHits[0].FullPath, unindexedFile, StringComparison.OrdinalIgnoreCase),
                    "Fallback scan did not find unindexed file.");

                // 4. Legacy v1 QUERYW fallback when QUERY2W returns FALSE (0)
                server.Mode = MockEverythingMode.LegacyV1Only;
                var v1Hits = new List<SearchResultEntry>();
                SearchExecutionReport repV1 = service.SearchAsync(
                    "alpha",
                    indexedDir,
                    includeHidden: false,
                    maxResults: 100,
                    onBatch: (items, _) => { lock (v1Hits) v1Hits.AddRange(items); },
                    cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                Check(repV1.Backend == SearchBackendKind.Everything, $"Expected Everything backend on v1 fallback, got {repV1.Backend}");
                Check(v1Hits.Exists(e => string.Equals(e.FullPath, matchFile, StringComparison.OrdinalIgnoreCase)),
                    "Legacy QUERYW fallback did not parse LISTW item.");
            });

            Test("Win32 fast scanner covers deferred noisy folders, hidden filtering, multi-term queries, and cache invalidation", () =>
            {
                string root = MakeDirectory("win32-fast-scan");
                string srcDir = Path.Combine(root, "src");
                string nmDir = Path.Combine(root, "node_modules", "sample-pkg");
                Directory.CreateDirectory(srcDir);
                Directory.CreateDirectory(nmDir);

                string regularFile = Path.Combine(srcDir, "FastMatch.cs");
                string nmFile = Path.Combine(nmDir, "deep_nm_target.js");
                string hiddenFile = Path.Combine(srcDir, "ghost_secret.txt");
                string multiTermFile = Path.Combine(srcDir, "LeafTab_Explorer_Host_Notes.md");

                File.WriteAllText(regularFile, "// cs");
                File.WriteAllText(nmFile, "// js");
                File.WriteAllText(hiddenFile, "hidden");
                File.SetAttributes(hiddenFile, FileAttributes.Hidden);
                File.WriteAllText(multiTermFile, "# notes");

                // Force Tier 2 using an unreachable IPC class name
                using var disabledIpc = new EverythingIpcClient("LeafTabDisabledIpc_" + Guid.NewGuid().ToString("N"));
                var service = new FastFileSearchService(disabledIpc);

                // First search scans with FindFirstFileExW and populates complete RAM snapshot
                var firstResults = new List<SearchResultEntry>();
                SearchExecutionReport r1 = service.SearchAsync(
                    "*.cs",
                    root,
                    includeHidden: false,
                    maxResults: 100,
                    onBatch: (items, _) => { lock (firstResults) firstResults.AddRange(items); },
                    cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                Check(r1.Backend == SearchBackendKind.NativeFastScan, $"Expected NativeFastScan, got {r1.Backend}");
                Check(firstResults.Count == 1 && string.Equals(firstResults[0].FullPath, regularFile, StringComparison.OrdinalIgnoreCase));

                // Subsequent search hits MemoryCache
                var cachedResults = new List<SearchResultEntry>();
                SearchExecutionReport r2 = service.SearchAsync(
                    "FastMatch",
                    root,
                    includeHidden: false,
                    maxResults: 100,
                    onBatch: (items, _) => { lock (cachedResults) cachedResults.AddRange(items); },
                    cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                Check(r2.Backend == SearchBackendKind.MemoryCache, $"Expected MemoryCache, got {r2.Backend}");
                Check(cachedResults.Count == 1);

                // Deferred node_modules folder items must still be found when searched
                var nmResults = new List<SearchResultEntry>();
                service.SearchAsync(
                    "nm_target",
                    root,
                    includeHidden: false,
                    maxResults: 100,
                    onBatch: (items, _) => { lock (nmResults) nmResults.AddRange(items); },
                    cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                Check(nmResults.Count == 1 && string.Equals(nmResults[0].FullPath, nmFile, StringComparison.OrdinalIgnoreCase),
                    "Deferred node_modules directory was skipped completely instead of deferred.");

                // Multi-keyword AND query
                var multiResults = new List<SearchResultEntry>();
                service.SearchAsync(
                    "Leaf Host Notes",
                    root,
                    includeHidden: false,
                    maxResults: 100,
                    onBatch: (items, _) => { lock (multiResults) multiResults.AddRange(items); },
                    cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                Check(multiResults.Count == 1 && string.Equals(multiResults[0].FullPath, multiTermFile, StringComparison.OrdinalIgnoreCase));

                // Hidden exclusion vs inclusion
                var hiddenOff = new List<SearchResultEntry>();
                service.SearchAsync("ghost_secret", root, includeHidden: false, 100, (items, _) => { lock (hiddenOff) hiddenOff.AddRange(items); }, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Check(hiddenOff.Count == 0, "Hidden file leaked when includeHidden=false.");

                var hiddenOn = new List<SearchResultEntry>();
                service.SearchAsync("ghost_secret", root, includeHidden: true, 100, (items, _) => { lock (hiddenOn) hiddenOn.AddRange(items); }, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Check(hiddenOn.Count == 1 && string.Equals(hiddenOn[0].FullPath, hiddenFile, StringComparison.OrdinalIgnoreCase));

                // Cache invalidation allows newly created files to appear
                string freshFile = Path.Combine(srcDir, "newly_created_after_cache.txt");
                File.WriteAllText(freshFile, "new");
                service.InvalidateCache();
                var freshHits = new List<SearchResultEntry>();
                SearchExecutionReport rFresh = service.SearchAsync(
                    "newly_created_after_cache",
                    root,
                    includeHidden: false,
                    maxResults: 100,
                    onBatch: (items, _) => { lock (freshHits) freshHits.AddRange(items); },
                    cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                Check(rFresh.Backend == SearchBackendKind.NativeFastScan && freshHits.Count == 1);
            });

            Test("Win32 fast scanner delivers every batch in dense directories and matches cached counts", () =>
            {
                string denseDir = MakeDirectory("win32-dense-1000");
                for (int i = 0; i < 1000; i++)
                {
                    File.WriteAllText(Path.Combine(denseDir, $"dense_hit_{i:D4}.txt"), string.Empty);
                }

                using var disabledIpc = new EverythingIpcClient("LeafTabDisabledIpc_" + Guid.NewGuid().ToString("N"));
                var service = new FastFileSearchService(disabledIpc);

                var delivered1000 = new List<SearchResultEntry>();
                var batchIndices = new List<int>();
                SearchExecutionReport rep1000 = service.SearchAsync(
                    "dense_hit_",
                    denseDir,
                    includeHidden: false,
                    maxResults: 400,
                    onBatch: (items, idx) =>
                    {
                        lock (delivered1000)
                        {
                            delivered1000.AddRange(items);
                            batchIndices.Add(idx);
                        }
                    },
                    cancellationToken: CancellationToken.None).GetAwaiter().GetResult();

                Check(rep1000.Backend == SearchBackendKind.NativeFastScan, $"Expected NativeFastScan, got {rep1000.Backend}");
                Check(rep1000.MatchedCount == 400, $"Expected MatchedCount=400, got {rep1000.MatchedCount}");
                Check(delivered1000.Count == 400, $"Expected 400 delivered items (no overwritten batches), got {delivered1000.Count}");
                Check(rep1000.IsTruncated, "Expected IsTruncated=true when 1,000 files match a 400-item cap.");
                for (int i = 0; i < batchIndices.Count; i++)
                {
                    Check(batchIndices[i] == i, $"Non-sequential batch index at {i}: got {batchIndices[i]}");
                }

                var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (SearchResultEntry e in delivered1000)
                    uniquePaths.Add(e.FullPath);
                Check(uniquePaths.Count == 400, $"Expected 400 distinct paths, got {uniquePaths.Count}");

                // 200-file cold vs warm parity check (previously 152 cold vs 200 cached)
                string mediumDir = MakeDirectory("win32-dense-200");
                for (int i = 0; i < 200; i++)
                {
                    File.WriteAllText(Path.Combine(mediumDir, $"pair_hit_{i:D4}.log"), string.Empty);
                }

                var cold200 = new List<SearchResultEntry>();
                SearchExecutionReport repCold200 = service.SearchAsync(
                    "pair_hit_",
                    mediumDir,
                    includeHidden: false,
                    maxResults: 400,
                    onBatch: (items, _) => { lock (cold200) cold200.AddRange(items); },
                    cancellationToken: CancellationToken.None).GetAwaiter().GetResult();

                var warm200 = new List<SearchResultEntry>();
                SearchExecutionReport repWarm200 = service.SearchAsync(
                    "pair_hit_",
                    mediumDir,
                    includeHidden: false,
                    maxResults: 400,
                    onBatch: (items, _) => { lock (warm200) warm200.AddRange(items); },
                    cancellationToken: CancellationToken.None).GetAwaiter().GetResult();

                Check(repCold200.Backend == SearchBackendKind.NativeFastScan && cold200.Count == 200 && !repCold200.IsTruncated,
                    $"Cold 200-file search mismatch: backend={repCold200.Backend}, count={cold200.Count}, truncated={repCold200.IsTruncated}");
                Check(repWarm200.Backend == SearchBackendKind.MemoryCache && warm200.Count == 200 && !repWarm200.IsTruncated,
                    $"Warm 200-file search mismatch: backend={repWarm200.Backend}, count={warm200.Count}, truncated={repWarm200.IsTruncated}");
            });

            Test("Everything IPC paginates past filtered hidden results and flags truncation when paging stops early", () =>
            {
                string indexedDir = MakeDirectory("ev-paged");
                string hiddenStub = Path.Combine(indexedDir, "paged_hidden_stub.txt");
                string visibleTail = Path.Combine(indexedDir, "paged_visible_tail.txt");
                File.WriteAllText(hiddenStub, "hidden");
                File.SetAttributes(hiddenStub, FileAttributes.Hidden);
                File.WriteAllText(visibleTail, "visible");

                string mockClass = "LeafTabMockEverythingPaged_" + Guid.NewGuid().ToString("N");
                using var server = new MockEverythingIpcServer(mockClass, indexedDir, visibleTail, hiddenStub, visibleTail, indexedDir);
                using var client = new EverythingIpcClient(mockClass);
                var service = new FastFileSearchService(client);

                // 1,200 hidden items followed by 1 visible item with maxResults=400 (initial fetch window = 1,200)
                var dataset = new (uint Flags, string Path, ulong Size)[1201];
                for (int i = 0; i < 1200; i++)
                    dataset[i] = (0u, hiddenStub, 16UL);
                dataset[1200] = (0u, visibleTail, 512UL);

                server.Mode = MockEverythingMode.PagedDataset;
                server.PagedItems = dataset;
                server.MaxReplySlice = 0; // honor caller's requested maxResults

                var hits = new List<SearchResultEntry>();
                SearchExecutionReport rep = service.SearchAsync(
                    "paged_",
                    indexedDir,
                    includeHidden: false,
                    maxResults: 400,
                    onBatch: (items, _) => { lock (hits) hits.AddRange(items); },
                    cancellationToken: CancellationToken.None).GetAwaiter().GetResult();

                Check(rep.Backend == SearchBackendKind.Everything, $"Expected Everything backend, got {rep.Backend}");
                Check(server.QueryCount >= 2, $"Expected multi-page IPC queries, got QueryCount={server.QueryCount}");
                Check(hits.Count == 1 && string.Equals(hits[0].FullPath, visibleTail, StringComparison.OrdinalIgnoreCase),
                    $"Expected visible tail after 1,200 hidden items, got count={hits.Count}");
                Check(!rep.IsTruncated, "All pages were exhausted; IsTruncated should be false.");

                // Exceeding MaxPages (24 pages * 50 items/page < 2,000 hidden items) must report IsTruncated=true
                var hugeHidden = new (uint Flags, string Path, ulong Size)[2000];
                for (int i = 0; i < hugeHidden.Length; i++)
                    hugeHidden[i] = (0u, hiddenStub, 16UL);

                server.PagedItems = hugeHidden;
                server.MaxReplySlice = 50;
                server.ResetQueryCount();

                var emptyHits = new List<SearchResultEntry>();
                SearchExecutionReport repTrunc = service.SearchAsync(
                    "paged_",
                    indexedDir,
                    includeHidden: false,
                    maxResults: 400,
                    onBatch: (items, _) => { lock (emptyHits) emptyHits.AddRange(items); },
                    cancellationToken: CancellationToken.None).GetAwaiter().GetResult();

                Check(repTrunc.Backend == SearchBackendKind.Everything && emptyHits.Count == 0 && repTrunc.IsTruncated,
                    $"Expected IsTruncated=true when page budget stops before TotalItems, got truncated={repTrunc.IsTruncated}");
            });

            Test("Search RAM cache detects directory writes automatically and enforces strict non-sliding TTL", () =>
            {
                string root = MakeDirectory("win32-cache-freshness");
                string subDir = Path.Combine(root, "sub");
                Directory.CreateDirectory(subDir);
                File.WriteAllText(Path.Combine(subDir, "seed_alpha.txt"), "seed");

                using var disabledIpc = new EverythingIpcClient("LeafTabDisabledIpc_" + Guid.NewGuid().ToString("N"));
                var service = new FastFileSearchService(disabledIpc);

                // Warm the cache
                var seedHits = new List<SearchResultEntry>();
                SearchExecutionReport r1 = service.SearchAsync("seed_", root, false, 100, (items, _) => { lock (seedHits) seedHits.AddRange(items); }, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Check(r1.Backend == SearchBackendKind.NativeFastScan && seedHits.Count == 1);

                SearchExecutionReport r2 = service.SearchAsync("seed_", root, false, 100, (_, _) => { }, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Check(r2.Backend == SearchBackendKind.MemoryCache, "Expected warm cache hit before directory change.");

                // 1. Create a new file in subDir WITHOUT calling InvalidateCache(): directory write-time stamp must invalidate snapshot
                Thread.Sleep(25);
                string addedPath = Path.Combine(subDir, "added_without_manual_refresh.txt");
                File.WriteAllText(addedPath, "fresh");

                var autoHits = new List<SearchResultEntry>();
                SearchExecutionReport rAuto = service.SearchAsync(
                    "added_without_manual_refresh",
                    root,
                    includeHidden: false,
                    maxResults: 100,
                    onBatch: (items, _) => { lock (autoHits) autoHits.AddRange(items); },
                    cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                Check(rAuto.Backend == SearchBackendKind.NativeFastScan && autoHits.Count == 1,
                    $"Expected automatic directory-stamp cache invalidation, got backend={rAuto.Backend}, hits={autoHits.Count}");

                // 2. Strict absolute CreatedUtc TTL: frequent cache hits must NOT extend expiration indefinitely
                service.InvalidateCache();
                service.CacheTtl = TimeSpan.FromMilliseconds(240);

                SearchExecutionReport ttlCold = service.SearchAsync("seed_", root, false, 100, (_, _) => { }, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Check(ttlCold.Backend == SearchBackendKind.NativeFastScan);

                for (int pulse = 0; pulse < 3; pulse++)
                {
                    Thread.Sleep(55);
                    service.SearchAsync("seed_", root, false, 100, (_, _) => { }, CancellationToken.None).GetAwaiter().GetResult();
                }

                // Total elapsed > 240ms since CreatedUtc, even though last hit was < 120ms ago
                Thread.Sleep(110);
                SearchExecutionReport afterTtl = service.SearchAsync("seed_", root, false, 100, (_, _) => { }, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Check(afterTtl.Backend == SearchBackendKind.NativeFastScan,
                    $"Expected absolute TTL expiration to trigger NativeFastScan despite frequent hits, got {afterTtl.Backend}");
            });

            Test("Large directory tree (>64 subfolders) invalidates cache across all subfolders and abandons incomplete tracking", () =>
            {
                string root = MakeDirectory("win32-cache-100-subdirs");
                var subDirs = new string[100];
                for (int i = 0; i < subDirs.Length; i++)
                {
                    subDirs[i] = Path.Combine(root, $"sub_{i:D3}");
                    Directory.CreateDirectory(subDirs[i]);
                    File.WriteAllText(Path.Combine(subDirs[i], $"seed_{i:D3}.txt"), "seed");
                }

                using var disabledIpc = new EverythingIpcClient("LeafTabDisabledIpc_" + Guid.NewGuid().ToString("N"));
                var service = new FastFileSearchService(disabledIpc);

                // 1. Warm cache over 100 subdirectories (> old 64 limit) and verify MemoryCache hit
                SearchExecutionReport warm1 = service.SearchAsync("seed_099", root, false, 100, (_, _) => { }, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Check(warm1.Backend == SearchBackendKind.NativeFastScan && warm1.MatchedCount == 1);

                SearchExecutionReport warm2 = service.SearchAsync("seed_099", root, false, 100, (_, _) => { }, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Check(warm2.Backend == SearchBackendKind.MemoryCache && warm2.MatchedCount == 1,
                    $"Expected MemoryCache hit for 100-subfolder tree, got {warm2.Backend}");

                // 2. Add a file inside subfolder #95 WITHOUT clearing cache: must immediately invalidate and find 1 item
                Thread.Sleep(25);
                File.WriteAllText(Path.Combine(subDirs[95], "deep_untracked_probe.txt"), "new");

                var deepHits = new List<SearchResultEntry>();
                SearchExecutionReport afterDeepWrite = service.SearchAsync(
                    "deep_untracked_probe",
                    root,
                    includeHidden: false,
                    maxResults: 100,
                    onBatch: (items, _) => { lock (deepHits) deepHits.AddRange(items); },
                    cancellationToken: CancellationToken.None).GetAwaiter().GetResult();

                Check(afterDeepWrite.Backend == SearchBackendKind.NativeFastScan && deepHits.Count == 1,
                    $"Expected immediate detection in subfolder #95, got backend={afterDeepWrite.Backend}, hits={deepHits.Count}");

                // 3. When directory count exceeds MaxTrackedCacheDirectories, snapshot must be abandoned rather than partially tracked
                service.InvalidateCache();
                service.MaxTrackedCacheDirectories = 12;

                SearchExecutionReport overCap1 = service.SearchAsync("seed_001", root, false, 100, (_, _) => { }, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Check(overCap1.Backend == SearchBackendKind.NativeFastScan && overCap1.MatchedCount == 1);

                Thread.Sleep(25);
                File.WriteAllText(Path.Combine(subDirs[98], "over_cap_new_file.txt"), "new");

                var overCapHits = new List<SearchResultEntry>();
                SearchExecutionReport overCap2 = service.SearchAsync(
                    "over_cap_new_file",
                    root,
                    includeHidden: false,
                    maxResults: 100,
                    onBatch: (items, _) => { lock (overCapHits) overCapHits.AddRange(items); },
                    cancellationToken: CancellationToken.None).GetAwaiter().GetResult();

                Check(overCap2.Backend == SearchBackendKind.NativeFastScan && overCapHits.Count == 1,
                    $"Over-cap directory tree served stale partial cache: backend={overCap2.Backend}, hits={overCapHits.Count}");
            });

            Test("Concurrent cold Everything IPC queries create at most one reply window/thread and leave zero leaked HWNDs on Dispose", () =>
            {
                static int CountProcessReplyMessageWindows()
                {
                    uint currentPid = unchecked((uint)Process.GetCurrentProcess().Id);
                    int count = 0;
                    IntPtr child = IntPtr.Zero;
                    while (true)
                    {
                        child = NativeMethods.FindWindowExW(
                            NativeMethods.HWND_MESSAGE,
                            child,
                            EverythingIpcClient.ReplyWindowClassName,
                            null);
                        if (child == IntPtr.Zero)
                            break;

                        NativeMethods.GetWindowThreadProcessId(child, out uint winPid);
                        if (winPid == currentPid && NativeMethods.IsWindow(child))
                            count++;
                    }
                    return count;
                }

                string indexedDir = MakeDirectory("concurrent-ipc-cold");
                string matchFile = Path.Combine(indexedDir, "concurrent_target.txt");
                string hiddenFile = Path.Combine(indexedDir, "hidden.txt");
                string outsideFile = Path.Combine(MakeDirectory("concurrent-ipc-outside"), "outside.txt");
                string subDir = Path.Combine(indexedDir, "sub");
                Directory.CreateDirectory(subDir);
                File.WriteAllText(matchFile, "ok");
                File.WriteAllText(hiddenFile, "h");
                File.WriteAllText(outsideFile, "o");

                string mockClass = "LeafTabMockEverythingConcurrent_" + Guid.NewGuid().ToString("N");
                using var server = new MockEverythingIpcServer(mockClass, indexedDir, matchFile, hiddenFile, outsideFile, subDir);

                int baselineWindows = CountProcessReplyMessageWindows();
                var client = new EverythingIpcClient(mockClass);
                try
                {
                    const int concurrency = 12;
                    using var readyBarrier = new CountdownEvent(concurrency);
                    using var startGate = new ManualResetEventSlim(false);
                    var tasks = new Task<EverythingQueryResponse>[concurrency];

                    for (int i = 0; i < concurrency; i++)
                    {
                        tasks[i] = Task.Run(async () =>
                        {
                            readyBarrier.Signal();
                            startGate.Wait(3000);
                            return await client.QueryRawAsync(
                                $"\"{indexedDir}\\\" concurrent",
                                maxResults: 50,
                                matchPath: true,
                                timeoutMs: 2000,
                                cancellationToken: CancellationToken.None).ConfigureAwait(false);
                        });
                    }

                    Check(readyBarrier.Wait(3000), "Concurrent IPC worker threads did not reach start barrier.");
                    startGate.Set();
                    Task.WaitAll(tasks);

                    for (int i = 0; i < concurrency; i++)
                    {
                        EverythingQueryResponse resp = tasks[i].Result;
                        Check(resp.IpcAvailable && !resp.TimedOut && resp.Items.Count > 0,
                            $"Concurrent cold IPC query #{i} failed (Available={resp.IpcAvailable}, TimedOut={resp.TimedOut}, Items={resp.Items.Count}).");
                    }

                    int activeDuringClient = CountProcessReplyMessageWindows();
                    Check(client.CreatedReplyThreadCount == 1,
                        $"Expected exactly 1 background reply thread, created {client.CreatedReplyThreadCount}.");
                    Check(client.TrackedReplyWindowCount == 1,
                        $"Expected 1 tracked reply window, got {client.TrackedReplyWindowCount}.");
                    Check(activeDuringClient == baselineWindows + 1,
                        $"Expected OS HWND_MESSAGE count {baselineWindows + 1}, got {activeDuringClient}.");
                }
                finally
                {
                    client.Dispose();
                }

                int afterDispose = CountProcessReplyMessageWindows();
                Check(client.TrackedReplyWindowCount == 0,
                    $"Expected 0 tracked reply windows after Dispose, got {client.TrackedReplyWindowCount}.");
                Check(afterDispose == baselineWindows,
                    $"Leaked {afterDispose - baselineWindows} hidden LeafTabEverythingReplyWnd HWND_MESSAGE window(s) after Dispose.");
            });

            Test("Search RAM cache re-verifies file size, hidden attributes, ancestor hidden folders, and deletions", () =>
            {
                string root = MakeDirectory("win32-cache-file-meta");
                string sub = Path.Combine(root, "sub");
                Directory.CreateDirectory(sub);

                string growFile = Path.Combine(sub, "grow_target.bin");
                string toggleVisFile = Path.Combine(sub, "toggle_vis.txt");
                string initiallyHiddenFile = Path.Combine(sub, "initially_hidden.txt");

                File.WriteAllBytes(growFile, new byte[] { 0x41 }); // 1 byte
                File.WriteAllText(toggleVisFile, "visible");
                File.WriteAllText(initiallyHiddenFile, "secret");
                File.SetAttributes(initiallyHiddenFile, FileAttributes.Hidden);

                using var disabledIpc = new EverythingIpcClient("LeafTabDisabledIpc_" + Guid.NewGuid().ToString("N"));
                var service = new FastFileSearchService(disabledIpc);

                // 1. Cold scan caches initial 1-byte size
                var coldHits = new List<SearchResultEntry>();
                SearchExecutionReport rCold = service.SearchAsync(
                    "grow_target",
                    root,
                    includeHidden: false,
                    maxResults: 50,
                    onBatch: (items, _) => { lock (coldHits) coldHits.AddRange(items); },
                    cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                Check(rCold.Backend == SearchBackendKind.NativeFastScan && coldHits.Count == 1 && coldHits[0].FileSize == 1UL);

                // 2. Grow file in-place from 1 byte to 8192 bytes (without adding/removing directory entries)
                using (var fs = new FileStream(growFile, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                {
                    var payload = new byte[8192];
                    payload[0] = 0x5A;
                    fs.Write(payload, 0, payload.Length);
                    fs.Flush();
                }

                var warmGrowHits = new List<SearchResultEntry>();
                SearchExecutionReport rGrow = service.SearchAsync(
                    "grow_target",
                    root,
                    includeHidden: false,
                    maxResults: 50,
                    onBatch: (items, _) => { lock (warmGrowHits) warmGrowHits.AddRange(items); },
                    cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                Check(rGrow.Backend == SearchBackendKind.MemoryCache, $"Expected MemoryCache hit, got {rGrow.Backend}");
                Check(warmGrowHits.Count == 1 && warmGrowHits[0].FileSize == 8192UL,
                    $"Expected live FileSize=8192 from MemoryCache, got {warmGrowHits.FirstOrDefault().FileSize}");

                var sizeFilterHits = new List<SearchResultEntry>();
                SearchExecutionReport rSize = service.SearchAsync(
                    "size:>4kb grow_target",
                    root,
                    includeHidden: false,
                    maxResults: 50,
                    onBatch: (items, _) => { lock (sizeFilterHits) sizeFilterHits.AddRange(items); },
                    cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                Check(rSize.Backend == SearchBackendKind.MemoryCache && sizeFilterHits.Count == 1,
                    $"Expected size:>4kb filter to match updated 8192B file in MemoryCache, got count={sizeFilterHits.Count}");

                // 3. Flipping Visible -> Hidden immediately excludes item from cached visible search
                File.SetAttributes(toggleVisFile, FileAttributes.Hidden);
                var hiddenExcluded = new List<SearchResultEntry>();
                SearchExecutionReport rHiddenExcl = service.SearchAsync(
                    "toggle_vis",
                    root,
                    includeHidden: false,
                    maxResults: 50,
                    onBatch: (items, _) => { lock (hiddenExcluded) hiddenExcluded.AddRange(items); },
                    cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                Check(rHiddenExcl.Backend == SearchBackendKind.MemoryCache && hiddenExcluded.Count == 0,
                    $"Expected newly hidden file to be excluded immediately, got count={hiddenExcluded.Count}");

                // 4. Flipping Hidden -> Visible immediately surfaces item in cached visible search
                File.SetAttributes(initiallyHiddenFile, FileAttributes.Normal);
                var unhiddenHits = new List<SearchResultEntry>();
                SearchExecutionReport rUnhidden = service.SearchAsync(
                    "initially_hidden",
                    root,
                    includeHidden: false,
                    maxResults: 50,
                    onBatch: (items, _) => { lock (unhiddenHits) unhiddenHits.AddRange(items); },
                    cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                Check(unhiddenHits.Count == 1, $"Expected un-hidden file to appear immediately, got {unhiddenHits.Count}");

                // 5. Hiding ancestor subfolder excludes descendants when includeHidden=false
                File.SetAttributes(sub, FileAttributes.Directory | FileAttributes.Hidden);
                try
                {
                    var ancestorHiddenHits = new List<SearchResultEntry>();
                    service.SearchAsync(
                        "grow_target",
                        root,
                        includeHidden: false,
                        maxResults: 50,
                        onBatch: (items, _) => { lock (ancestorHiddenHits) ancestorHiddenHits.AddRange(items); },
                        cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                    Check(ancestorHiddenHits.Count == 0,
                        $"Expected child inside hidden subfolder to be excluded, got {ancestorHiddenHits.Count}");
                }
                finally
                {
                    File.SetAttributes(sub, FileAttributes.Directory);
                }
            });

            Test("Native search supports ext:, size:, dm:, type:, folder:, regex:, OR tokens and warns on Everything-only syntax", () =>
            {
                string root = MakeDirectory("win32-native-syntax");
                string subDocs = Path.Combine(root, "docs_archive");
                Directory.CreateDirectory(subDocs);

                string bigTodayTxt = Path.Combine(root, "report_2026_01.txt");
                string smallOldTxt = Path.Combine(subDocs, "legacy_notes.txt");
                string codeFile = Path.Combine(root, "EngineCore.cs");
                string mdFile = Path.Combine(root, "CHANGELOG.md");

                File.WriteAllBytes(bigTodayTxt, new byte[6144]);
                File.SetLastWriteTime(bigTodayTxt, DateTime.Now);

                File.WriteAllBytes(smallOldTxt, new byte[256]);
                File.SetLastWriteTime(smallOldTxt, new DateTime(2021, 6, 15, 10, 30, 0, DateTimeKind.Local));

                File.WriteAllText(codeFile, "class EngineCore {}");
                File.WriteAllText(mdFile, "# Changelog");

                using var disabledIpc = new EverythingIpcClient("LeafTabDisabledIpc_" + Guid.NewGuid().ToString("N"));
                var service = new FastFileSearchService(disabledIpc);

                List<SearchResultEntry> RunQuery(string q, out SearchExecutionReport report)
                {
                    var list = new List<SearchResultEntry>();
                    report = service.SearchAsync(
                        q,
                        root,
                        includeHidden: false,
                        maxResults: 100,
                        onBatch: (items, _) => { lock (list) list.AddRange(items); },
                        cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                    return list;
                }

                // Cold scan (NativeFastScan) + Warm scan (MemoryCache) parity on rich filter queries
                var extCold = RunQuery("ext:txt", out SearchExecutionReport repExtCold);
                var extWarm = RunQuery("ext:txt", out SearchExecutionReport repExtWarm);
                Check(repExtCold.Backend == SearchBackendKind.NativeFastScan && extCold.Count == 2,
                    $"Expected 2 .txt files on cold ext:txt, got {extCold.Count}");
                Check(repExtWarm.Backend == SearchBackendKind.MemoryCache && extWarm.Count == 2,
                    $"Expected 2 .txt files on warm ext:txt, got {extWarm.Count}");

                Check(RunQuery("ext:cs;md", out _).Count == 2, "Expected ext:cs;md to match 2 files.");
                Check(RunQuery("ext:txt size:>4kb", out _).Single().FullPath.EndsWith("report_2026_01.txt", StringComparison.OrdinalIgnoreCase));
                Check(RunQuery("ext:txt size:100b..1kb", out _).Single().FullPath.EndsWith("legacy_notes.txt", StringComparison.OrdinalIgnoreCase));
                Check(RunQuery("ext:txt dm:today", out _).Single().FullPath.EndsWith("report_2026_01.txt", StringComparison.OrdinalIgnoreCase));
                Check(RunQuery("ext:txt dm:2021", out _).Single().FullPath.EndsWith("legacy_notes.txt", StringComparison.OrdinalIgnoreCase));
                Check(RunQuery("type:code", out _).Single().FullPath.EndsWith("EngineCore.cs", StringComparison.OrdinalIgnoreCase));
                Check(RunQuery("folder: docs", out _).Single(e => (e.FileAttributes & (uint)FileAttributes.Directory) != 0).FullPath.EndsWith("docs_archive", StringComparison.OrdinalIgnoreCase));
                Check(RunQuery(@"regex:^report_\d{4}_\d{2}\.txt$", out _).Single().FullPath.EndsWith("report_2026_01.txt", StringComparison.OrdinalIgnoreCase));
                Check(RunQuery("CHANGELOG.md | EngineCore.cs", out _).Count == 2, "Expected '|' OR query to match 2 files.");
                Check(FastFileSearchService.BuildEntryMatcher("C:")("本地磁盘 (C:)", "本地磁盘 (C:)"),
                    "Expected Windows drive token 'C:' not to be treated as an unknown filter operator.");

                // Everything-only operator when Everything IPC is unavailable must return an explicit Chinese notice
                var unsupported = RunQuery("content:secret_token", out SearchExecutionReport repUnsupported);
                Check(unsupported.Count == 0 && !string.IsNullOrWhiteSpace(repUnsupported.NoticeMessage) && repUnsupported.NoticeMessage!.Contains("Everything"),
                    $"Expected explicit Everything notice for content:, got '{repUnsupported.NoticeMessage}'");
            });

            Test("Disjunctive OR combines size:, ext:, folder:, and regex: filters in any order across cold and cached searches", () =>
            {
                string root = MakeDirectory("win32-or-composite-filters");
                string subFolder = Path.Combine(root, "target_dir");
                Directory.CreateDirectory(subFolder);

                string smallTxt = Path.Combine(root, "small.txt");
                string largeBin = Path.Combine(root, "large.bin");
                string tinyBin = Path.Combine(root, "tiny.bin");

                File.WriteAllBytes(smallTxt, new byte[12]);
                File.WriteAllBytes(largeBin, new byte[8192]);
                File.WriteAllBytes(tinyBin, new byte[12]);

                using var disabledIpc = new EverythingIpcClient("LeafTabDisabledIpc_" + Guid.NewGuid().ToString("N"));
                var service = new FastFileSearchService(disabledIpc);

                List<SearchResultEntry> RunOr(string q, out SearchExecutionReport rep)
                {
                    var list = new List<SearchResultEntry>();
                    rep = service.SearchAsync(
                        q,
                        root,
                        includeHidden: false,
                        maxResults: 100,
                        onBatch: (items, _) => { lock (list) list.AddRange(items); },
                        cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                    return list;
                }

                // Cold scan: size:>4kb | ext:txt -> must return both small.txt and large.bin
                var cold1 = RunOr("size:>4kb | ext:txt", out SearchExecutionReport rCold1);
                Check(rCold1.Backend == SearchBackendKind.NativeFastScan && cold1.Count == 2,
                    $"Expected 2 matches for cold 'size:>4kb | ext:txt', got {cold1.Count}");
                Check(cold1.Any(e => e.FullPath.EndsWith("small.txt", StringComparison.OrdinalIgnoreCase)) &&
                      cold1.Any(e => e.FullPath.EndsWith("large.bin", StringComparison.OrdinalIgnoreCase)));

                // Warm cache: size:>4kb | ext:txt and reversed ext:txt | size:>4kb and unspaced size:>4kb|ext:txt
                var warm1 = RunOr("size:>4kb | ext:txt", out SearchExecutionReport rWarm1);
                Check(rWarm1.Backend == SearchBackendKind.MemoryCache && warm1.Count == 2,
                    $"Expected 2 matches for cached 'size:>4kb | ext:txt', got {warm1.Count}");

                var warmReversed = RunOr("ext:txt | size:>4kb", out SearchExecutionReport rWarmRev);
                Check(rWarmRev.Backend == SearchBackendKind.MemoryCache && warmReversed.Count == 2,
                    $"Expected 2 matches for reversed 'ext:txt | size:>4kb', got {warmReversed.Count}");

                var warmUnspaced = RunOr("size:>4kb|ext:txt", out _);
                Check(warmUnspaced.Count == 2, $"Expected 2 matches for unspaced 'size:>4kb|ext:txt', got {warmUnspaced.Count}");

                // Cold scan with reversed order as well
                service.InvalidateCache();
                var coldReversed = RunOr("ext:txt | size:>4kb", out SearchExecutionReport rColdRev);
                Check(rColdRev.Backend == SearchBackendKind.NativeFastScan && coldReversed.Count == 2,
                    $"Expected 2 matches for cold reversed 'ext:txt | size:>4kb', got {coldReversed.Count}");

                // Directory OR file size filter
                var dirOrSize = RunOr("folder:target_dir | size:>4kb", out _);
                Check(dirOrSize.Count == 2 &&
                      dirOrSize.Any(e => e.FullPath.EndsWith("target_dir", StringComparison.OrdinalIgnoreCase)) &&
                      dirOrSize.Any(e => e.FullPath.EndsWith("large.bin", StringComparison.OrdinalIgnoreCase)),
                    $"Expected folder + large.bin for 'folder:target_dir | size:>4kb', got {dirOrSize.Count}");
            });

            Test("Regex filter with backslash escapes matches filenames in nested subdirectories and supports path:regex:", () =>
            {
                string root = MakeDirectory("win32-regex-subdir-escapes");
                string nested = Path.Combine(root, "2026", "monthly");
                Directory.CreateDirectory(nested);

                string nestedReport = Path.Combine(nested, "report_128.txt");
                string nestedAlpha = Path.Combine(nested, "alpha_07.log");
                string nonMatching = Path.Combine(nested, "prefix_report_128.txt");
                File.WriteAllText(nestedReport, "ok");
                File.WriteAllText(nestedAlpha, "log");
                File.WriteAllText(nonMatching, "no");

                using var disabledIpc = new EverythingIpcClient("LeafTabDisabledIpc_" + Guid.NewGuid().ToString("N"));
                var service = new FastFileSearchService(disabledIpc);

                List<SearchResultEntry> RunRx(string q, out SearchExecutionReport rep)
                {
                    var list = new List<SearchResultEntry>();
                    rep = service.SearchAsync(
                        q,
                        root,
                        includeHidden: false,
                        maxResults: 100,
                        onBatch: (items, _) => { lock (list) list.AddRange(items); },
                        cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                    return list;
                }

                // Cold recursive scan from ancestor root with \d and \. escapes + ^ anchor
                var coldHits = RunRx(@"regex:^report_\d+\.txt$", out SearchExecutionReport rCold);
                Check(rCold.Backend == SearchBackendKind.NativeFastScan && coldHits.Count == 1 &&
                      coldHits[0].FullPath.EndsWith("report_128.txt", StringComparison.OrdinalIgnoreCase),
                    $"Expected recursive regex:^report_\\d+\\.txt$ to match nested report_128.txt on cold scan, got {coldHits.Count}");

                // Warm cached recursive lookup
                var warmHits = RunRx(@"regex:^report_\d+\.txt$", out SearchExecutionReport rWarm);
                Check(rWarm.Backend == SearchBackendKind.MemoryCache && warmHits.Count == 1 &&
                      warmHits[0].FullPath.EndsWith("report_128.txt", StringComparison.OrdinalIgnoreCase),
                    $"Expected recursive regex:^report_\\d+\\.txt$ to match nested report_128.txt in MemoryCache, got {warmHits.Count}");

                // Regex internal alternation (alpha|beta) inside nested subdirectory
                var altHits = RunRx(@"regex:^(alpha|beta)_\d+\.log$", out _);
                Check(altHits.Count == 1 && altHits[0].FullPath.EndsWith("alpha_07.log", StringComparison.OrdinalIgnoreCase),
                    $"Expected regex alternation in subdirectory to match alpha_07.log, got {altHits.Count}");

                // Explicit path:regex: matches relative path from root
                var pathRxHits = RunRx(@"path:regex:^2026\\monthly\\report_\d+\.txt$", out _);
                Check(pathRxHits.Count == 1 && pathRxHits[0].FullPath.EndsWith("report_128.txt", StringComparison.OrdinalIgnoreCase),
                    $"Expected path:regex: to match relative path, got {pathRxHits.Count}");
            });

            Test("Un-hiding a previously skipped hidden subdirectory invalidates search RAM cache without manual refresh", () =>
            {
                string root = MakeDirectory("win32-unhide-subdir-cache");
                string hiddenSub = Path.Combine(root, "vault_hidden");
                Directory.CreateDirectory(hiddenSub);
                string targetFile = Path.Combine(hiddenSub, "unlocked_item.txt");
                File.WriteAllText(targetFile, "revealed");
                File.SetAttributes(hiddenSub, FileAttributes.Directory | FileAttributes.Hidden);

                DateTime fixedParentWriteUtc = new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);
                Directory.SetLastWriteTimeUtc(root, fixedParentWriteUtc);

                using var disabledIpc = new EverythingIpcClient("LeafTabDisabledIpc_" + Guid.NewGuid().ToString("N"));
                var service = new FastFileSearchService(disabledIpc);

                try
                {
                    // 1. Initial scan while hiddenSub is Hidden -> skipped, 0 results, snapshot cached
                    var initialHits = new List<SearchResultEntry>();
                    SearchExecutionReport r1 = service.SearchAsync(
                        "unlocked_item",
                        root,
                        includeHidden: false,
                        maxResults: 50,
                        onBatch: (items, _) => { lock (initialHits) initialHits.AddRange(items); },
                        cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                    Check(r1.Backend == SearchBackendKind.NativeFastScan && initialHits.Count == 0,
                        $"Expected 0 visible matches while directory is hidden, got {initialHits.Count}");

                    // Confirm snapshot IS active in RAM cache before un-hiding
                    var cachedWhileHidden = new List<SearchResultEntry>();
                    SearchExecutionReport rCached = service.SearchAsync(
                        "unlocked_item",
                        root,
                        includeHidden: false,
                        maxResults: 50,
                        onBatch: (items, _) => { lock (cachedWhileHidden) cachedWhileHidden.AddRange(items); },
                        cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                    Check(rCached.Backend == SearchBackendKind.MemoryCache && cachedWhileHidden.Count == 0,
                        $"Expected MemoryCache hit while directory remains hidden, got {rCached.Backend}");

                    // 2. Un-hide subdirectory without modifying parent directory's LastWriteTime
                    File.SetAttributes(hiddenSub, FileAttributes.Directory);
                    Check(Directory.GetLastWriteTimeUtc(root) == fixedParentWriteUtc,
                        "Expected parent directory LastWriteTimeUtc to remain unchanged when child attribute flips.");

                    // 3. Immediate search (no InvalidateCache) must detect visibility change and return unlocked_item.txt
                    var revealedHits = new List<SearchResultEntry>();
                    SearchExecutionReport rRevealed = service.SearchAsync(
                        "unlocked_item",
                        root,
                        includeHidden: false,
                        maxResults: 50,
                        onBatch: (items, _) => { lock (revealedHits) revealedHits.AddRange(items); },
                        cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                    Check(revealedHits.Count == 1 &&
                          revealedHits[0].FullPath.EndsWith("unlocked_item.txt", StringComparison.OrdinalIgnoreCase),
                        $"Expected un-hidden directory contents to be discovered immediately without clearing cache, got {revealedHits.Count}");
                }
                finally
                {
                    if (Directory.Exists(hiddenSub))
                    {
                        File.SetAttributes(hiddenSub, FileAttributes.Directory);
                    }
                }
            });
        }

        private enum MockEverythingMode
        {
            NormalV2,
            UnindexedZero,
            LegacyV1Only,
            PagedDataset
        }

        private sealed class MockEverythingIpcServer : IDisposable
        {
            private readonly string _className;
            private readonly string _indexedDir;
            private readonly string _matchFile;
            private readonly string _hiddenFile;
            private readonly string _outsideFile;
            private readonly string _matchSubdir;
            private readonly WNDPROC _wndProcDelegate;
            private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);
            private readonly Thread _thread;
            private IntPtr _hwnd;
            private bool _disposed;

            public MockEverythingMode Mode { get; set; } = MockEverythingMode.NormalV2;
            public bool SawValidV2Header { get; private set; }
            public (uint Flags, string Path, ulong Size)[]? PagedItems { get; set; }
            public int MaxReplySlice { get; set; }
            private int _queryCount;
            public int QueryCount => Volatile.Read(ref _queryCount);
            public void ResetQueryCount() => Interlocked.Exchange(ref _queryCount, 0);

            [DllImport("user32.dll")]
            private static extern void PostQuitMessage(int nExitCode);

            public MockEverythingIpcServer(
                string className,
                string indexedDir,
                string matchFile,
                string hiddenFile,
                string outsideFile,
                string matchSubdir)
            {
                _className = className;
                _indexedDir = indexedDir;
                _matchFile = matchFile;
                _hiddenFile = hiddenFile;
                _outsideFile = outsideFile;
                _matchSubdir = matchSubdir;
                _wndProcDelegate = WndProc;

                _thread = new Thread(RunLoop)
                {
                    IsBackground = true,
                    Name = "LeafTab.MockEverythingServer"
                };
                _thread.Start();
                _ready.Wait(3000);
            }

            private void RunLoop()
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                    hInstance = NativeMethods.GetModuleHandleW(null),
                    lpszClassName = _className
                };

                ushort atom = NativeMethods.RegisterClassExW(ref wc);
                if (atom != 0)
                {
                    // Must be a top-level hidden window (hWndParent == IntPtr.Zero) so FindWindowW locates it
                    _hwnd = NativeMethods.CreateWindowExW(
                        0,
                        _className,
                        string.Empty,
                        0,
                        0, 0, 0, 0,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        NativeMethods.GetModuleHandleW(null),
                        IntPtr.Zero);
                }
                _ready.Set();

                if (_hwnd == IntPtr.Zero)
                    return;

                while (NativeMethods.GetMessageW(out NATIVE_MSG msg, IntPtr.Zero, 0, 0) > 0)
                {
                    NativeMethods.TranslateMessage(ref msg);
                    NativeMethods.DispatchMessageW(ref msg);
                }

                NativeMethods.UnregisterClassW(_className, NativeMethods.GetModuleHandleW(null));
            }

            private IntPtr WndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam)
            {
                if (uMsg == NativeMethods.WM_COPYDATA && lParam != IntPtr.Zero)
                {
                    COPYDATASTRUCT cds = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
                    uint queryKind = unchecked((uint)cds.dwData.ToInt64());
                    byte[] req = new byte[cds.cbData];
                    Marshal.Copy(cds.lpData, req, 0, cds.cbData);

                    if (queryKind == 18 /* QUERY2W */)
                    {
                        if (Mode == MockEverythingMode.LegacyV1Only)
                            return IntPtr.Zero; // Force caller to fall back to QUERYW

                        if (req.Length < 30)
                            return IntPtr.Zero;

                        uint replyHwndVal = ReadU32(req, 0);
                        uint replyMsgId = ReadU32(req, 4);
                        uint reqOffset = ReadU32(req, 12);
                        uint reqMax = ReadU32(req, 16);
                        uint reqFlags = ReadU32(req, 20);
                        uint sortType = ReadU32(req, 24);
                        var replyHwnd = new IntPtr(replyHwndVal);
                        Interlocked.Increment(ref _queryCount);

                        if (Mode == MockEverythingMode.UnindexedZero)
                        {
                            byte[] emptyReply = BuildList2Packet(reqFlags, sortType, 0u, 0u, Array.Empty<(uint Flags, string Path, ulong Size)>());
                            SendReply(replyHwnd, replyMsgId, emptyReply);
                            return new IntPtr(1);
                        }

                        if (Mode == MockEverythingMode.PagedDataset)
                        {
                            var all = PagedItems ?? Array.Empty<(uint Flags, string Path, ulong Size)>();
                            int start = (int)Math.Min((long)all.Length, reqOffset);
                            int cap = (int)Math.Min(all.Length - start, reqMax);
                            if (MaxReplySlice > 0 && cap > MaxReplySlice)
                                cap = MaxReplySlice;

                            var slice = new (uint Flags, string Path, ulong Size)[cap];
                            if (cap > 0)
                                Array.Copy(all, start, slice, 0, cap);

                            byte[] pagedReply = BuildList2Packet(reqFlags, sortType, (uint)all.Length, reqOffset, slice);
                            SendReply(replyHwnd, replyMsgId, pagedReply);
                            return new IntPtr(1);
                        }

                        string searchStr = ReadNullTerminatedUnicode(req, 28);
                        if (NativeMethods.IsWindow(replyHwnd) &&
                            reqFlags == 0x54u &&
                            sortType == 1u &&
                            searchStr.StartsWith("\"" + _indexedDir.TrimEnd('\\') + "\\\"", StringComparison.OrdinalIgnoreCase))
                        {
                            SawValidV2Header = true;
                        }

                        var items = new[]
                        {
                            (0u, _matchFile, 12345UL),
                            (0u, _hiddenFile, 99UL),
                            (0u, _outsideFile, 77UL),
                            (1u /* EVERYTHING_IPC_FOLDER */, _matchSubdir, 0UL)
                        };
                        byte[] list2 = BuildList2Packet(reqFlags, sortType, (uint)items.Length, 0u, items);
                        SendReply(replyHwnd, replyMsgId, list2);
                        return new IntPtr(1);
                    }

                    if (queryKind == 2 /* QUERYW */ && req.Length >= 22)
                    {
                        uint replyHwndVal = ReadU32(req, 0);
                        uint replyMsgId = ReadU32(req, 4);
                        byte[] list1 = BuildList1Packet(new[]
                        {
                            (0u, Path.GetFileName(_matchFile), _indexedDir),
                            (1u, Path.GetFileName(_matchSubdir), _indexedDir)
                        });
                        SendReply(new IntPtr(replyHwndVal), replyMsgId, list1);
                        return new IntPtr(1);
                    }
                }

                if (uMsg == 0x8002u)
                {
                    NativeMethods.DestroyWindow(hWnd);
                    return IntPtr.Zero;
                }

                if (uMsg == 0x0002u /* WM_DESTROY */)
                {
                    PostQuitMessage(0);
                    return IntPtr.Zero;
                }

                return NativeMethods.DefWindowProcW(hWnd, uMsg, wParam, lParam);
            }

            private void SendReply(IntPtr replyHwnd, uint replyMsgId, byte[] payload)
            {
                GCHandle pinned = GCHandle.Alloc(payload, GCHandleType.Pinned);
                try
                {
                    var replyCds = new COPYDATASTRUCT
                    {
                        dwData = new IntPtr(replyMsgId),
                        cbData = payload.Length,
                        lpData = pinned.AddrOfPinnedObject()
                    };
                    NativeMethods.SendMessageTimeoutCopyDataW(
                        replyHwnd,
                        NativeMethods.WM_COPYDATA,
                        _hwnd,
                        ref replyCds,
                        NativeMethods.SMTO_NORMAL,
                        500,
                        out _);
                }
                finally
                {
                    pinned.Free();
                }
            }

            private static byte[] BuildList2Packet(
                uint reqFlags,
                uint sortType,
                uint totalItems,
                uint offset,
                (uint Flags, string Path, ulong Size)[] items)
            {
                using var ms = new MemoryStream();
                using var bw = new BinaryWriter(ms, Encoding.Unicode, leaveOpen: true);

                uint count = (uint)items.Length;
                int tableStart = 20;

                // Header (20 bytes)
                bw.Write(totalItems); // totitems
                bw.Write(count);      // numitems
                bw.Write(offset);     // offset
                bw.Write(reqFlags);   // request_flags
                bw.Write(sortType);   // sort_type

                // Reserve item table
                bw.Write(new byte[items.Length * 8]);

                uint[] offsets = new uint[items.Length];
                for (int i = 0; i < items.Length; i++)
                {
                    offsets[i] = (uint)ms.Position;
                    string full = items[i].Path;
                    bw.Write((uint)full.Length);
                    bw.Write(Encoding.Unicode.GetBytes(full));
                    bw.Write((ushort)0);
                    bw.Write(unchecked((long)items[i].Size));
                    bw.Write(0x22222222); // ftWrite Low
                    bw.Write(0x01DB1111); // ftWrite High
                }

                ms.Position = tableStart;
                for (int i = 0; i < items.Length; i++)
                {
                    bw.Write(items[i].Flags);
                    bw.Write(offsets[i]);
                }

                bw.Flush();
                return ms.ToArray();
            }

            private static byte[] BuildList1Packet((uint Flags, string Name, string Dir)[] items)
            {
                using var ms = new MemoryStream();
                using var bw = new BinaryWriter(ms, Encoding.Unicode, leaveOpen: true);

                uint count = (uint)items.Length;
                bw.Write(1u); // totfolders
                bw.Write(1u); // totfiles
                bw.Write(count); // totitems
                bw.Write(1u); // numfolders
                bw.Write(1u); // numfiles
                bw.Write(count); // numitems
                bw.Write(0u); // offset

                int tableStart = 28;
                bw.Write(new byte[items.Length * 12]);

                uint[] nameOffsets = new uint[items.Length];
                uint[] dirOffsets = new uint[items.Length];
                for (int i = 0; i < items.Length; i++)
                {
                    nameOffsets[i] = (uint)ms.Position;
                    bw.Write(Encoding.Unicode.GetBytes(items[i].Name));
                    bw.Write((ushort)0);

                    dirOffsets[i] = (uint)ms.Position;
                    bw.Write(Encoding.Unicode.GetBytes(items[i].Dir));
                    bw.Write((ushort)0);
                }

                ms.Position = tableStart;
                for (int i = 0; i < items.Length; i++)
                {
                    bw.Write(items[i].Flags);
                    bw.Write(nameOffsets[i]);
                    bw.Write(dirOffsets[i]);
                }

                bw.Flush();
                return ms.ToArray();
            }

            private static uint ReadU32(byte[] buf, int offset) =>
                (uint)(buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24));

            private static string ReadNullTerminatedUnicode(byte[] buf, int offset)
            {
                int end = offset;
                while (end + 1 < buf.Length && !(buf[end] == 0 && buf[end + 1] == 0))
                    end += 2;
                return end > offset ? Encoding.Unicode.GetString(buf, offset, end - offset) : string.Empty;
            }

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                if (_hwnd != IntPtr.Zero && NativeMethods.IsWindow(_hwnd))
                    NativeMethods.PostMessageW(_hwnd, 0x8002u, IntPtr.Zero, IntPtr.Zero);
                if (_thread.IsAlive)
                    _thread.Join(1000);
                _ready.Dispose();
            }
        }
    }
}
