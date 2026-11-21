using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LeafTab.Interop;
using LeafTab.Services;

namespace LeafTab.Controls
{
    public sealed partial class ExplorerBrowserHost
    {
        // ==================== ASYNC DIRECTORY SEARCH (NATIVE IResultsFolder) ====================

        internal Func<bool>? SearchHiddenItemsProvider { get; set; }
        internal FastFileSearchService FileSearchService { get; set; } = FastFileSearchService.Shared;
        internal Func<IntPtr, string, int, CancellationToken, (List<byte[]> Items, bool Truncated)> VirtualChildrenProvider
            { get; set; } = CollectMatchingVirtualChildren;
        internal bool SimulateSearchViewTimeoutForTest { get; set; }

        private bool EnsureSearchBrowserReady(out bool isFreshBrowser)
        {
            isFreshBrowser = false;
            if (_isDestroyed || _hostHwnd == IntPtr.Zero)
                return false;

            if (_searchBrowser != null && _searchHostHwnd != IntPtr.Zero)
            {
                if (IsResultsFolderReady())
                {
                    _searchBrowser.RemoveAll();
                    return true;
                }

                // 首次 FillFromObject 超时或失败后会残留半初始化的搜索视图，必须销毁后重建
                DestroySearchBrowserOnly();
            }

            NativeMethods.GetClientRect(_hostHwnd, out RECT rc);
            int w = Math.Max(1, rc.Width > 0 ? rc.Width : 800);
            int h = Math.Max(1, rc.Height > 0 ? rc.Height : 500);

            _searchHostHwnd = NativeMethods.CreateWindowExW(
                0,
                EnsureWhiteHostWindowClass(),
                string.Empty,
                NativeMethods.WS_CHILD | NativeMethods.WS_CLIPCHILDREN | NativeMethods.WS_CLIPSIBLINGS,
                0,
                0,
                w,
                h,
                _hostHwnd,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            if (_searchHostHwnd == IntPtr.Zero)
                return false;

            Type? comType = Type.GetTypeFromCLSID(ShellConstants.CLSID_ExplorerBrowser, throwOnError: false);
            if (comType == null)
                return false;

            _searchBrowser = (IExplorerBrowser)Activator.CreateInstance(comType)!;
            _searchPaneSite = new SearchResultsSite(this);
            if (_searchBrowser is IObjectWithSite withSite)
            {
                withSite.SetSite(_searchPaneSite);
            }

            _searchBrowser.SetOptions(EXPLORER_BROWSER_OPTIONS.EBO_NOBORDER |
                EXPLORER_BROWSER_OPTIONS.EBO_NOPERSISTVIEWSTATE);

            var initRect = new RECT(0, 0, w, h);
            var fs = new FOLDERSETTINGS
            {
                ViewMode = FOLDERVIEWMODE.FVM_DETAILS,
                fFlags = FOLDERFLAGS.FWF_AUTOARRANGE
            };

            if (_searchBrowser.Initialize(_searchHostHwnd, ref initRect, ref fs) != ShellConstants.S_OK)
            {
                DestroySearchBrowserOnly();
                return false;
            }

            _searchBrowser.SetPropertyBag("LeafTab.SearchBag.v2");

            _searchEventSink = new SearchBrowserEventSink(this);
            if (_searchBrowser.Advise(_searchEventSink, out uint cookie) == ShellConstants.S_OK)
            {
                _searchAdviseCookie = cookie;
            }

            _isFillSearchTransition = true;
            int fillHr = _searchBrowser.FillFromObject(null, 0);
            if (fillHr != ShellConstants.S_OK)
            {
                _isFillSearchTransition = false;
                DestroySearchBrowserOnly();
                return false;
            }

            isFreshBrowser = true;
            return true;
        }

        private sealed class OwnedPidlBatch : IDisposable
        {
            private IntPtr[]? _pidls;
            private readonly int _count;
            private int _disposed;

            public OwnedPidlBatch(IntPtr[] pidls, int count)
            {
                _pidls = pidls;
                _count = count;
            }

            public int Count => _count;

            public IntPtr[]? RawArray => _pidls;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;

                IntPtr[]? arr = Interlocked.Exchange(ref _pidls, null);
                if (arr == null)
                    return;

                for (int i = 0; i < _count; i++)
                {
                    if (arr[i] != IntPtr.Zero)
                    {
                        NativeMethods.ILFree(arr[i]);
                        arr[i] = IntPtr.Zero;
                    }
                }
            }
        }

        public Task<int> LoadMoreSearchResultsAsync()
        {
            if (_isDestroyed || _browser == null || !CanLoadMoreSearchResults)
                return Task.FromResult(_searchMatchCount);

            int nextMax = Math.Min(MaxLoadMoreSearchResults, Math.Max(DefaultMaxSearchResults * 3, _currentSearchMaxResults * 3));
            return StartSearchAsync(_lastSearchQuery, _preSearchFolderPidl, _lastSearchRootPath, nextMax);
        }

        internal int SearchRequestVersion { get; private set; }

        public async Task<int> StartSearchAsync(
            string rawQuery, IntPtr currentFolderPidl, string? folderFileSystemPath, int maxResults = DefaultMaxSearchResults)
        {
            SearchRequestVersion++;
            var result = await RunSearchAsync(rawQuery, currentFolderPidl, folderFileSystemPath, maxResults);
            return result.Count;
        }

        internal async Task<bool> StartSearchForMoveAsync(
            string rawQuery, IntPtr currentFolderPidl, string? folderFileSystemPath, int maxResults)
        {
            var result = await RunSearchAsync(rawQuery, currentFolderPidl, folderFileSystemPath, maxResults);
            return result.Completed;
        }

        private async Task<(int Count, bool Completed)> RunSearchAsync(
            string rawQuery, IntPtr currentFolderPidl, string? folderFileSystemPath, int maxResults)
        {
            if (_isDestroyed || _browser == null)
                return (0, false);

            string query = (rawQuery ?? string.Empty).Trim();
            if (query.Length == 0)
            {
                ExitSearchModeInternal(notifyChanged: true);
                return (0, false);
            }

            int limit = Math.Max(1, Math.Min(MaxLoadMoreSearchResults, maxResults));
            var elapsed = Stopwatch.StartNew();
            if (!_isShowingSearchResults && currentFolderPidl != IntPtr.Zero)
            {
                PidlManager.Free(ref _preSearchFolderPidl);
                _preSearchFolderPidl = PidlManager.Clone(currentFolderPidl);
            }

            _lastSearchQuery = query;
            _lastSearchRootPath = folderFileSystemPath;
            _currentSearchMaxResults = limit;
            _lastSearchNotice = null;

            bool hasFileSystemRoot = !string.IsNullOrWhiteSpace(folderFileSystemPath) && Directory.Exists(folderFileSystemPath);
            IntPtr sourceFolder = _preSearchFolderPidl != IntPtr.Zero ? _preSearchFolderPidl : currentFolderPidl;
            bool isThisPc = !hasFileSystemRoot && PidlManager.IsThisPcPidl(sourceFolder);
            bool searchFiles = hasFileSystemRoot || isThisPc;
            bool searchVirtual = !hasFileSystemRoot &&
                (!isThisPc || !FastFileSearchService.ParseQuery(query).HasUnsupportedNativeSyntax);
            byte[]? sourceBytes = searchVirtual && sourceFolder != IntPtr.Zero ? PidlManager.ToBytes(sourceFolder) : null;

            CancelSearchWorkerOnly();
            var searchCts = new CancellationTokenSource();
            _searchCts = searchCts;
            CancellationToken token = searchCts.Token;
            int generation = ++_searchGeneration;
            bool IsCurrent() => !_isDestroyed && !token.IsCancellationRequested && generation == _searchGeneration;

            var viewPrepared = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var fileBudget = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task workers = Task.CompletedTask;
            try
            {
                bool browserReady = EnsureSearchBrowserReady(out bool firstInit);
                if (!IsCurrent()) return (0, false);
                if (!browserReady || _searchBrowser == null)
                {
                    _lastSearchNotice = "搜索结果视图未能初始化，请重新搜索。";
                    _isSearchTruncated = false;
                    SearchStateChanged?.Invoke(this, EventArgs.Empty);
                    return (0, false);
                }

                _isShowingSearchResults = true;
                ShowSearchPresentation();
                if (!IsCurrent()) return (0, false);
                _isSearchRunning = true;
                _isSearchTruncated = false;
                _searchMatchCount = 0;
                _lastSearchBackend = SearchBackendKind.None;
                _lastSearchElapsedMs = 0;
                InvalidateSelectionCache();
                _searchBrowser.SetEmptyText($"正在搜索“{query}”…");
                SearchStateChanged?.Invoke(this, EventArgs.Empty);
                if (!IsCurrent()) return (0, false);

                Task<(List<byte[]> Items, bool Truncated)>? virtualTask = null;
                if (searchVirtual)
                {
                    virtualTask = PidlManager.RunVirtualSearchAsync(staToken =>
                    {
                        IntPtr folder = sourceBytes == null ? IntPtr.Zero : PidlManager.FromBytes(sourceBytes);
                        try { return VirtualChildrenProvider(folder, query, limit, staToken); }
                        finally { PidlManager.Free(ref folder); }
                    }, token, timeoutMs: 30000);
                    workers = virtualTask;
                }

                // Leave room for top-level Shell devices while the filesystem scan streams.
                // Only the tail waits for their actual count; unused slots return to files.
                int reservedForDevices = Math.Min(64, (limit + 1) / 2);
                int earlyFileBudget = searchVirtual ? limit - reservedForDevices : limit;
                if (!searchVirtual) fileBudget.TrySetResult(limit);
                Task<(SearchExecutionReport Report, bool Omitted)>? fileTask = null;
                if (searchFiles)
                {
                    bool includeHidden = SearchHiddenItemsProvider?.Invoke() ?? PidlManager.GetShellVisibilityFlags().ShowHidden;
                    fileTask = Task.Run(() => SearchFileBatchesAsync(query, hasFileSystemRoot ? folderFileSystemPath : null, includeHidden, limit,
                        earlyFileBudget, fileBudget.Task, viewPrepared.Task, generation, token), token);
                    workers = Task.WhenAll(workers, fileTask);
                }

                if (!await WaitForResultsFolderReadyAsync(firstInit, generation, token, elapsed) || !IsCurrent())
                    return (0, false);

                AttachDirectUiSubclassIfNeeded();
                TrySetViewStyle(ExplorerViewStyle.Details);
                EnsureSearchColumnsConfigured();
                viewPrepared.TrySetResult(true);

                int virtualAdded = 0;
                bool truncated = false;
                bool completed = true;
                string? notice = null;
                if (virtualTask != null)
                {
                    try
                    {
                        var result = await virtualTask;
                        truncated = result.Truncated;
                        int offset = 0;
                        while (offset < result.Items.Count && IsCurrent())
                        {
                            int capacity = limit - _searchMatchCount;
                            if (capacity <= 0)
                            {
                                truncated = true;
                                break;
                            }
                            int count = Math.Min(Math.Min(offset == 0 ? 48 : 96, capacity), result.Items.Count - offset);
                            int added = AddPidlBatchToResultsFolder(result.Items.GetRange(offset, count), generation);
                            if (!IsCurrent()) return (0, false);
                            virtualAdded += added;
                            _searchMatchCount += added;
                            offset += count;
                            InvalidateSelectionCache();
                            SearchStateChanged?.Invoke(this, EventArgs.Empty);
                            if (offset < result.Items.Count)
                                await Dispatcher.Yield(DispatcherPriority.Background);
                        }
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested)
                    {
                        completed = false;
                        notice = ex is TimeoutException ? "虚拟目录搜索响应超时，请稍后重试。" :
                            ex is NotSupportedException ? "虚拟目录不支持此搜索条件。" : "未能读取虚拟目录，请稍后重试。";
                    }
                }
                if (!IsCurrent()) return (0, false);
                fileBudget.TrySetResult(limit - virtualAdded);

                SearchBackendKind backend = SearchBackendKind.VirtualFolder;
                if (fileTask != null)
                {
                    var (report, omitted) = await fileTask;
                    if (!IsCurrent()) return (0, false);
                    backend = report.Backend;
                    truncated |= report.IsTruncated || omitted;
                    completed &= report.QuerySupported && backend != SearchBackendKind.None;
                    if (report.NoticeMessage != null)
                        notice = notice == null ? report.NoticeMessage : notice + " " + report.NoticeMessage;
                }

                _isSearchTruncated = truncated;
                _lastSearchBackend = backend;
                _lastSearchElapsedMs = Math.Max(1, elapsed.ElapsedMilliseconds);
                _lastSearchNotice = notice;
                _isSearchRunning = false;
                int found = _searchMatchCount;
                _searchBrowser?.SetEmptyText(_searchMatchCount == 0 ? notice ?? "没有与搜索条件匹配的项。" : string.Empty);
                if (!IsCurrent()) return (0, false);
                InvalidateSelectionCache();
                SearchStateChanged?.Invoke(this, EventArgs.Empty);
                return (found, completed && IsCurrent());
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return (0, false);
            }
            catch (Exception ex)
            {
                if (!IsCurrent()) return (0, false);
                LeafTab.Shared.DiagnosticLog.Write("search-failed", ex);
                _lastSearchNotice = "搜索未能完成，请重新搜索。";
                _lastSearchElapsedMs = Math.Max(1, elapsed.ElapsedMilliseconds);
                _isSearchRunning = false;
                _searchBrowser?.SetEmptyText(_lastSearchNotice);
                SearchStateChanged?.Invoke(this, EventArgs.Empty);
                return (_searchMatchCount, false);
            }
            finally
            {
                viewPrepared.TrySetResult(false);
                fileBudget.TrySetResult(0);
                if (ReferenceEquals(_searchCts, searchCts))
                    _searchCts = null;
                if (!workers.IsCompleted)
                    searchCts.Cancel();
                // A canceled native scan may still own token registrations until its worker exits.
                _ = workers.ContinueWith(t => { _ = t.Exception; searchCts.Dispose(); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        private async Task<(SearchExecutionReport Report, bool Omitted)> SearchFileBatchesAsync(
            string query, string? root, bool includeHidden, int limit, int earlyBudget, Task<int> finalBudget,
            Task<bool> viewPrepared, int generation, CancellationToken token)
        {
            using var binder = new PidlManager.FastPidlBinder();
            int filesAdded = 0;
            bool omitted = false;
            SearchExecutionReport report = await FileSearchService.SearchAsync(query, root, includeHidden, limit, (entries, batchIndex) =>
            {
                if (!viewPrepared.GetAwaiter().GetResult() || token.IsCancellationRequested)
                    return;
                int available = finalBudget.IsCompleted ? finalBudget.GetAwaiter().GetResult() : earlyBudget;
                int first = Math.Min(entries.Length, Math.Max(0, available - filesAdded));
                filesAdded += Deliver(0, first);
                if (first < entries.Length)
                {
                    int remaining = Math.Max(0, finalBudget.GetAwaiter().GetResult() - filesAdded);
                    int tail = Math.Min(entries.Length - first, remaining);
                    filesAdded += Deliver(first, tail);
                    omitted |= first + tail < entries.Length;
                }

                int Deliver(int start, int count)
                {
                    if (count == 0 || token.IsCancellationRequested) return 0;
                    var pidls = new IntPtr[count];
                    using var batch = new OwnedPidlBatch(pidls, count);
                    for (int i = 0; i < count && !token.IsCancellationRequested; i++)
                    {
                        SearchResultEntry item = entries[start + i];
                        pidls[i] = binder.CreatePidlFromMetadata(item.FullPath, item.FileAttributes, item.FileSize, item.LastWriteTime);
                    }
                    if (token.IsCancellationRequested) return 0;
                    var dispatched = DispatchSearchPidlBatchAsync(batch, batchIndex, generation, token).GetAwaiter().GetResult();
                    omitted |= dispatched.Omitted;
                    return dispatched.Added;
                }
            }, token).ConfigureAwait(false);
            return (report, omitted);
        }

        private async Task<bool> WaitForResultsFolderReadyAsync(bool firstInit, int expectedGen, CancellationToken token, Stopwatch elapsed)
        {
            int maxIterations = firstInit ? 36 : 12;
            for (int waitIdx = 0; ; waitIdx++)
            {
                if (_isDestroyed || token.IsCancellationRequested || expectedGen != _searchGeneration)
                    return false;

                bool ready = IsResultsFolderReady();
                if (_isDestroyed || token.IsCancellationRequested || expectedGen != _searchGeneration)
                    return false;
                if (ready)
                {
                    _isFillSearchTransition = false;
                    return true;
                }

                if (waitIdx == maxIterations)
                    break;
                await Task.Delay(8).ConfigureAwait(true);
            }

            CancelSearchWorkerOnly();
            _isFillSearchTransition = false;
            _lastSearchElapsedMs = elapsed.ElapsedMilliseconds;
            _lastSearchNotice = "搜索结果视图未能及时就绪，请重新搜索。";
            _searchBrowser?.SetEmptyText(_lastSearchNotice);
            SearchStateChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }

        // The scanner owns the batch until the Dispatcher operation finishes, including cancellation or failure.
        private Task<(int Added, bool Omitted)> DispatchSearchPidlBatchAsync(
            OwnedPidlBatch batch, int batchIndex, int expectedGen, CancellationToken token)
        {
            var priority = batchIndex == 0 ? DispatcherPriority.Normal : DispatcherPriority.Background;
            return Dispatcher.InvokeAsync(() =>
            {
                if (_isDestroyed || token.IsCancellationRequested || expectedGen != _searchGeneration || !_isShowingSearchResults)
                    return (0, false);
                int count = Math.Min(batch.Count, Math.Max(0, _currentSearchMaxResults - _searchMatchCount));
                int added = AddOwnedPidlBatchToResultsFolder(batch, count, expectedGen);
                if (expectedGen != _searchGeneration) return (0, false);
                if (added > 0)
                {
                    _searchMatchCount += added;
                    InvalidateSelectionCache();
                    SearchStateChanged?.Invoke(this, EventArgs.Empty);
                }
                return (added, count < batch.Count);
            }, priority).Task;
        }

        private int AddOwnedPidlBatchToResultsFolder(OwnedPidlBatch batch, int count, int generation)
        {
            IntPtr[]? pidls = batch.RawArray;
            if (pidls == null || count <= 0)
                return 0;

            IResultsFolder? rf = AcquireResultsFolder(out IntPtr prf, out IFolderView2? fv2, out IntPtr pfv2);
            if (rf == null)
                throw new IOException("搜索结果视图不可用。");

            int added = 0;
            bool redrawSuspended = false;
            try
            {
                if (count >= 4 && fv2 != null)
                {
                    fv2.SetRedraw(false);
                    redrawSuspended = true;
                }

                for (int i = 0; i < count && generation == _searchGeneration; i++)
                {
                    IntPtr pidl = pidls[i];
                    if (pidl == IntPtr.Zero)
                        continue;

                    if (rf.AddIDList(pidl, out IntPtr addedChild) == ShellConstants.S_OK)
                    {
                        PidlManager.Free(ref addedChild);
                        added++;
                    }
                }
            }
            finally
            {
                if (redrawSuspended && fv2 != null)
                {
                    try { fv2.SetRedraw(true); } catch { }
                }

                Marshal.ReleaseComObject(rf);
                if (prf != IntPtr.Zero) Marshal.Release(prf);
                if (fv2 != null) Marshal.ReleaseComObject(fv2);
                if (pfv2 != IntPtr.Zero) Marshal.Release(pfv2);
            }

            return added;
        }

        private int AddPidlBatchToResultsFolder(List<byte[]> pidlBytesList, int generation)
        {
            if (pidlBytesList.Count == 0)
                return 0;

            IResultsFolder? rf = AcquireResultsFolder(out IntPtr prf, out IFolderView2? fv2, out IntPtr pfv2);
            if (rf == null)
                throw new IOException("搜索结果视图不可用。");

            int added = 0;
            bool redrawSuspended = false;
            try
            {
                if (pidlBytesList.Count >= 4 && fv2 != null)
                {
                    fv2.SetRedraw(false);
                    redrawSuspended = true;
                }

                foreach (byte[] raw in pidlBytesList)
                {
                    if (generation != _searchGeneration) break;
                    IntPtr pidl = PidlManager.FromBytes(raw);
                    if (pidl != IntPtr.Zero)
                    {
                        try
                        {
                            if (rf.AddIDList(pidl, out IntPtr addedChild) == ShellConstants.S_OK)
                            {
                                PidlManager.Free(ref addedChild);
                                added++;
                            }
                        }
                        finally
                        {
                            PidlManager.Free(ref pidl);
                        }
                    }
                }
            }
            finally
            {
                if (redrawSuspended && fv2 != null)
                {
                    try { fv2.SetRedraw(true); } catch { }
                }

                Marshal.ReleaseComObject(rf);
                if (prf != IntPtr.Zero) Marshal.Release(prf);
                if (fv2 != null) Marshal.ReleaseComObject(fv2);
                if (pfv2 != IntPtr.Zero) Marshal.Release(pfv2);
            }

            return added;
        }

        private bool IsResultsFolderReady()
        {
            if (SimulateSearchViewTimeoutForTest)
                return false;

            IResultsFolder? rf = AcquireResultsFolder(out IntPtr prf, out IFolderView2? fv2, out IntPtr pfv2);
            if (rf == null)
                return false;

            Marshal.ReleaseComObject(rf);
            if (prf != IntPtr.Zero) Marshal.Release(prf);
            if (fv2 != null) Marshal.ReleaseComObject(fv2);
            if (pfv2 != IntPtr.Zero) Marshal.Release(pfv2);
            return true;
        }

        private IResultsFolder? AcquireResultsFolder(out IntPtr prf, out IFolderView2? fv2, out IntPtr pfv2)
        {
            prf = IntPtr.Zero;
            fv2 = null;
            pfv2 = IntPtr.Zero;

            if (_isDestroyed || _searchBrowser == null)
                return null;

            Guid iidFv2 = ShellConstants.IID_IFolderView2;
            if (_searchBrowser.GetCurrentView(ref iidFv2, out pfv2) != ShellConstants.S_OK || pfv2 == IntPtr.Zero)
                return null;

            fv2 = (IFolderView2)Marshal.GetObjectForIUnknown(pfv2);
            try
            {
                Guid iidRf = ShellConstants.IID_IResultsFolder;
                if (fv2.GetFolder(ref iidRf, out prf) == ShellConstants.S_OK && prf != IntPtr.Zero)
                {
                    return (IResultsFolder)Marshal.GetObjectForIUnknown(prf);
                }

                Marshal.ReleaseComObject(fv2);
                fv2 = null;
                Marshal.Release(pfv2);
                pfv2 = IntPtr.Zero;
                return null;
            }
            catch
            {
                if (fv2 != null)
                {
                    Marshal.ReleaseComObject(fv2);
                    fv2 = null;
                }
                if (pfv2 != IntPtr.Zero)
                {
                    Marshal.Release(pfv2);
                    pfv2 = IntPtr.Zero;
                }
                return null;
            }
        }

        internal static (List<byte[]> Items, bool Truncated) CollectMatchingVirtualChildren(
            IntPtr folderPidl,
            string query,
            int maxItems = 200,
            CancellationToken cancellationToken = default)
        {
            var matched = new List<byte[]>();
            bool truncated = false;
            if (folderPidl == IntPtr.Zero)
                throw new IOException("无法取得虚拟目录。");

            cancellationToken.ThrowIfCancellationRequested();
            FastFileSearchService.ParsedSearchQuery parsedQuery = FastFileSearchService.ParseQuery(query);
            if (parsedQuery.HasUnsupportedNativeSyntax)
                throw new NotSupportedException("虚拟目录不支持此搜索条件。");
            Guid iidShellItem = ShellConstants.IID_IShellItem;
            Marshal.ThrowExceptionForHR(NativeMethods.SHCreateItemFromIDList(folderPidl, ref iidShellItem, out IShellItem? folderItem), new IntPtr(-1));
            if (folderItem == null)
                throw new IOException("无法取得虚拟目录。");

            IntPtr pSf = IntPtr.Zero;
            IShellFolder? sf = null;
            IntPtr pEnum = IntPtr.Zero;
            IEnumIDList? enumList = null;

            try
            {
                Guid bhid = new Guid("3981e224-f559-11d3-8e3a-00c04f6837d5");
                Guid iidSf = ShellConstants.IID_IShellFolder;
                Marshal.ThrowExceptionForHR(folderItem.BindToHandler(IntPtr.Zero, ref bhid, ref iidSf, out pSf), new IntPtr(-1));
                if (pSf == IntPtr.Zero)
                    throw new IOException("无法打开虚拟目录。");

                sf = (IShellFolder)Marshal.GetObjectForIUnknown(pSf);
                uint flags = ShellConstants.SHCONTF_FOLDERS | ShellConstants.SHCONTF_NONFOLDERS;
                int hr = sf.EnumObjects(IntPtr.Zero, flags, out pEnum);
                Marshal.ThrowExceptionForHR(hr, new IntPtr(-1));
                if (hr == ShellConstants.S_FALSE)
                    return (matched, false);
                if (pEnum == IntPtr.Zero)
                    throw new IOException("无法枚举虚拟目录。");

                enumList = (IEnumIDList)Marshal.GetObjectForIUnknown(pEnum);
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IntPtr childRel = PidlManager.NextChildPidl(enumList);
                    if (childRel == IntPtr.Zero)
                        break;
                    IntPtr fullChild = IntPtr.Zero;
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (NativeMethods.SHCreateItemWithParent(folderPidl, sf, childRel, ref iidShellItem, out IShellItem? item) == ShellConstants.S_OK && item != null)
                        {
                            try
                            {
                                string? name = PidlManager.GetShellItemString(item, SIGDN.SIGDN_NORMALDISPLAY);
                                if (!string.IsNullOrEmpty(name) && MatchesVirtualSearchItem(item, name!, parsedQuery))
                                {
                                    if (matched.Count >= maxItems)
                                    {
                                        truncated = true;
                                        break;
                                    }

                                    fullChild = PidlManager.Combine(folderPidl, childRel);
                                    if (fullChild != IntPtr.Zero)
                                        matched.Add(PidlManager.ToBytes(fullChild));
                                }
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(item);
                            }
                        }
                    }
                    finally
                    {
                        PidlManager.Free(ref fullChild);
                        PidlManager.Free(ref childRel);
                    }
                }
            }
            finally
            {
                if (enumList != null) Marshal.ReleaseComObject(enumList);
                if (pEnum != IntPtr.Zero) Marshal.Release(pEnum);
                if (sf != null) Marshal.ReleaseComObject(sf);
                if (pSf != IntPtr.Zero) Marshal.Release(pSf);
                Marshal.ReleaseComObject(folderItem);
            }

            return (matched, truncated);
        }

        private static bool MatchesVirtualSearchItem(IShellItem item, string displayName,
            FastFileSearchService.ParsedSearchQuery query)
        {
            uint mask = ShellConstants.SFGAO_FOLDER | ShellConstants.SFGAO_STREAM;
            if (item.GetAttributes(mask, out uint attributes) < 0)
                return false;
            bool isFolder = (attributes & ShellConstants.SFGAO_FOLDER) != 0 &&
                            (attributes & ShellConstants.SFGAO_STREAM) == 0;

            // The friendly name can hide the extension. Keep friendly namespace
            // names (including Recycle Bin names), restoring an omitted extension
            // only when the filesystem leaf is otherwise the same name.
            string name = displayName;
            string? path = PidlManager.GetShellItemString(item, SIGDN.SIGDN_FILESYSPATH);
            if (!string.IsNullOrEmpty(path))
            {
                string leaf = Path.GetFileName(path);
                if (leaf.StartsWith(displayName + ".", StringComparison.OrdinalIgnoreCase))
                    name = leaf;
            }

            if (!query.MatchesPathAndKind(name, name, isFolder))
                return false;

            ulong size = 0;
            var modified = default(System.Runtime.InteropServices.ComTypes.FILETIME);
            bool hasSize = false;
            bool hasModified = false;
            if (query.RequiresMetadata && item is IShellItem2 properties)
            {
                PROPERTYKEY sizeKey = PropertyKeys.Size;
                PROPERTYKEY modifiedKey = PropertyKeys.DateModified;
                hasSize = properties.GetUInt64(ref sizeKey, out size) >= 0;
                hasModified = properties.GetFileTime(ref modifiedKey, out modified) >= 0;
            }
            var entry = new SearchResultEntry(path ?? name,
                isFolder ? NativeMethods.FILE_ATTRIBUTE_DIRECTORY : NativeMethods.FILE_ATTRIBUTE_NORMAL,
                size, modified, hasSize, hasModified);
            return query.MatchesEntry(name, name, in entry);
        }

        public bool ExitSearchMode()
        {
            SearchRequestVersion++;
            return ExitSearchModeInternal(notifyChanged: true);
        }

        private bool ExitSearchModeInternal(bool notifyChanged)
        {
            if (_isDestroyed || _browser == null || !_isShowingSearchResults)
                return false;

            CancelSearchStateInternal(freeSavedPidl: true);
            HideSearchPresentation();
            _searchBrowser?.RemoveAll();
            _searchBrowser?.SetEmptyText(string.Empty);

            SyncBrowserRect();
            AttachDirectUiSubclassIfNeeded();
            InvalidateSelectionCache();

            if (notifyChanged)
                SearchStateChanged?.Invoke(this, EventArgs.Empty);

            return true;
        }

        private void CancelSearchWorkerOnly()
        {
            _searchGeneration++;
            CancellationTokenSource? active = _searchCts;
            _searchCts = null;
            if (active != null)
            {
                // 仅触发取消而不立即 Dispose，由 StartSearchAsync 在后台任务收敛后释放，避免 Register 抛 ObjectDisposedException
                try { active.Cancel(); } catch { }
            }
            _isSearchRunning = false;
        }

        private void EnsureSearchColumnsConfigured()
        {
            if (_isDestroyed || _searchBrowser == null)
                return;

            Guid iidCm = ShellConstants.IID_IColumnManager;
            if (_searchBrowser.GetCurrentView(ref iidCm, out IntPtr pcm) != ShellConstants.S_OK || pcm == IntPtr.Zero)
                return;

            var cm = (IColumnManager)Marshal.GetObjectForIUnknown(pcm);
            try
            {
                if (cm.GetColumnCount(CM_ENUM_FLAGS.CM_ENUM_VISIBLE, out uint visCount) != ShellConstants.S_OK || visCount == 0 || visCount > 64)
                    return;

                var visibleCols = new PROPERTYKEY[visCount];
                if (cm.GetColumns(CM_ENUM_FLAGS.CM_ENUM_VISIBLE, visibleCols, visCount) != ShellConstants.S_OK)
                    return;

                PROPERTYKEY folderPathKey = PropertyKeys.ItemFolderPathDisplay;
                bool alreadyVisible = false;
                int nameIndex = -1;
                for (int i = 0; i < visibleCols.Length; i++)
                {
                    if (visibleCols[i].Equals(folderPathKey))
                    {
                        alreadyVisible = true;
                        break;
                    }
                    if (visibleCols[i].Equals(PropertyKeys.ItemNameDisplay))
                    {
                        nameIndex = i;
                    }
                }

                if (!alreadyVisible)
                {
                    var updated = new List<PROPERTYKEY>((int)visCount + 1);
                    int insertAt = nameIndex >= 0 ? (nameIndex + 1) : 1;
                    insertAt = Math.Min(insertAt, visibleCols.Length);

                    for (int i = 0; i < insertAt; i++)
                        updated.Add(visibleCols[i]);
                    updated.Add(folderPathKey);
                    for (int i = insertAt; i < visibleCols.Length; i++)
                        updated.Add(visibleCols[i]);

                    cm.SetColumns(updated.ToArray(), (uint)updated.Count);

                    var colInfo = new CM_COLUMNINFO
                    {
                        cbSize = (uint)Marshal.SizeOf<CM_COLUMNINFO>(),
                        dwMask = CM_MASK.CM_MASK_WIDTH | CM_MASK.CM_MASK_STATE,
                        dwState = CM_STATE.CM_STATE_VISIBLE,
                        uWidth = 260,
                        wszName = string.Empty
                    };
                    cm.SetColumnInfo(ref folderPathKey, ref colInfo);
                }
            }
            catch
            {
            }
            finally
            {
                Marshal.ReleaseComObject(cm);
                Marshal.Release(pcm);
            }
        }

        internal bool IsSearchFolderPathColumnVisible()
        {
            if (_isDestroyed || _searchBrowser == null)
                return false;

            Guid iidCm = ShellConstants.IID_IColumnManager;
            if (_searchBrowser.GetCurrentView(ref iidCm, out IntPtr pcm) != ShellConstants.S_OK || pcm == IntPtr.Zero)
                return false;

            var cm = (IColumnManager)Marshal.GetObjectForIUnknown(pcm);
            try
            {
                if (cm.GetColumnCount(CM_ENUM_FLAGS.CM_ENUM_VISIBLE, out uint visCount) != ShellConstants.S_OK || visCount == 0 || visCount > 64)
                    return false;

                var visibleCols = new PROPERTYKEY[visCount];
                if (cm.GetColumns(CM_ENUM_FLAGS.CM_ENUM_VISIBLE, visibleCols, visCount) != ShellConstants.S_OK)
                    return false;

                for (int i = 0; i < visibleCols.Length; i++)
                {
                    if (visibleCols[i].Equals(PropertyKeys.ItemFolderPathDisplay))
                        return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                Marshal.ReleaseComObject(cm);
                Marshal.Release(pcm);
            }
        }

        private void CancelSearchStateInternal(bool freeSavedPidl)
        {
            CancelSearchWorkerOnly();
            _isShowingSearchResults = false;
            _isFillSearchTransition = false;
            _isSearchTruncated = false;
            _searchMatchCount = 0;
            _currentSearchMaxResults = DefaultMaxSearchResults;
            _lastSearchQuery = string.Empty;
            _lastSearchRootPath = null;
            _lastSearchNotice = null;
            _lastSearchBackend = SearchBackendKind.None;
            _lastSearchElapsedMs = 0;

            if (freeSavedPidl)
            {
                PidlManager.Free(ref _preSearchFolderPidl);
            }
        }

        private sealed class SearchBrowserEventSink : IExplorerBrowserEvents
        {
            private const int E_ABORT = unchecked((int)0x80004004);
            private WeakReference<ExplorerBrowserHost>? _ownerRef;

            public SearchBrowserEventSink(ExplorerBrowserHost owner) =>
                _ownerRef = new WeakReference<ExplorerBrowserHost>(owner);

            public void Detach() => _ownerRef = null;

            public int OnNavigationPending(IntPtr pidlFolder)
            {
                if (_ownerRef == null || !_ownerRef.TryGetTarget(out ExplorerBrowserHost? owner))
                    return ShellConstants.S_OK;

                // Allow initial FillFromObject(null, 0) ResultsFolder creation
                if (owner._isFillSearchTransition)
                    return ShellConstants.S_OK;

                if (owner._searchResultsPidl == IntPtr.Zero || PidlManager.AreEqual(pidlFolder, owner._searchResultsPidl))
                    return E_ABORT;

                // Keep the result container in place if the Shell requests a
                // folder navigation; route that folder to the primary browser.
                if (pidlFolder != IntPtr.Zero)
                {
                    IntPtr cloned = PidlManager.Clone(pidlFolder);
                    if (cloned != IntPtr.Zero)
                    {
                        int generation = owner._searchGeneration;
                        owner.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                if (!owner._isDestroyed && owner._isShowingSearchResults &&
                                    generation == owner._searchGeneration)
                                    owner.NavigateToPidl(cloned);
                            }
                            finally
                            {
                                PidlManager.Free(ref cloned);
                            }
                        }));
                    }
                }

                return E_ABORT;
            }

            public int OnViewCreated(IntPtr psv)
            {
                if (_ownerRef != null && _ownerRef.TryGetTarget(out ExplorerBrowserHost? owner))
                    owner.OnNativeViewCreated();
                return ShellConstants.S_OK;
            }

            public int OnNavigationComplete(IntPtr pidlFolder)
            {
                if (_ownerRef != null && _ownerRef.TryGetTarget(out ExplorerBrowserHost? owner))
                {
                    PidlManager.Free(ref owner._searchResultsPidl);
                    owner._searchResultsPidl = PidlManager.Clone(pidlFolder);
                    owner._isFillSearchTransition = false;
                    owner.OnNativeViewCreated();
                }
                return ShellConstants.S_OK;
            }

            public int OnNavigationFailed(IntPtr pidlFolder) => ShellConstants.S_OK;
        }
    }
}
