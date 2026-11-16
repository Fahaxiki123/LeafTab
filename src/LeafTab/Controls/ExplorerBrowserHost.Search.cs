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

        private bool EnsureSearchBrowserReady()
        {
            if (_isDestroyed || _hostHwnd == IntPtr.Zero)
                return false;

            if (_searchBrowser != null && _searchHostHwnd != IntPtr.Zero)
            {
                _searchBrowser.RemoveAll();
                return true;
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

        public async Task<int> StartSearchAsync(
            string rawQuery,
            IntPtr currentFolderPidl,
            string? folderFileSystemPath,
            int maxResults = DefaultMaxSearchResults)
        {
            if (_isDestroyed || _browser == null)
                return 0;

            string query = (rawQuery ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(query))
            {
                ExitSearchMode();
                return 0;
            }

            int effectiveMaxResults = Math.Max(1, Math.Min(MaxLoadMoreSearchResults, maxResults));
            Stopwatch uiSw = Stopwatch.StartNew();

            if (!_isShowingSearchResults && currentFolderPidl != IntPtr.Zero)
            {
                PidlManager.Free(ref _preSearchFolderPidl);
                _preSearchFolderPidl = PidlManager.Clone(currentFolderPidl);
            }

            _lastSearchQuery = query;
            _lastSearchRootPath = folderFileSystemPath;
            _currentSearchMaxResults = effectiveMaxResults;
            _lastSearchNotice = null;

            bool hasRealFsRoot = !string.IsNullOrWhiteSpace(folderFileSystemPath) && Directory.Exists(folderFileSystemPath);
            IntPtr sourceFolder = _preSearchFolderPidl != IntPtr.Zero ? _preSearchFolderPidl : currentFolderPidl;
            bool isThisPcMode = !hasRealFsRoot && PidlManager.IsThisPcPidl(sourceFolder);

            // Snapshot virtual children BEFORE switching view when not in a real filesystem directory
            List<byte[]>? virtualCandidates = null;
            bool virtualTruncated = false;
            if (!hasRealFsRoot)
            {
                int virtualCap = isThisPcMode ? 64 : Math.Min(effectiveMaxResults, 1000);
                (virtualCandidates, virtualTruncated) = CollectMatchingVirtualChildren(sourceFolder, query, virtualCap);
            }

            CancelSearchWorkerOnly();
            _searchCts = new CancellationTokenSource();
            CancellationToken token = _searchCts.Token;
            int myGen = ++_searchGeneration;

            bool firstInit = _searchBrowser == null;
            if (!EnsureSearchBrowserReady() || _searchBrowser == null)
            {
                _isSearchRunning = false;
                _isSearchTruncated = false;
                SearchStateChanged?.Invoke(this, EventArgs.Empty);
                return 0;
            }

            _isShowingSearchResults = true;
            ShowSearchPresentation();
            _isSearchRunning = true;
            _isSearchTruncated = false;
            _searchMatchCount = 0;
            _lastSearchBackend = SearchBackendKind.None;
            _lastSearchElapsedMs = 0;
            InvalidateSelectionCache();
            _searchBrowser.SetEmptyText($"正在搜索“{query}”…");
            SearchStateChanged?.Invoke(this, EventArgs.Empty);

            // Pure virtual folder (e.g. Recycle Bin, Home): wait briefly for ResultsFolder and insert snapshotted items
            if (!hasRealFsRoot && !isThisPcMode)
            {
                bool virtualReady = await WaitForResultsFolderReadyAsync(firstInit, myGen, token).ConfigureAwait(true);
                _isFillSearchTransition = false;
                if (!virtualReady || _isDestroyed || token.IsCancellationRequested || myGen != _searchGeneration)
                    return 0;

                AttachDirectUiSubclassIfNeeded();
                TrySetViewStyle(ExplorerViewStyle.Details);
                EnsureSearchColumnsConfigured();

                int added = AddPidlBatchToResultsFolder(virtualCandidates ?? new List<byte[]>());
                uiSw.Stop();
                _searchMatchCount = added;
                _isSearchTruncated = virtualTruncated;
                _lastSearchBackend = SearchBackendKind.VirtualFolder;
                _lastSearchElapsedMs = uiSw.ElapsedMilliseconds;
                _isSearchRunning = false;
                _searchBrowser.SetEmptyText(added == 0 ? "没有与搜索条件匹配的项。" : string.Empty);
                InvalidateSelectionCache();
                SearchStateChanged?.Invoke(this, EventArgs.Empty);
                return added;
            }

            // Filesystem directory OR '此电脑' full-system search:
            // Run ResultsFolder readiness wait CONCURRENTLY with Everything IPC / multi-threaded Win32 scan + background PIDL construction.
            string? searchRoot = hasRealFsRoot ? folderFileSystemPath : null;
            (_, bool includeHidden) = PidlManager.GetShellVisibilityFlags();
            int remainingFsBudget = effectiveMaxResults;
            if (isThisPcMode && virtualCandidates != null && virtualCandidates.Count > 0)
            {
                remainingFsBudget = Math.Max(1, effectiveMaxResults - virtualCandidates.Count);
            }

            var viewPreparedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var binderLock = new object();

            Task<SearchExecutionReport> searchTask = Task.Run(async () =>
            {
                using var pidlBinder = new PidlManager.FastPidlBinder();

                try
                {
                    return await FastFileSearchService.Shared.SearchAsync(
                        query,
                        searchRoot,
                        includeHidden,
                        remainingFsBudget,
                        (entries, batchIndex) =>
                        {
                            if (token.IsCancellationRequested || _isDestroyed)
                                return;

                            IntPtr[] pidls = new IntPtr[entries.Length];
                            int validCount = 0;
                            try
                            {
                                lock (binderLock)
                                {
                                    for (int i = 0; i < entries.Length; i++)
                                    {
                                        if (token.IsCancellationRequested)
                                            break;

                                        SearchResultEntry entry = entries[i];
                                        IntPtr pidl = pidlBinder.CreatePidlFromMetadata(
                                            entry.FullPath,
                                            entry.FileAttributes,
                                            entry.FileSize,
                                            entry.LastWriteTime);

                                        if (pidl != IntPtr.Zero)
                                        {
                                            pidls[validCount++] = pidl;
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                for (int i = 0; i < validCount; i++)
                                {
                                    if (pidls[i] != IntPtr.Zero)
                                    {
                                        NativeMethods.ILFree(pidls[i]);
                                        pidls[i] = IntPtr.Zero;
                                    }
                                }
                                return;
                            }

                            if (validCount == 0)
                                return;

                            var ownedBatch = new OwnedPidlBatch(pidls, validCount);
                            bool viewReady = viewPreparedTcs.Task.GetAwaiter().GetResult();
                            if (!viewReady || token.IsCancellationRequested || _isDestroyed)
                            {
                                ownedBatch.Dispose();
                                return;
                            }

                            try
                            {
                                DispatchSearchPidlBatchAsync(ownedBatch, batchIndex, myGen, token).GetAwaiter().GetResult();
                            }
                            catch
                            {
                                ownedBatch.Dispose();
                            }
                        },
                        token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return new SearchExecutionReport(SearchBackendKind.None, 0, false, 0);
                }
            }, token);

            try
            {
                bool ready = await WaitForResultsFolderReadyAsync(firstInit, myGen, token).ConfigureAwait(true);
                _isFillSearchTransition = false;
                if (!ready || _isDestroyed || token.IsCancellationRequested || myGen != _searchGeneration)
                {
                    viewPreparedTcs.TrySetResult(false);
                    return 0;
                }

                AttachDirectUiSubclassIfNeeded();
                TrySetViewStyle(ExplorerViewStyle.Details);
                EnsureSearchColumnsConfigured();

                // In '此电脑' mode, immediately surface matching drives/virtual items before or alongside file matches
                if (isThisPcMode && virtualCandidates != null && virtualCandidates.Count > 0)
                {
                    int virtualAdded = AddPidlBatchToResultsFolder(virtualCandidates);
                    if (virtualAdded > 0)
                    {
                        _searchMatchCount += virtualAdded;
                        InvalidateSelectionCache();
                        SearchStateChanged?.Invoke(this, EventArgs.Empty);
                    }
                }

                viewPreparedTcs.TrySetResult(true);

                SearchExecutionReport report = await searchTask.ConfigureAwait(true);

                if (!_isDestroyed && myGen == _searchGeneration && !token.IsCancellationRequested)
                {
                    uiSw.Stop();
                    _isSearchTruncated = report.IsTruncated || (isThisPcMode && virtualTruncated);
                    _lastSearchBackend = report.Backend;
                    _lastSearchElapsedMs = Math.Max(1, uiSw.ElapsedMilliseconds);
                    _lastSearchNotice = report.NoticeMessage;
                    _isSearchRunning = false;
                    string emptyText = !string.IsNullOrWhiteSpace(report.NoticeMessage)
                        ? report.NoticeMessage!
                        : "没有与搜索条件匹配的项。";
                    _searchBrowser?.SetEmptyText(_searchMatchCount == 0 ? emptyText : string.Empty);
                    InvalidateSelectionCache();
                    SearchStateChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (OperationCanceledException)
            {
                viewPreparedTcs.TrySetResult(false);
            }
            finally
            {
                viewPreparedTcs.TrySetResult(false);
            }

            return _searchMatchCount;
        }

        private async Task<bool> WaitForResultsFolderReadyAsync(bool firstInit, int expectedGen, CancellationToken token)
        {
            int maxIterations = firstInit ? 36 : 12;
            for (int waitIdx = 0; waitIdx < maxIterations; waitIdx++)
            {
                if (_isDestroyed || token.IsCancellationRequested || expectedGen != _searchGeneration)
                    return false;

                if (IsResultsFolderReady())
                    return true;

                await Task.Delay(8).ConfigureAwait(true);
            }
            return !_isDestroyed && !token.IsCancellationRequested && expectedGen == _searchGeneration && IsResultsFolderReady();
        }

        private Task DispatchSearchPidlBatchAsync(OwnedPidlBatch batch, int batchIndex, int expectedGen, CancellationToken token)
        {
            DispatcherPriority priority = batchIndex == 0 ? DispatcherPriority.Normal : DispatcherPriority.Background;
            bool queued = false;
            try
            {
                Task uiTask = Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        if (_isDestroyed || token.IsCancellationRequested || expectedGen != _searchGeneration || !_isShowingSearchResults)
                            return;

                        int added = AddOwnedPidlBatchToResultsFolder(batch);
                        if (added > 0)
                        {
                            _searchMatchCount += added;
                            InvalidateSelectionCache();
                            SearchStateChanged?.Invoke(this, EventArgs.Empty);
                        }
                    }
                    finally
                    {
                        batch.Dispose();
                    }
                }, priority).Task;
                queued = true;

                // Ensure PIDLs are guaranteed freed even if the Dispatcher cancels a Background item during shutdown
                return uiTask.ContinueWith(
                    _ => batch.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            finally
            {
                if (!queued)
                    batch.Dispose();
            }
        }

        private int AddOwnedPidlBatchToResultsFolder(OwnedPidlBatch batch)
        {
            IntPtr[]? pidls = batch.RawArray;
            int count = batch.Count;
            if (pidls == null || count <= 0)
                return 0;

            IResultsFolder? rf = AcquireResultsFolder(out IntPtr prf, out IFolderView2? fv2, out IntPtr pfv2);
            if (rf == null)
                return 0;

            int added = 0;
            bool redrawSuspended = false;
            try
            {
                if (count >= 4 && fv2 != null)
                {
                    fv2.SetRedraw(false);
                    redrawSuspended = true;
                }

                for (int i = 0; i < count; i++)
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

        private int AddPidlBatchToResultsFolder(List<byte[]> pidlBytesList)
        {
            if (pidlBytesList.Count == 0)
                return 0;

            IResultsFolder? rf = AcquireResultsFolder(out IntPtr prf, out IFolderView2? fv2, out IntPtr pfv2);
            if (rf == null)
                return 0;

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

        private static (List<byte[]> Items, bool Truncated) CollectMatchingVirtualChildren(IntPtr folderPidl, string query, int maxItems = 200)
        {
            var matched = new List<byte[]>();
            bool truncated = false;
            if (folderPidl == IntPtr.Zero)
                return (matched, false);

            Func<string, bool> matcher = BuildFileNameMatcher(query);
            Guid iidShellItem = ShellConstants.IID_IShellItem;
            if (NativeMethods.SHCreateItemFromIDList(folderPidl, ref iidShellItem, out IShellItem? folderItem) != ShellConstants.S_OK || folderItem == null)
                return (matched, false);

            IntPtr pSf = IntPtr.Zero;
            IShellFolder? sf = null;
            IntPtr pEnum = IntPtr.Zero;
            IEnumIDList? enumList = null;

            try
            {
                Guid bhid = new Guid("3981e224-f559-11d3-8e3a-00c04f6837d5");
                Guid iidSf = ShellConstants.IID_IShellFolder;
                if (folderItem.BindToHandler(IntPtr.Zero, ref bhid, ref iidSf, out pSf) != ShellConstants.S_OK || pSf == IntPtr.Zero)
                    return (matched, false);

                sf = (IShellFolder)Marshal.GetObjectForIUnknown(pSf);
                uint flags = ShellConstants.SHCONTF_FOLDERS | ShellConstants.SHCONTF_NONFOLDERS;
                if (sf.EnumObjects(IntPtr.Zero, flags, out pEnum) != ShellConstants.S_OK || pEnum == IntPtr.Zero)
                    return (matched, false);

                enumList = (IEnumIDList)Marshal.GetObjectForIUnknown(pEnum);
                while (enumList.Next(1, out IntPtr childRel, out uint fetched) == ShellConstants.S_OK && fetched == 1 && childRel != IntPtr.Zero)
                {
                    IntPtr fullChild = IntPtr.Zero;
                    try
                    {
                        if (NativeMethods.SHCreateItemWithParent(folderPidl, sf, childRel, ref iidShellItem, out IShellItem? item) == ShellConstants.S_OK && item != null)
                        {
                            try
                            {
                                string? name = PidlManager.GetShellItemString(item, SIGDN.SIGDN_NORMALDISPLAY);
                                if (!string.IsNullOrEmpty(name) && matcher(name!))
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
            catch
            {
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

        private static Func<string, bool> BuildFileNameMatcher(string query)
        {
            Func<string, string, bool> matcher = FastFileSearchService.BuildEntryMatcher(query);
            return name => matcher(name, name);
        }

        public bool ExitSearchMode() => ExitSearchModeInternal(notifyChanged: true);

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
            if (_searchCts != null)
            {
                try { _searchCts.Cancel(); } catch { }
                _searchCts.Dispose();
                _searchCts = null;
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

                if (PidlManager.AreEqual(pidlFolder, owner._searchResultsPidl))
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
