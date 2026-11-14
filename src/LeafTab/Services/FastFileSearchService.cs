using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LeafTab.Interop;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace LeafTab.Services
{
    public enum SearchBackendKind
    {
        None = 0,
        VirtualFolder = 1,
        Everything = 2,
        NativeFastScan = 3,
        MemoryCache = 4
    }

    internal readonly struct SearchResultEntry
    {
        public string FullPath { get; }
        public uint FileAttributes { get; }
        public ulong FileSize { get; }
        public FILETIME LastWriteTime { get; }

        public SearchResultEntry(string fullPath, uint fileAttributes, ulong fileSize, FILETIME lastWriteTime)
        {
            FullPath = fullPath;
            FileAttributes = fileAttributes;
            FileSize = fileSize;
            LastWriteTime = lastWriteTime;
        }
    }

    internal sealed class SearchExecutionReport
    {
        public SearchBackendKind Backend { get; }
        public int MatchedCount { get; }
        public bool IsTruncated { get; }
        public long ElapsedMs { get; }
        public string? NoticeMessage { get; }

        public SearchExecutionReport(
            SearchBackendKind backend,
            int matchedCount,
            bool isTruncated,
            long elapsedMs,
            string? noticeMessage = null)
        {
            Backend = backend;
            MatchedCount = matchedCount;
            IsTruncated = isTruncated;
            ElapsedMs = elapsedMs;
            NoticeMessage = noticeMessage;
        }
    }

    /// <summary>
    /// High-speed two-tier file search engine:
    /// Tier 1: Native WM_COPYDATA IPC query directly to a running Everything instance (millisecond results).
    /// Tier 2: Multi-threaded Win32 FindFirstFileExW (FindExInfoBasic + FIND_FIRST_EX_LARGE_FETCH) with
    ///         deferred noisy-directory queues and a short-TTL bounded RAM entry cache for consecutive keystrokes.
    /// </summary>
    internal sealed partial class FastFileSearchService
    {
        public static readonly FastFileSearchService Shared = new FastFileSearchService(EverythingIpcClient.Shared);

        private const int FirstBatchSize = 48;
        private const int SubsequentBatchSize = 96;
        private const int MaxCacheEntriesPerRoot = 120_000;
        private const int MaxCacheMetadataProbes = 4096;
        private const int MaxCachedRoots = 4;
        public const int DefaultMaxTrackedCacheDirectories = 2048;
        public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromSeconds(30);

        private static readonly HashSet<string> DeferredDirNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules",
            ".git",
            ".hg",
            ".svn",
            ".vs",
            "$recycle.bin",
            "system volume information",
            "winsxs",
            "package cache",
            "__pycache__",
            ".nuget",
            "packages",
            "$sysreset",
            "recovery",
            "temp"
        };

        private static readonly HashSet<string> DeferredDriveRootDirNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "windows",
            "programdata",
            "program files",
            "program files (x86)"
        };

        private readonly struct TrackedDirectoryStamp
        {
            public string DirPath { get; }
            public long LastWriteTicks { get; }
            public bool WasHidden { get; }
            public bool WasTraversed { get; }

            public TrackedDirectoryStamp(string dirPath, long lastWriteTicks, bool wasHidden, bool wasTraversed)
            {
                DirPath = dirPath;
                LastWriteTicks = lastWriteTicks;
                WasHidden = wasHidden;
                WasTraversed = wasTraversed;
            }
        }

        private sealed class CachedRootSnapshot
        {
            public string CacheKey { get; }
            public string? RootDir { get; }
            public SearchResultEntry[] Entries { get; }
            public TrackedDirectoryStamp[] DirectoryStamps { get; }
            public DateTime CreatedUtc { get; }
            public DateTime LastAccessedUtc { get; set; }

            public CachedRootSnapshot(
                string cacheKey,
                string? rootDir,
                SearchResultEntry[] entries,
                TrackedDirectoryStamp[] directoryStamps)
            {
                CacheKey = cacheKey;
                RootDir = rootDir;
                Entries = entries;
                DirectoryStamps = directoryStamps;
                DateTime now = DateTime.UtcNow;
                CreatedUtc = now;
                LastAccessedUtc = now;
            }
        }

        private readonly EverythingIpcClient _ipcClient;
        private readonly object _cacheLock = new object();
        private readonly Dictionary<string, CachedRootSnapshot> _rootCache =
            new Dictionary<string, CachedRootSnapshot>(StringComparer.OrdinalIgnoreCase);

        public TimeSpan CacheTtl { get; set; } = DefaultCacheTtl;
        internal int MaxTrackedCacheDirectories { get; set; } = DefaultMaxTrackedCacheDirectories;

        public FastFileSearchService(EverythingIpcClient ipcClient)
        {
            _ipcClient = ipcClient ?? throw new ArgumentNullException(nameof(ipcClient));
        }

        public void InvalidateCache()
        {
            lock (_cacheLock)
            {
                _rootCache.Clear();
            }
        }

        public async Task<SearchExecutionReport> SearchAsync(
            string query,
            string? rootDir,
            bool includeHidden,
            int maxResults,
            Action<SearchResultEntry[], int> onBatch,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(query) || maxResults <= 0)
                return new SearchExecutionReport(SearchBackendKind.None, 0, false, 0);

            Stopwatch sw = Stopwatch.StartNew();
            string trimmedQuery = query.Trim();

            string? normalizedRoot = NormalizeRootDirectory(rootDir);
            ParsedSearchQuery parsedQuery = ParseQuery(trimmedQuery);

            // Tier 1: Query Everything IPC when available
            if (_ipcClient.IsAvailable)
            {
                SearchExecutionReport? everythingReport = await TrySearchViaEverythingAsync(
                    trimmedQuery,
                    normalizedRoot,
                    includeHidden,
                    maxResults,
                    parsedQuery,
                    onBatch,
                    sw,
                    cancellationToken).ConfigureAwait(false);

                if (everythingReport != null)
                    return everythingReport;
            }

            if (cancellationToken.IsCancellationRequested)
                return new SearchExecutionReport(SearchBackendKind.None, 0, false, sw.ElapsedMilliseconds);

            if (parsedQuery.HasUnsupportedNativeSyntax)
            {
                return new SearchExecutionReport(
                    SearchBackendKind.NativeFastScan,
                    0,
                    false,
                    sw.ElapsedMilliseconds,
                    parsedQuery.UnsupportedSyntaxNotice);
            }

            // Tier 2a: Short-TTL RAM snapshot cache for instant consecutive keystrokes
            if (normalizedRoot != null &&
                TrySearchMemoryCache(normalizedRoot, includeHidden, maxResults, parsedQuery, onBatch, sw, cancellationToken, out SearchExecutionReport? cacheReport))
            {
                return cacheReport!;
            }

            // Tier 2b: Multi-threaded Win32 FindFirstFileExW scan
            string[] rootsToScan;
            if (normalizedRoot != null)
            {
                rootsToScan = new[] { normalizedRoot };
            }
            else
            {
                rootsToScan = GetReadyLocalDriveRoots();
                if (rootsToScan.Length == 0)
                    return new SearchExecutionReport(SearchBackendKind.NativeFastScan, 0, false, sw.ElapsedMilliseconds);
            }

            return ScanRootsWithWin32(
                rootsToScan,
                normalizedRoot,
                includeHidden,
                maxResults,
                parsedQuery,
                onBatch,
                sw,
                cancellationToken);
        }

        private async Task<SearchExecutionReport?> TrySearchViaEverythingAsync(
            string rawQuery,
            string? normalizedRoot,
            bool includeHidden,
            int maxResults,
            ParsedSearchQuery parsedQuery,
            Action<SearchResultEntry[], int> onBatch,
            Stopwatch sw,
            CancellationToken cancellationToken)
        {
            string scopedQuery = BuildEverythingScopedQuery(normalizedRoot, rawQuery);
            uint fetchCap = (uint)Math.Min(4000, Math.Max(maxResults * 3, 600));

            EverythingQueryResponse response = await _ipcClient.QueryRawAsync(
                scopedQuery,
                maxResults: fetchCap,
                matchPath: false,
                timeoutMs: 650,
                cancellationToken: cancellationToken,
                offset: 0).ConfigureAwait(false);

            if (!response.IpcAvailable || response.TimedOut || cancellationToken.IsCancellationRequested)
                return null;

            string? scopePrefix = normalizedRoot != null ? EnsureTrailingBackslash(normalizedRoot) : null;

            if (normalizedRoot != null)
            {
                if (response.TotalItems == 0)
                {
                    bool rootIndexed = await _ipcClient.IsDirectoryIndexedAsync(normalizedRoot, cancellationToken).ConfigureAwait(false);
                    if (!rootIndexed)
                        return null; // Unindexed path (e.g. FAT32 USB / UNC / excluded folder) -> Tier 2 fallback

                    return new SearchExecutionReport(SearchBackendKind.Everything, 0, false, sw.ElapsedMilliseconds);
                }

                (int matched, bool truncated, _) = await ConsumeAndPageEverythingAsync(
                    response,
                    scopedQuery,
                    scopePrefix,
                    normalizedRoot,
                    includeHidden,
                    maxResults,
                    onBatch,
                    sw,
                    cancellationToken).ConfigureAwait(false);

                return new SearchExecutionReport(
                    SearchBackendKind.Everything,
                    matched,
                    truncated,
                    sw.ElapsedMilliseconds);
            }

            // Global '此电脑' search: emit Everything results first (with post-filter pagination), then check whether any ready drive is unindexed (e.g. FAT32 USB)
            (int globalMatched, bool globalTruncated, int nextBatchStart) = await ConsumeAndPageEverythingAsync(
                response,
                scopedQuery,
                scopePrefix: null,
                scopeRoot: null,
                includeHidden: includeHidden,
                maxResults: maxResults,
                onBatch: onBatch,
                sw: sw,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            bool anyMoreSpace = !globalTruncated && globalMatched < maxResults;
            if (anyMoreSpace && !cancellationToken.IsCancellationRequested && !parsedQuery.HasUnsupportedNativeSyntax)
            {
                string[] allDrives = GetReadyLocalDriveRoots();
                var unindexedDrives = new List<string>(allDrives.Length);
                foreach (string drive in allDrives)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;
                    bool driveIndexed = await _ipcClient.IsDirectoryIndexedAsync(drive, cancellationToken).ConfigureAwait(false);
                    if (!driveIndexed)
                    {
                        unindexedDrives.Add(drive);
                    }
                }

                if (unindexedDrives.Count > 0 && !cancellationToken.IsCancellationRequested)
                {
                    int remaining = maxResults - globalMatched;
                    SearchExecutionReport extra = ScanRootsWithWin32(
                        unindexedDrives.ToArray(),
                        singleCacheRoot: null,
                        includeHidden: includeHidden,
                        maxResults: remaining,
                        parsedQuery: parsedQuery,
                        onBatch: (items, idx) => onBatch(items, idx + nextBatchStart),
                        sw: sw,
                        cancellationToken: cancellationToken);

                    return new SearchExecutionReport(
                        SearchBackendKind.Everything,
                        globalMatched + extra.MatchedCount,
                        globalTruncated || extra.IsTruncated,
                        sw.ElapsedMilliseconds);
                }
            }

            return new SearchExecutionReport(
                SearchBackendKind.Everything,
                globalMatched,
                globalTruncated,
                sw.ElapsedMilliseconds);
        }

        private async Task<(int Matched, bool Truncated, int NextBatchIndex)> ConsumeAndPageEverythingAsync(
            EverythingQueryResponse firstPage,
            string scopedQuery,
            string? scopePrefix,
            string? scopeRoot,
            bool includeHidden,
            int maxResults,
            Action<SearchResultEntry[], int> onBatch,
            Stopwatch sw,
            CancellationToken cancellationToken)
        {
            const int MaxPages = 24;
            const long MaxPagingBudgetMs = 1500;

            int matched = 0;
            bool hitResultLimit = false;
            int batchIndex = 0;
            int targetBatchSize = FirstBatchSize;
            var pending = new List<SearchResultEntry>(targetBatchSize);
            Dictionary<string, bool>? hiddenParentCache = includeHidden
                ? null
                : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            EverythingQueryResponse currentPage = firstPage;
            uint nextOffset = 0;
            uint lastTotalItems = firstPage.TotalItems;
            int pageNumber = 0;
            bool exhaustedAllPages = false;
            long pagingStartMs = sw.ElapsedMilliseconds;

            while (true)
            {
                pageNumber++;
                lastTotalItems = currentPage.TotalItems;
                List<EverythingIpcItem> items = currentPage.Items;

                for (int i = 0; i < items.Count; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    EverythingIpcItem item = items[i];
                    string fullPath = item.FullPath;
                    if (string.IsNullOrEmpty(fullPath))
                        continue;

                    if (scopePrefix != null)
                    {
                        if (!fullPath.StartsWith(scopePrefix, StringComparison.OrdinalIgnoreCase) ||
                            fullPath.Length <= scopePrefix.Length)
                        {
                            continue;
                        }
                    }

                    uint attrs;
                    if (!includeHidden)
                    {
                        attrs = NativeMethods.GetFileAttributesW(fullPath);
                        if (attrs == NativeMethods.INVALID_FILE_ATTRIBUTES)
                            continue;

                        if ((attrs & NativeMethods.FILE_ATTRIBUTE_HIDDEN) != 0)
                            continue;

                        if (HasHiddenAncestorUnderScope(fullPath, scopeRoot, hiddenParentCache!))
                            continue;
                    }
                    else
                    {
                        attrs = item.IsFolder ? NativeMethods.FILE_ATTRIBUTE_DIRECTORY : NativeMethods.FILE_ATTRIBUTE_NORMAL;
                    }

                    if (matched >= maxResults)
                    {
                        hitResultLimit = true;
                        break;
                    }

                    pending.Add(new SearchResultEntry(fullPath, attrs, item.FileSize, item.LastWriteTime));
                    matched++;

                    if (pending.Count >= targetBatchSize)
                    {
                        onBatch(pending.ToArray(), batchIndex++);
                        pending.Clear();
                        targetBatchSize = SubsequentBatchSize;
                    }
                }

                if (cancellationToken.IsCancellationRequested || hitResultLimit)
                    break;

                nextOffset += (uint)items.Count;
                if (items.Count == 0 || nextOffset >= lastTotalItems)
                {
                    exhaustedAllPages = true;
                    break;
                }

                // If we reached maxResults at the exact end of this page while more items exist in Everything
                if (matched >= maxResults)
                    break;

                if (pageNumber >= MaxPages || (sw.ElapsedMilliseconds - pagingStartMs) >= MaxPagingBudgetMs)
                    break;

                uint nextPageSize = (uint)Math.Min(4000, Math.Max(maxResults * 4, 1600));
                EverythingQueryResponse followUp = await _ipcClient.QueryRawAsync(
                    scopedQuery,
                    maxResults: nextPageSize,
                    matchPath: false,
                    timeoutMs: 500,
                    cancellationToken: cancellationToken,
                    offset: nextOffset).ConfigureAwait(false);

                if (!followUp.IpcAvailable || followUp.TimedOut || cancellationToken.IsCancellationRequested)
                    break;

                currentPage = followUp;
            }

            if (pending.Count > 0 && !cancellationToken.IsCancellationRequested)
            {
                onBatch(pending.ToArray(), batchIndex++);
                pending.Clear();
            }

            bool isTruncated = hitResultLimit ||
                               (!exhaustedAllPages && !cancellationToken.IsCancellationRequested && nextOffset < lastTotalItems);
            return (matched, isTruncated, batchIndex);
        }

        private static bool TryRefreshEntryMetadata(string fullPath, out SearchResultEntry refreshed)
        {
            refreshed = default;
            if (string.IsNullOrWhiteSpace(fullPath))
                return false;

            string queryPath = ToWin32AttributePath(fullPath);
            if (!NativeMethods.GetFileAttributesExW(
                    queryPath,
                    GET_FILEEX_INFO_LEVELS.GetFileExInfoStandard,
                    out WIN32_FILE_ATTRIBUTE_DATA data))
            {
                return false;
            }

            if (data.dwFileAttributes == NativeMethods.INVALID_FILE_ATTRIBUTES)
                return false;

            ulong fileSize = ((ulong)data.nFileSizeHigh << 32) | data.nFileSizeLow;
            refreshed = new SearchResultEntry(fullPath, data.dwFileAttributes, fileSize, data.ftLastWriteTime);
            return true;
        }

        private static bool HasHiddenAncestorUnderScope(
            string fullPath,
            string? scopeRoot,
            Dictionary<string, bool> cache)
        {
            string trimmedScope = scopeRoot != null ? scopeRoot.TrimEnd('\\') : string.Empty;
            string? dir = GetParentDirectoryFast(fullPath);
            if (string.IsNullOrEmpty(dir))
                return false;

            // Walk upward checking directory chain up to drive root or scopeRoot
            var chain = new List<string>(8);
            string? cur = dir;
            bool hiddenFound = false;

            while (!string.IsNullOrEmpty(cur))
            {
                if (!string.IsNullOrEmpty(trimmedScope) &&
                    string.Equals(cur!.TrimEnd('\\'), trimmedScope, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (cur!.Length <= 3 && cur.Length >= 2 && cur[1] == ':')
                    break;

                if (cache.TryGetValue(cur, out bool cachedHidden))
                {
                    hiddenFound = cachedHidden;
                    break;
                }

                chain.Add(cur);
                uint dirAttrs = NativeMethods.GetFileAttributesW(cur);
                if (dirAttrs != NativeMethods.INVALID_FILE_ATTRIBUTES &&
                    (dirAttrs & NativeMethods.FILE_ATTRIBUTE_HIDDEN) != 0)
                {
                    hiddenFound = true;
                    break;
                }

                cur = GetParentDirectoryFast(cur);
            }

            for (int i = 0; i < chain.Count; i++)
            {
                cache[chain[i]] = hiddenFound;
            }

            return hiddenFound;
        }

        private bool TrySearchMemoryCache(
            string rootDir,
            bool includeHidden,
            int maxResults,
            ParsedSearchQuery parsedQuery,
            Action<SearchResultEntry[], int> onBatch,
            Stopwatch sw,
            CancellationToken cancellationToken,
            out SearchExecutionReport? report)
        {
            report = null;
            string cacheKey = BuildCacheKey(rootDir, includeHidden);
            CachedRootSnapshot? snapshot = null;

            lock (_cacheLock)
            {
                if (_rootCache.TryGetValue(cacheKey, out CachedRootSnapshot? existing))
                {
                    DateTime now = DateTime.UtcNow;
                    if (now - existing.CreatedUtc <= CacheTtl)
                    {
                        existing.LastAccessedUtc = now;
                        snapshot = existing;
                    }
                    else
                    {
                        _rootCache.Remove(cacheKey);
                    }
                }
            }

            if (snapshot == null)
                return false;

            if (!AreDirectoryStampsFresh(snapshot.DirectoryStamps))
            {
                lock (_cacheLock)
                {
                    if (_rootCache.TryGetValue(cacheKey, out CachedRootSnapshot? current) &&
                        ReferenceEquals(current, snapshot))
                    {
                        _rootCache.Remove(cacheKey);
                    }
                }
                return false;
            }

            string prefix = EnsureTrailingBackslash(rootDir);
            int prefixLen = prefix.Length;
            SearchResultEntry[] entries = snapshot.Entries;

            // Phase 1: Pure in-memory path/extension/kind pre-filter.
            // If candidate count exceeds MaxCacheMetadataProbes, fall back to bulk FindFirstFileExW(LARGE_FETCH)
            // before emitting any batch so no duplicate results can ever be dispatched.
            var candidateIndices = new List<int>(Math.Min(entries.Length, 256));
            for (int i = 0; i < entries.Length; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                    return false;

                SearchResultEntry entry = entries[i];
                bool isDir = (entry.FileAttributes & NativeMethods.FILE_ATTRIBUTE_DIRECTORY) != 0;
                string rel = entry.FullPath.Length > prefixLen ? entry.FullPath.Substring(prefixLen) : entry.FullPath;
                int sep = rel.LastIndexOf('\\');
                string name = sep >= 0 ? rel.Substring(sep + 1) : rel;

                if (parsedQuery.MatchesPathAndKind(name, rel, isDir))
                {
                    if (candidateIndices.Count >= MaxCacheMetadataProbes)
                        return false;

                    candidateIndices.Add(i);
                }
            }

            // Phase 2: Re-verify live file attributes, size, and last-write timestamp on each candidate
            // so in-place edits (1 B -> 8192 B), newly-hidden files/ancestors, and un-hidden files are always fresh.
            int matched = 0;
            bool truncated = false;
            int batchIndex = 0;
            int targetBatch = FirstBatchSize;
            var pending = new List<SearchResultEntry>(targetBatch);
            Dictionary<string, bool>? hiddenParentCache = includeHidden
                ? null
                : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            for (int c = 0; c < candidateIndices.Count; c++)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                SearchResultEntry cachedEntry = entries[candidateIndices[c]];
                if (!TryRefreshEntryMetadata(cachedEntry.FullPath, out SearchResultEntry liveEntry))
                    continue;

                if (!includeHidden)
                {
                    if ((liveEntry.FileAttributes & NativeMethods.FILE_ATTRIBUTE_HIDDEN) != 0)
                        continue;

                    if (HasHiddenAncestorUnderScope(liveEntry.FullPath, rootDir, hiddenParentCache!))
                        continue;
                }

                string rel = liveEntry.FullPath.Length > prefixLen ? liveEntry.FullPath.Substring(prefixLen) : liveEntry.FullPath;
                int sep = rel.LastIndexOf('\\');
                string name = sep >= 0 ? rel.Substring(sep + 1) : rel;

                if (!parsedQuery.MatchesEntry(name, rel, in liveEntry))
                    continue;

                if (matched >= maxResults)
                {
                    truncated = true;
                    break;
                }

                pending.Add(liveEntry);
                matched++;

                if (pending.Count >= targetBatch)
                {
                    onBatch(pending.ToArray(), batchIndex++);
                    pending.Clear();
                    targetBatch = SubsequentBatchSize;
                }
            }

            if (pending.Count > 0 && !cancellationToken.IsCancellationRequested)
            {
                onBatch(pending.ToArray(), batchIndex);
            }

            report = new SearchExecutionReport(SearchBackendKind.MemoryCache, matched, truncated, sw.ElapsedMilliseconds);
            return true;
        }

        private SearchExecutionReport ScanRootsWithWin32(
            string[] roots,
            string? singleCacheRoot,
            bool includeHidden,
            int maxResults,
            ParsedSearchQuery parsedQuery,
            Action<SearchResultEntry[], int> onBatch,
            Stopwatch sw,
            CancellationToken cancellationToken)
        {
            var queueLock = new object();
            var primaryQueue = new Queue<string>(64);
            var deferredQueue = new Queue<string>(32);
            bool drainingDeferred = false;
            int activeWorkers = 0;
            bool stopRequested = false;

            var batchLock = new object();
            var dispatchLock = new object();
            var pendingBatch = new List<SearchResultEntry>(FirstBatchSize);
            int totalMatched = 0;
            bool isTruncated = false;
            int batchIndex = 0;
            int nextBatchTarget = FirstBatchSize;

            bool collectCache = singleCacheRoot != null;
            int maxTrackedDirs = Math.Max(1, MaxTrackedCacheDirectories);
            int capturedCount = 0;
            int trackedDirCount = 0;
            int cacheAbandoned = 0;
            var allCapturedChunks = collectCache ? new ConcurrentBag<List<SearchResultEntry>>() : null;
            var trackedCacheDirs = collectCache ? new ConcurrentQueue<TrackedDirectoryStamp>() : null;

            string? singlePrefix = singleCacheRoot != null ? EnsureTrailingBackslash(singleCacheRoot) : null;
            int singlePrefixLen = singlePrefix?.Length ?? 0;

            if (singleCacheRoot != null)
            {
                primaryQueue.Enqueue(singleCacheRoot);
            }
            else
            {
                EnqueueInitialMultiDrivePriorityDirs(primaryQueue, roots, includeHidden);
                foreach (string r in roots)
                {
                    string cleanRoot = EnsureTrailingBackslash(r);
                    primaryQueue.Enqueue(cleanRoot);
                }
            }

            // Dedup visited directories when prioritizing user folders + drive roots
            var visitedDirs = roots.Length > 1
                ? new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase)
                : null;

            int workerCount = Math.Min(8, Math.Max(2, Environment.ProcessorCount));
            Task[] workers = new Task[workerCount];

            void SignalStop()
            {
                Volatile.Write(ref stopRequested, true);
                if (collectCache)
                    Volatile.Write(ref cacheAbandoned, 1);
                lock (queueLock)
                {
                    Monitor.PulseAll(queueLock);
                }
            }

            using CancellationTokenRegistration reg = cancellationToken.Register(SignalStop);

            for (int w = 0; w < workerCount; w++)
            {
                workers[w] = Task.Run(() =>
                {
                    var localMatches = new List<SearchResultEntry>(16);
                    var localPrimarySubdirs = new List<string>(16);
                    var localDeferredSubdirs = new List<string>(8);
                    var localBatchesToSend = new List<(SearchResultEntry[] Items, int Index)>(4);
                    List<SearchResultEntry>? localCacheChunk = collectCache ? new List<SearchResultEntry>(256) : null;

                    while (!Volatile.Read(ref stopRequested) && !cancellationToken.IsCancellationRequested)
                    {
                        string? currentDir = null;
                        lock (queueLock)
                        {
                            while (true)
                            {
                                if (Volatile.Read(ref stopRequested) || cancellationToken.IsCancellationRequested)
                                {
                                    currentDir = null;
                                    break;
                                }

                                if (primaryQueue.Count > 0)
                                {
                                    currentDir = primaryQueue.Dequeue();
                                    activeWorkers++;
                                    break;
                                }

                                if (activeWorkers == 0 && deferredQueue.Count > 0)
                                {
                                    drainingDeferred = true;
                                }

                                if (drainingDeferred && deferredQueue.Count > 0)
                                {
                                    currentDir = deferredQueue.Dequeue();
                                    activeWorkers++;
                                    break;
                                }

                                if (activeWorkers == 0 && primaryQueue.Count == 0 && deferredQueue.Count == 0)
                                {
                                    Monitor.PulseAll(queueLock);
                                    currentDir = null;
                                    break;
                                }

                                Monitor.Wait(queueLock, 40);
                            }
                        }

                        if (currentDir == null)
                            break;

                        localMatches.Clear();
                        localPrimarySubdirs.Clear();
                        localDeferredSubdirs.Clear();

                        try
                        {
                            if (visitedDirs != null && !visitedDirs.TryAdd(currentDir.TrimEnd('\\'), 0))
                                continue;

                            if (trackedCacheDirs != null && Volatile.Read(ref cacheAbandoned) == 0)
                            {
                                if (Interlocked.Increment(ref trackedDirCount) <= maxTrackedDirs &&
                                    TryGetDirectoryStamp(currentDir, out long dirStamp, out bool dirIsHidden))
                                {
                                    trackedCacheDirs.Enqueue(new TrackedDirectoryStamp(currentDir, dirStamp, dirIsHidden, wasTraversed: true));
                                }
                                else
                                {
                                    Volatile.Write(ref cacheAbandoned, 1);
                                }
                            }

                            EnumerateDirectoryNative(
                                currentDir,
                                singlePrefixLen,
                                includeHidden,
                                parsedQuery,
                                collectCache && Volatile.Read(ref cacheAbandoned) == 0,
                                maxTrackedDirs,
                                trackedCacheDirs,
                                localMatches,
                                localPrimarySubdirs,
                                localDeferredSubdirs,
                                localCacheChunk,
                                ref capturedCount,
                                ref trackedDirCount,
                                ref cacheAbandoned,
                                ref stopRequested,
                                cancellationToken);
                        }
                        finally
                        {
                            lock (queueLock)
                            {
                                for (int i = 0; i < localPrimarySubdirs.Count; i++)
                                    primaryQueue.Enqueue(localPrimarySubdirs[i]);
                                for (int i = 0; i < localDeferredSubdirs.Count; i++)
                                    deferredQueue.Enqueue(localDeferredSubdirs[i]);

                                activeWorkers--;
                                Monitor.PulseAll(queueLock);
                            }
                        }

                        if (localMatches.Count > 0 && !cancellationToken.IsCancellationRequested)
                        {
                            localBatchesToSend.Clear();

                            lock (batchLock)
                            {
                                for (int i = 0; i < localMatches.Count; i++)
                                {
                                    if (totalMatched >= maxResults)
                                    {
                                        isTruncated = true;
                                        SignalStop();
                                        break;
                                    }

                                    pendingBatch.Add(localMatches[i]);
                                    totalMatched++;

                                    if (pendingBatch.Count >= nextBatchTarget)
                                    {
                                        localBatchesToSend.Add((pendingBatch.ToArray(), batchIndex++));
                                        pendingBatch.Clear();
                                        nextBatchTarget = SubsequentBatchSize;
                                    }
                                }

                                if (totalMatched >= maxResults && pendingBatch.Count > 0)
                                {
                                    localBatchesToSend.Add((pendingBatch.ToArray(), batchIndex++));
                                    pendingBatch.Clear();
                                    nextBatchTarget = SubsequentBatchSize;
                                }
                            }

                            if (localBatchesToSend.Count > 0 && !cancellationToken.IsCancellationRequested)
                            {
                                lock (dispatchLock)
                                {
                                    for (int b = 0; b < localBatchesToSend.Count; b++)
                                    {
                                        if (cancellationToken.IsCancellationRequested)
                                            break;
                                        (SearchResultEntry[] items, int idx) = localBatchesToSend[b];
                                        onBatch(items, idx);
                                    }
                                }
                            }
                        }
                    }

                    if (localCacheChunk != null && localCacheChunk.Count > 0 && Volatile.Read(ref cacheAbandoned) == 0)
                    {
                        allCapturedChunks!.Add(localCacheChunk);
                    }
                });
            }

            Task.WaitAll(workers);

            SearchResultEntry[]? tailBatch = null;
            int tailIndex = -1;
            lock (batchLock)
            {
                if (pendingBatch.Count > 0 && !cancellationToken.IsCancellationRequested)
                {
                    tailBatch = pendingBatch.ToArray();
                    pendingBatch.Clear();
                    tailIndex = batchIndex++;
                }
            }

            if (tailBatch != null && !cancellationToken.IsCancellationRequested)
            {
                lock (dispatchLock)
                {
                    if (!cancellationToken.IsCancellationRequested)
                        onBatch(tailBatch, tailIndex);
                }
            }

            if (collectCache &&
                singleCacheRoot != null &&
                !isTruncated &&
                !cancellationToken.IsCancellationRequested &&
                Volatile.Read(ref cacheAbandoned) == 0 &&
                allCapturedChunks != null)
            {
                StoreCacheSnapshot(singleCacheRoot, includeHidden, allCapturedChunks, trackedCacheDirs);
            }

            return new SearchExecutionReport(SearchBackendKind.NativeFastScan, totalMatched, isTruncated, sw.ElapsedMilliseconds);
        }

        private static void EnqueueInitialMultiDrivePriorityDirs(
            Queue<string> primaryQueue,
            string[] allowedRoots,
            bool includeHidden)
        {
            void TryEnqueuePriority(string? dir)
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                    return;

                bool underAllowedRoot = false;
                for (int i = 0; i < allowedRoots.Length; i++)
                {
                    if (dir!.StartsWith(allowedRoots[i], StringComparison.OrdinalIgnoreCase))
                    {
                        underAllowedRoot = true;
                        break;
                    }
                }
                if (!underAllowedRoot)
                    return;

                if (!includeHidden)
                {
                    uint attrs = NativeMethods.GetFileAttributesW(dir!);
                    if (attrs != NativeMethods.INVALID_FILE_ATTRIBUTES && (attrs & NativeMethods.FILE_ATTRIBUTE_HIDDEN) != 0)
                        return;
                }
                primaryQueue.Enqueue(dir!);
            }

            try
            {
                TryEnqueuePriority(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
                TryEnqueuePriority(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
                TryEnqueuePriority(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            }
            catch
            {
            }
        }

        private static void EnumerateDirectoryNative(
            string currentDir,
            int singlePrefixLen,
            bool includeHidden,
            ParsedSearchQuery parsedQuery,
            bool captureForCache,
            int maxTrackedDirs,
            ConcurrentQueue<TrackedDirectoryStamp>? trackedCacheDirs,
            List<SearchResultEntry> localMatches,
            List<string> localPrimarySubdirs,
            List<string> localDeferredSubdirs,
            List<SearchResultEntry>? localCacheChunk,
            ref int capturedCount,
            ref int trackedDirCount,
            ref int cacheAbandoned,
            ref bool stopRequested,
            CancellationToken cancellationToken)
        {
            string dirWithSlash = EnsureTrailingBackslash(currentDir);
            string searchPattern = ToWin32FindPattern(dirWithSlash);
            bool isDriveRoot = dirWithSlash.Length <= 3;

            IntPtr hFind = NativeMethods.FindFirstFileExW(
                searchPattern,
                FINDEX_INFO_LEVELS.FindExInfoBasic,
                out WIN32_FIND_DATAW fd,
                FINDEX_SEARCH_OPS.FindExSearchNameMatch,
                IntPtr.Zero,
                NativeMethods.FIND_FIRST_EX_LARGE_FETCH);

            if (hFind == NativeMethods.INVALID_HANDLE_VALUE)
                return;

            try
            {
                do
                {
                    if (Volatile.Read(ref stopRequested) || cancellationToken.IsCancellationRequested)
                        break;

                    string name = fd.cFileName;
                    if (string.IsNullOrEmpty(name))
                        continue;

                    if (name == "." || name == "..")
                        continue;

                    uint attrs = fd.dwFileAttributes;
                    bool isHidden = (attrs & NativeMethods.FILE_ATTRIBUTE_HIDDEN) != 0;
                    bool isDir = (attrs & NativeMethods.FILE_ATTRIBUTE_DIRECTORY) != 0;
                    string fullPath = dirWithSlash + name;

                    // Do not traverse hidden subdirectories when hidden items are disabled,
                    // but DO record them in DirectoryStamps so un-hiding a directory (without touching its parent's LastWriteTime)
                    // immediately invalidates the snapshot and triggers a fresh scan.
                    if (!includeHidden && isHidden && isDir)
                    {
                        if (captureForCache && trackedCacheDirs != null && Volatile.Read(ref cacheAbandoned) == 0)
                        {
                            if (Interlocked.Increment(ref trackedDirCount) <= maxTrackedDirs)
                            {
                                long hiddenDirTicks = ((long)fd.ftLastWriteTime.dwHighDateTime << 32) | (uint)fd.ftLastWriteTime.dwLowDateTime;
                                trackedCacheDirs.Enqueue(new TrackedDirectoryStamp(fullPath, hiddenDirTicks, wasHidden: true, wasTraversed: false));
                            }
                            else
                            {
                                Volatile.Write(ref cacheAbandoned, 1);
                            }
                        }
                        continue;
                    }

                    string relTarget = (singlePrefixLen > 0 && fullPath.Length > singlePrefixLen)
                        ? fullPath.Substring(singlePrefixLen)
                        : name;

                    ulong fileSize = ((ulong)fd.nFileSizeHigh << 32) | fd.nFileSizeLow;
                    var entry = new SearchResultEntry(fullPath, attrs, fileSize, fd.ftLastWriteTime);

                    if (captureForCache && localCacheChunk != null && Volatile.Read(ref cacheAbandoned) == 0)
                    {
                        if (Interlocked.Increment(ref capturedCount) <= MaxCacheEntriesPerRoot)
                        {
                            localCacheChunk.Add(entry);
                        }
                        else
                        {
                            Volatile.Write(ref cacheAbandoned, 1);
                        }
                    }

                    if ((includeHidden || !isHidden) && parsedQuery.MatchesEntry(name, relTarget, in entry))
                    {
                        localMatches.Add(entry);
                    }

                    bool isSubDir = isDir && (attrs & NativeMethods.FILE_ATTRIBUTE_REPARSE_POINT) == 0;
                    if (isSubDir)
                    {
                        if (DeferredDirNames.Contains(name) ||
                            (isDriveRoot && DeferredDriveRootDirNames.Contains(name)))
                        {
                            localDeferredSubdirs.Add(fullPath);
                        }
                        else
                        {
                            localPrimarySubdirs.Add(fullPath);
                        }
                    }
                }
                while (NativeMethods.FindNextFileW(hFind, out fd));
            }
            finally
            {
                NativeMethods.FindClose(hFind);
            }
        }

        private void StoreCacheSnapshot(
            string rootDir,
            bool includeHidden,
            ConcurrentBag<List<SearchResultEntry>> chunks,
            ConcurrentQueue<TrackedDirectoryStamp>? trackedDirs)
        {
            if (trackedDirs == null)
                return;

            var stampsList = new List<TrackedDirectoryStamp>(Math.Max(16, trackedDirs.Count));
            var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (TrackedDirectoryStamp dirStamp in trackedDirs)
            {
                string clean = dirStamp.DirPath.TrimEnd('\\');
                if (seenDirs.Add(clean))
                {
                    stampsList.Add(dirStamp);
                }
            }

            if (stampsList.Count == 0 || !seenDirs.Contains(rootDir.TrimEnd('\\')))
                return;

            int total = 0;
            foreach (List<SearchResultEntry> chunk in chunks)
                total += chunk.Count;

            var merged = new SearchResultEntry[total];
            int offset = 0;
            foreach (List<SearchResultEntry> chunk in chunks)
            {
                chunk.CopyTo(merged, offset);
                offset += chunk.Count;
            }

            TrackedDirectoryStamp[] stamps = stampsList.ToArray();
            string cacheKey = BuildCacheKey(rootDir, includeHidden);
            lock (_cacheLock)
            {
                _rootCache[cacheKey] = new CachedRootSnapshot(cacheKey, rootDir, merged, stamps);

                while (_rootCache.Count > MaxCachedRoots)
                {
                    string? oldestKey = null;
                    DateTime oldestTime = DateTime.MaxValue;
                    foreach (KeyValuePair<string, CachedRootSnapshot> kv in _rootCache)
                    {
                        if (kv.Key != cacheKey && kv.Value.LastAccessedUtc < oldestTime)
                        {
                            oldestTime = kv.Value.LastAccessedUtc;
                            oldestKey = kv.Key;
                        }
                    }

                    if (oldestKey == null)
                        break;

                    _rootCache.Remove(oldestKey);
                }
            }
        }

        private static bool AreDirectoryStampsFresh(TrackedDirectoryStamp[] stamps)
        {
            if (stamps == null || stamps.Length == 0)
                return false;

            for (int i = 0; i < stamps.Length; i++)
            {
                TrackedDirectoryStamp expected = stamps[i];
                if (!TryGetDirectoryStamp(expected.DirPath, out long currentTicks, out bool currentIsHidden))
                    return false;

                if (currentIsHidden != expected.WasHidden)
                    return false;

                if (expected.WasTraversed && currentTicks != expected.LastWriteTicks)
                    return false;
            }

            return true;
        }

        private static bool TryGetDirectoryStamp(string dirPath, out long stamp, out bool isHidden)
        {
            stamp = -1;
            isHidden = false;
            if (string.IsNullOrWhiteSpace(dirPath))
                return false;

            string queryPath = ToWin32AttributePath(dirPath);
            if (!NativeMethods.GetFileAttributesExW(
                    queryPath,
                    GET_FILEEX_INFO_LEVELS.GetFileExInfoStandard,
                    out WIN32_FILE_ATTRIBUTE_DATA data))
            {
                return false;
            }

            if (data.dwFileAttributes == NativeMethods.INVALID_FILE_ATTRIBUTES ||
                (data.dwFileAttributes & NativeMethods.FILE_ATTRIBUTE_DIRECTORY) == 0)
            {
                return false;
            }

            stamp = ((long)data.ftLastWriteTime.dwHighDateTime << 32) | (uint)data.ftLastWriteTime.dwLowDateTime;
            isHidden = (data.dwFileAttributes & NativeMethods.FILE_ATTRIBUTE_HIDDEN) != 0;
            return true;
        }

        private static string ToWin32AttributePath(string dirPath)
        {
            string trimmed = dirPath.TrimEnd('\\');
            string normalized = (trimmed.Length == 2 && trimmed[1] == ':') ? (trimmed + "\\") : trimmed;
            if (normalized.Length < 240 || normalized.StartsWith(@"\\?\", StringComparison.Ordinal))
                return normalized;

            if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
                return @"\\?\UNC\" + normalized.Substring(2);

            return @"\\?\" + normalized;
        }

        internal static string BuildEverythingScopedQuery(string? rootDir, string rawQuery)
        {
            string normalizedQuery = rawQuery.Trim();
            if (normalizedQuery.IndexOf("regex:", StringComparison.OrdinalIgnoreCase) < 0)
            {
                normalizedQuery = normalizedQuery.Replace('/', '\\');
            }

            int quoteCount = 0;
            for (int i = 0; i < normalizedQuery.Length; i++)
            {
                if (normalizedQuery[i] == '"')
                    quoteCount++;
            }
            if ((quoteCount & 1) == 1)
            {
                normalizedQuery += "\"";
            }

            if (string.IsNullOrWhiteSpace(rootDir))
                return normalizedQuery;

            string prefix = EnsureTrailingBackslash(rootDir!).Replace("\"", string.Empty);
            return $"\"{prefix}\" {normalizedQuery}";
        }

        private static List<string> TokenizeQuery(string query)
        {
            var result = new List<string>(4);
            var sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < query.Length; i++)
            {
                char ch = query[i];
                if (ch == '"')
                {
                    inQuotes = !inQuotes;
                    continue;
                }

                if (!inQuotes && char.IsWhiteSpace(ch))
                {
                    if (sb.Length > 0)
                    {
                        result.Add(sb.ToString());
                        sb.Clear();
                    }
                    continue;
                }

                sb.Append(ch);
            }

            if (sb.Length > 0)
                result.Add(sb.ToString());

            return result;
        }

        private static string[] GetReadyLocalDriveRoots()
        {
            var list = new List<string>(8);
            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (drive.IsReady &&
                            (drive.DriveType == DriveType.Fixed || drive.DriveType == DriveType.Removable))
                        {
                            list.Add(EnsureTrailingBackslash(drive.Name));
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
            return list.ToArray();
        }

        private static string? NormalizeRootDirectory(string? rootDir)
        {
            if (string.IsNullOrWhiteSpace(rootDir))
                return null;

            try
            {
                string full = Path.GetFullPath(rootDir!.Trim().Trim('"'));
                return EnsureTrailingBackslash(full);
            }
            catch
            {
                return null;
            }
        }

        private static string EnsureTrailingBackslash(string path)
        {
            string trimmed = path.TrimEnd('\\');
            if (trimmed.Length == 2 && trimmed[1] == ':')
                return trimmed + "\\";
            return path.EndsWith("\\", StringComparison.Ordinal) ? path : path + "\\";
        }

        private static string ToWin32FindPattern(string dirWithSlash)
        {
            string pattern = dirWithSlash + "*";
            if (pattern.Length < 240 || pattern.StartsWith(@"\\?\", StringComparison.Ordinal))
                return pattern;

            if (pattern.StartsWith(@"\\", StringComparison.Ordinal))
                return @"\\?\UNC\" + pattern.Substring(2);

            return @"\\?\" + pattern;
        }

        private static string BuildCacheKey(string rootDir, bool includeHidden) =>
            $"{(includeHidden ? '1' : '0')}|{rootDir.TrimEnd('\\')}";

        private static string? GetParentDirectoryFast(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;

            string trimmed = path.TrimEnd('\\');
            int idx = trimmed.LastIndexOf('\\');
            if (idx <= 0)
                return null;

            string parent = trimmed.Substring(0, idx);
            if (parent.Length == 2 && parent[1] == ':')
                return parent + "\\";
            return parent;
        }
    }
}
