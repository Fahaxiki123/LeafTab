using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using LeafTab.Interop;

namespace LeafTab.Controls
{
    public enum ExplorerViewStyle
    {
        ExtraLargeIcons,
        LargeIcons,
        MediumIcons,
        SmallIcons,
        List,
        Details,
        Tiles,
        Content
    }

    public enum NewItemKind
    {
        Folder,
        TextDocument,
        MarkdownDocument,
        ZipArchive
    }

    public readonly struct SelectionStatusInfo
    {
        public int TotalItems { get; }
        public int SelectedItems { get; }
        public ulong SelectedSizeBytes { get; }
        public bool HasSizedFileSelection { get; }

        public SelectionStatusInfo(int totalItems, int selectedItems, ulong selectedSizeBytes, bool hasSizedFileSelection)
        {
            TotalItems = totalItems;
            SelectedItems = selectedItems;
            SelectedSizeBytes = selectedSizeBytes;
            HasSizedFileSelection = hasSizedFileSelection;
        }
    }

    public sealed class NavigationCompletedEventArgs : EventArgs
    {
        public IntPtr FolderPidl { get; }
        public NavigationCompletedEventArgs(IntPtr folderPidl) => FolderPidl = folderPidl;
    }

    public sealed class FolderMiddleClickedEventArgs : EventArgs
    {
        public IntPtr TargetFolderPidl { get; }
        public double UiaElapsedMs { get; }

        public FolderMiddleClickedEventArgs(IntPtr targetFolderPidl, double uiaElapsedMs)
        {
            TargetFolderPidl = targetFolderPidl;
            UiaElapsedMs = uiaElapsedMs;
        }
    }

    /// <summary>
    /// Hosts a native Windows IExplorerBrowser (with EBO_SHOWFRAMES navigation pane) inside WPF via HwndHost.
    /// Supports Phase 1 + Phase 2 capabilities:
    /// - Clean COM lifecycle (WeakReference event sink + Unadvise + SetSite(null) + Destroy + FinalReleaseComObject)
    /// - IShellView::TranslateAccelerator forwarding
    /// - Dedicated MTA UIA gesture detection (middle-click folder, double-click blank area)
    /// - Win11 Command Bar verbs (New item + inline rename, Cut/Copy/Paste/Delete/Rename/Properties, Sort, View styles, Preview pane)
    /// - Asynchronous streaming directory search into native IResultsFolder
    /// </summary>
    public sealed class ExplorerBrowserHost : HwndHost
    {
        private static readonly UIntPtr DirectUiSubclassId = new UIntPtr(0x4C454146); // 'LEAF'
        private const string WhiteHostWindowClassName = "LeafTabWhiteHostWnd";
        private static readonly object s_wndClassLock = new object();
        private static bool s_whiteHostClassRegistered;
        private static readonly WNDPROC s_whiteHostWndProc = WhiteHostWndProc;

        private static string EnsureWhiteHostWindowClass()
        {
            lock (s_wndClassLock)
            {
                if (s_whiteHostClassRegistered)
                    return WhiteHostWindowClassName;

                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    style = 0,
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_whiteHostWndProc),
                    hInstance = NativeMethods.GetModuleHandleW(null),
                    hbrBackground = NativeMethods.GetStockObject(NativeMethods.WHITE_BRUSH),
                    lpszClassName = WhiteHostWindowClassName
                };

                ushort atom = NativeMethods.RegisterClassExW(ref wc);
                if (atom != 0)
                {
                    s_whiteHostClassRegistered = true;
                    return WhiteHostWindowClassName;
                }

                return "Static";
            }
        }

        private static IntPtr WhiteHostWndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam)
        {
            return NativeMethods.DefWindowProcW(hWnd, uMsg, wParam, lParam);
        }

        private IntPtr _hostHwnd;
        private IntPtr _browseHostHwnd;
        private IExplorerBrowser? _browser;
        private BrowserEventSink? _eventSink;
        private ExplorerPaneVisibilitySite? _paneSite;
        private uint _adviseCookie;
        private IntPtr _pendingInitialPidl;

        // Lazily created secondary ResultsFolder browser (FillFromObject requires an un-navigated IExplorerBrowser)
        private IntPtr _searchHostHwnd;
        private IExplorerBrowser? _searchBrowser;
        private SearchBrowserEventSink? _searchEventSink;
        private uint _searchAdviseCookie;

        private IntPtr _subclassedDirectUiHwnd;
        private readonly SUBCLASSPROC _directUiSubclassDelegate;
        private int _viewGeneration;
        private bool _showPreviewPane;
        private bool _isMouseDownInView;
        private bool _isKeyboardFocusActive;
        private bool _isDestroyed;

        private IExplorerBrowser? ActiveBrowser => (_isShowingSearchResults && _searchBrowser != null) ? _searchBrowser : _browser;

        // Cached selection telemetry to avoid redundant COM allocations on idle timer ticks
        private int _cachedTotalItems = -1;
        private int _cachedSelectedItems = -1;
        private int _cachedFocusedItem = -2;
        private ulong _cachedSelectedBytes;
        private bool _cachedHasSizedSelection;
        private string? _cachedSingleSelectedPath;
        private string? _cachedSingleSelectedDisplayName;
        private bool _cachedSingleSelectedIsFolder;

        // Async directory search state
        private IntPtr _preSearchFolderPidl;
        private bool _isShowingSearchResults;
        private bool _isFillSearchTransition;
        private bool _isSearchRunning;
        private bool _isSearchTruncated;
        private int _searchMatchCount;
        private int _searchGeneration;
        private CancellationTokenSource? _searchCts;

        public IntPtr DirectUiHwnd => _subclassedDirectUiHwnd;
        public int ViewGeneration => _viewGeneration;
        public bool ShowPreviewPane => _showPreviewPane;
        public bool IsShowingSearchResults => _isShowingSearchResults;
        public bool IsSearchRunning => _isSearchRunning;
        public bool IsSearchTruncated => _isSearchTruncated;
        public int SearchMatchCount => _searchMatchCount;

        public event EventHandler<NavigationCompletedEventArgs>? NavigationCompleted;
        public event EventHandler<string>? NavigationFailed;
        public event EventHandler<FolderMiddleClickedEventArgs>? FolderMiddleClicked;
        public event EventHandler<double>? BlankAreaDoubleClicked;
        public event EventHandler? ViewInteractionOccurred;
        public event EventHandler? SearchStateChanged;

        private bool _forwardingWheel;

        public ExplorerBrowserHost(IntPtr initialPidl, bool showPreviewPane = false)
        {
            _pendingInitialPidl = PidlManager.Clone(initialPidl);
            _showPreviewPane = showPreviewPane;
            _directUiSubclassDelegate = DirectUiSubclassProc;
            Focusable = true;
        }

        protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // When keyboard focus is parked on _hostHwnd (0 items selected), ensure mouse wheel still scrolls DirectUIHWND
            // even on systems where Windows "Scroll inactive windows" is disabled.
            if (!_isDestroyed &&
                !_forwardingWheel &&
                (msg == 0x020A || msg == 0x020E) &&
                NativeMethods.GetFocus() == _hostHwnd &&
                _subclassedDirectUiHwnd != IntPtr.Zero &&
                NativeMethods.IsWindow(_subclassedDirectUiHwnd))
            {
                _forwardingWheel = true;
                try
                {
                    IntPtr res = NativeMethods.SendMessageW(_subclassedDirectUiHwnd, (uint)msg, wParam, lParam);
                    handled = true;
                    return res;
                }
                finally
                {
                    _forwardingWheel = false;
                }
            }

            return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
        }

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            int visibleFlag = Visibility == Visibility.Visible ? NativeMethods.WS_VISIBLE : 0;
            int initW = Math.Max(1, (int)ActualWidth);
            int initH = Math.Max(1, (int)ActualHeight);

            string hostCls = EnsureWhiteHostWindowClass();

            _hostHwnd = NativeMethods.CreateWindowExW(
                0,
                hostCls,
                string.Empty,
                NativeMethods.WS_CHILD | visibleFlag | NativeMethods.WS_CLIPCHILDREN | NativeMethods.WS_CLIPSIBLINGS,
                0,
                0,
                initW,
                initH,
                hwndParent.Handle,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            _browseHostHwnd = NativeMethods.CreateWindowExW(
                0,
                hostCls,
                string.Empty,
                NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_CLIPCHILDREN | NativeMethods.WS_CLIPSIBLINGS,
                0,
                0,
                initW,
                initH,
                _hostHwnd,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            InitializeExplorerBrowser();
            return new HandleRef(this, _hostHwnd);
        }

        private void InitializeExplorerBrowser()
        {
            Type? comType = Type.GetTypeFromCLSID(ShellConstants.CLSID_ExplorerBrowser, throwOnError: true);
            _browser = (IExplorerBrowser)Activator.CreateInstance(comType!)!;

            _paneSite ??= new ExplorerPaneVisibilitySite();
            if (_browser is IObjectWithSite withSite)
            {
                withSite.SetSite(_paneSite);
            }

            _browser.SetOptions(EXPLORER_BROWSER_OPTIONS.EBO_SHOWFRAMES | EXPLORER_BROWSER_OPTIONS.EBO_NOBORDER);

            NativeMethods.GetClientRect(_browseHostHwnd, out RECT rc);
            if (rc.Width <= 0) rc.Right = 800;
            if (rc.Height <= 0) rc.Bottom = 500;


            var fs = new FOLDERSETTINGS
            {
                ViewMode = FOLDERVIEWMODE.FVM_AUTO,
                fFlags = FOLDERFLAGS.FWF_AUTOARRANGE
            };

            int hr = _browser.Initialize(_browseHostHwnd, ref rc, ref fs);
            if (hr != ShellConstants.S_OK)
            {
                throw new COMException("Failed to initialize IExplorerBrowser.", hr);
            }

            _browser.SetPropertyBag("LeafTab.BrowserBag.v1");

            _eventSink = new BrowserEventSink(this);
            if (_browser.Advise(_eventSink, out uint cookie) == ShellConstants.S_OK)
            {
                _adviseCookie = cookie;
            }

            if (_pendingInitialPidl != IntPtr.Zero)
            {
                IntPtr target = _pendingInitialPidl;
                _pendingInitialPidl = IntPtr.Zero;
                try
                {
                    NavigateToPidl(target);
                }
                finally
                {
                    PidlManager.Free(ref target);
                }
            }
        }

        private int _innerStatusBarClipPx = -1;

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            _innerStatusBarClipPx = -1;
            SyncBrowserRect();
        }

        protected override void OnWindowPositionChanged(Rect rcBoundingBox)
        {
            base.OnWindowPositionChanged(rcBoundingBox);
            SyncBrowserRect();
        }

        public void SyncBrowserRect()
        {
            if (_isDestroyed || _browser == null || _hostHwnd == IntPtr.Zero)
                return;

            if (!NativeMethods.GetClientRect(_hostHwnd, out RECT rc) || rc.Width <= 0 || rc.Height <= 0)
                return;

            if (_browseHostHwnd != IntPtr.Zero)
            {
                NativeMethods.MoveWindow(_browseHostHwnd, 0, 0, rc.Width, rc.Height, true);
            }

            if (_searchHostHwnd != IntPtr.Zero)
            {
                NativeMethods.MoveWindow(_searchHostHwnd, 0, 0, rc.Width, rc.Height, true);
            }

            if (_innerStatusBarClipPx < 0 && !_isShowingSearchResults)
            {
                IntPtr hdwpProbe = IntPtr.Zero;
                _browser.SetRect(ref hdwpProbe, rc);

                IntPtr svHwnd = GetShellViewWindowHandleFor(_browser);
                if (svHwnd != IntPtr.Zero &&
                    NativeMethods.GetWindowRect(_browseHostHwnd, out RECT hostScreen) &&
                    NativeMethods.GetWindowRect(svHwnd, out RECT svScreen) &&
                    svScreen.Height > 40)
                {
                    int bottomGap = hostScreen.Bottom - svScreen.Bottom;
                    _innerStatusBarClipPx = (bottomGap > 0 && bottomGap <= 56) ? bottomGap : 0;
                }
            }

            RECT targetRect = _innerStatusBarClipPx > 0
                ? new RECT(0, 0, rc.Right, rc.Bottom + _innerStatusBarClipPx)
                : rc;

            if (_innerStatusBarClipPx >= 0)
            {
                IntPtr hdwp1 = IntPtr.Zero;
                _browser.SetRect(ref hdwp1, targetRect);
            }
            else
            {
                IntPtr hdwp1 = IntPtr.Zero;
                _browser.SetRect(ref hdwp1, rc);
            }

            if (_searchBrowser != null)
            {
                IntPtr hdwp2 = IntPtr.Zero;
                _searchBrowser.SetRect(ref hdwp2, targetRect);
            }
        }

        public void SetPreviewPaneVisible(bool visible)
        {
            if (_isDestroyed)
                return;

            _showPreviewPane = visible;
        }

        private void DestroyPrimaryBrowserOnly()
        {
            DetachDirectUiSubclass();

            if (_eventSink != null)
            {
                _eventSink.Detach();
                _eventSink = null;
            }

            if (_browser != null)
            {
                try
                {
                    if (_adviseCookie != 0)
                    {
                        _browser.Unadvise(_adviseCookie);
                        _adviseCookie = 0;
                    }
                    if (_browser is IObjectWithSite withSite)
                    {
                        withSite.SetSite(null);
                    }
                    _browser.Destroy();
                }
                catch
                {
                }
                finally
                {
                    Marshal.FinalReleaseComObject(_browser);
                    _browser = null;
                }
            }
        }

        internal int GetShellViewWidthPx()
        {
            IntPtr svHwnd = GetShellViewWindowHandleFor(ActiveBrowser);
            if (svHwnd != IntPtr.Zero && NativeMethods.GetWindowRect(svHwnd, out RECT rc))
                return rc.Width;
            return 0;
        }

        public bool NavigateToPidl(IntPtr pidl)
        {
            if (_isDestroyed || _browser == null || pidl == IntPtr.Zero)
                return false;

            if (_isShowingSearchResults)
            {
                ExitSearchModeInternal(notifyChanged: true);
            }
            else
            {
                CancelSearchStateInternal(freeSavedPidl: true);
            }

            InvalidateSelectionCache();

            int hr = _browser.BrowseToIDList(pidl, ShellConstants.SBSP_ABSOLUTE | ShellConstants.SBSP_SAMEBROWSER);
            return hr == ShellConstants.S_OK;
        }

        public bool NavigateBack()
        {
            if (_isDestroyed || _browser == null) return false;
            if (_isShowingSearchResults)
                return ExitSearchMode();

            return _browser.BrowseToIDList(IntPtr.Zero, ShellConstants.SBSP_NAVIGATEBACK) == ShellConstants.S_OK;
        }

        public bool NavigateForward()
        {
            if (_isDestroyed || _browser == null) return false;
            return _browser.BrowseToIDList(IntPtr.Zero, ShellConstants.SBSP_NAVIGATEFORWARD) == ShellConstants.S_OK;
        }

        public bool NavigateUp()
        {
            if (_isDestroyed || _browser == null) return false;
            if (_isShowingSearchResults)
                return ExitSearchMode();

            return _browser.BrowseToIDList(IntPtr.Zero, ShellConstants.SBSP_PARENT) == ShellConstants.S_OK;
        }

        public void RefreshView()
        {
            IExplorerBrowser? active = ActiveBrowser;
            if (_isDestroyed || active == null) return;
            InvalidateSelectionCache();

            Guid iidShellView = ShellConstants.IID_IShellView;
            if (active.GetCurrentView(ref iidShellView, out IntPtr psv) == ShellConstants.S_OK && psv != IntPtr.Zero)
            {
                var sv = (IShellView)Marshal.GetObjectForIUnknown(psv);
                try
                {
                    sv.Refresh();
                }
                finally
                {
                    Marshal.ReleaseComObject(sv);
                    Marshal.Release(psv);
                }
            }
        }

        public void InvalidateSelectionCache()
        {
            _cachedTotalItems = -1;
            _cachedSelectedItems = -1;
            _cachedFocusedItem = -2;
            _cachedSingleSelectedPath = null;
            _cachedSingleSelectedDisplayName = null;
            _cachedSingleSelectedIsFolder = false;
        }

        internal (string? FileSystemPath, string? DisplayName, bool IsFolder) GetCachedSingleSelection() =>
            (_cachedSingleSelectedPath, _cachedSingleSelectedDisplayName, _cachedSingleSelectedIsFolder);

        public (int TotalItems, int SelectedItems) GetItemCounts()
        {
            SelectionStatusInfo info = GetSelectionStatus(computeBytes: false);
            return (info.TotalItems, info.SelectedItems);
        }

        public SelectionStatusInfo GetSelectionStatus(bool computeBytes = true)
        {
            IExplorerBrowser? active = ActiveBrowser;
            if (_isDestroyed || active == null)
                return new SelectionStatusInfo(0, 0, 0, false);

            Guid iidFv2 = ShellConstants.IID_IFolderView2;
            if (active.GetCurrentView(ref iidFv2, out IntPtr pfv2) == ShellConstants.S_OK && pfv2 != IntPtr.Zero)
            {
                var fv2 = (IFolderView2)Marshal.GetObjectForIUnknown(pfv2);
                try
                {
                    fv2.ItemCount(ShellConstants.SVGIO_ALLVIEW, out int total);
                    fv2.ItemCount(ShellConstants.SVGIO_SELECTION, out int selected);
                    fv2.GetFocusedItem(out int focused);

                    total = Math.Max(0, total);
                    selected = Math.Max(0, selected);

                    if (!computeBytes)
                        return new SelectionStatusInfo(total, selected, _cachedSelectedBytes, _cachedHasSizedSelection);

                    bool missingSingleMeta = selected == 1 &&
                                             _cachedSingleSelectedPath == null &&
                                             _cachedSingleSelectedDisplayName == null;

                    if (total == _cachedTotalItems &&
                        selected == _cachedSelectedItems &&
                        focused == _cachedFocusedItem &&
                        !missingSingleMeta)
                    {
                        return new SelectionStatusInfo(total, selected, _cachedSelectedBytes, _cachedHasSizedSelection);
                    }

                    ulong sumBytes = 0;
                    bool hasSized = false;
                    string? singlePath = null;
                    string? singleDisplayName = null;
                    bool singleIsFolder = false;

                    if (selected > 0 && selected <= 400 &&
                        fv2.GetSelection(false, out IShellItemArray? selectionArray) == ShellConstants.S_OK &&
                        selectionArray != null)
                    {
                        try
                        {
                            if (selectionArray.GetCount(out uint count) == ShellConstants.S_OK)
                            {
                                PROPERTYKEY sizeKey = PropertyKeys.Size;
                                for (uint i = 0; i < count; i++)
                                {
                                    if (selectionArray.GetItemAt(i, out IShellItem? item) == ShellConstants.S_OK && item != null)
                                    {
                                        try
                                        {
                                            uint mask = ShellConstants.SFGAO_FOLDER | ShellConstants.SFGAO_STREAM;
                                            int attrHr = item.GetAttributes(mask, out uint attrs);
                                            bool isPureFolder = attrHr >= 0 &&
                                                                (attrs & ShellConstants.SFGAO_FOLDER) != 0 &&
                                                                (attrs & ShellConstants.SFGAO_STREAM) == 0;

                                            if (selected == 1 && i == 0)
                                            {
                                                singleIsFolder = isPureFolder;
                                                singlePath = PidlManager.GetShellItemString(item, SIGDN.SIGDN_FILESYSPATH);
                                                singleDisplayName = PidlManager.GetShellItemString(item, SIGDN.SIGDN_NORMALDISPLAY)
                                                                    ?? PidlManager.GetShellItemString(item, SIGDN.SIGDN_PARENTRELATIVEEDITING);
                                            }

                                            if (!isPureFolder && item is IShellItem2 item2 &&
                                                item2.GetUInt64(ref sizeKey, out ulong fileSize) == ShellConstants.S_OK)
                                            {
                                                sumBytes += fileSize;
                                                hasSized = true;
                                            }
                                        }
                                        finally
                                        {
                                            Marshal.ReleaseComObject(item);
                                        }
                                    }
                                }
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(selectionArray);
                        }
                    }

                    _cachedTotalItems = total;
                    _cachedSelectedItems = selected;
                    _cachedFocusedItem = focused;
                    _cachedSelectedBytes = sumBytes;
                    _cachedHasSizedSelection = hasSized;
                    _cachedSingleSelectedPath = singlePath;
                    _cachedSingleSelectedDisplayName = singleDisplayName;
                    _cachedSingleSelectedIsFolder = singleIsFolder;

                    return new SelectionStatusInfo(total, selected, sumBytes, hasSized);
                }
                finally
                {
                    Marshal.ReleaseComObject(fv2);
                    Marshal.Release(pfv2);
                }
            }

            Guid iidFolderView = ShellConstants.IID_IFolderView;
            if (active.GetCurrentView(ref iidFolderView, out IntPtr pfv) != ShellConstants.S_OK || pfv == IntPtr.Zero)
                return new SelectionStatusInfo(0, 0, 0, false);

            var fv = (IFolderView)Marshal.GetObjectForIUnknown(pfv);
            try
            {
                fv.ItemCount(ShellConstants.SVGIO_ALLVIEW, out int total);
                fv.ItemCount(ShellConstants.SVGIO_SELECTION, out int selected);
                return new SelectionStatusInfo(Math.Max(0, total), Math.Max(0, selected), 0, false);
            }
            finally
            {
                Marshal.ReleaseComObject(fv);
                Marshal.Release(pfv);
            }
        }

        // ==================== WIN11 COMMAND BAR OPERATIONS ====================

        public bool TryInvokeSelectionVerb(string ansiVerb)
        {
            IExplorerBrowser? active = ActiveBrowser;
            if (_isDestroyed || active == null || string.IsNullOrWhiteSpace(ansiVerb))
                return false;

            Guid iidFv2 = ShellConstants.IID_IFolderView2;
            if (active.GetCurrentView(ref iidFv2, out IntPtr pfv2) == ShellConstants.S_OK && pfv2 != IntPtr.Zero)
            {
                var fv2 = (IFolderView2)Marshal.GetObjectForIUnknown(pfv2);
                try
                {
                    if (fv2.ItemCount(ShellConstants.SVGIO_SELECTION, out int sel) == ShellConstants.S_OK && sel > 0)
                    {
                        if (fv2.InvokeVerbOnSelection(ansiVerb) == ShellConstants.S_OK)
                        {
                            InvalidateSelectionCache();
                            return true;
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(fv2);
                    Marshal.Release(pfv2);
                }
            }

            return TryInvokeContextMenuVerb(ShellConstants.SVGIO_SELECTION, ansiVerb);
        }

        public bool TryInvokeBackgroundVerb(string ansiVerb)
        {
            if (_isDestroyed || ActiveBrowser == null || string.IsNullOrWhiteSpace(ansiVerb))
                return false;

            bool ok = TryInvokeContextMenuVerb(ShellConstants.SVGIO_BACKGROUND, ansiVerb);
            if (ok)
                InvalidateSelectionCache();
            return ok;
        }

        private bool TryInvokeContextMenuVerb(uint svgioScope, string ansiVerb)
        {
            IExplorerBrowser? active = ActiveBrowser;
            if (_isDestroyed || active == null)
                return false;

            Guid iidShellView = ShellConstants.IID_IShellView;
            if (active.GetCurrentView(ref iidShellView, out IntPtr psv) != ShellConstants.S_OK || psv == IntPtr.Zero)
                return false;

            var sv = (IShellView)Marshal.GetObjectForIUnknown(psv);
            IntPtr pcm = IntPtr.Zero;
            IContextMenu? cm = null;
            IntPtr hMenu = IntPtr.Zero;
            IntPtr verbPtr = IntPtr.Zero;

            try
            {
                Guid iidCm = ShellConstants.IID_IContextMenu;
                if (sv.GetItemObject(svgioScope, ref iidCm, out pcm) != ShellConstants.S_OK || pcm == IntPtr.Zero)
                    return false;

                cm = (IContextMenu)Marshal.GetObjectForIUnknown(pcm);
                hMenu = NativeMethods.CreatePopupMenu();
                if (hMenu == IntPtr.Zero)
                    return false;

                cm.QueryContextMenu(hMenu, 0, 1, 0x7FFF, ShellConstants.CMF_NORMAL);

                verbPtr = Marshal.StringToHGlobalAnsi(ansiVerb);
                var cmi = new CMINVOKECOMMANDINFO
                {
                    cbSize = (uint)Marshal.SizeOf<CMINVOKECOMMANDINFO>(),
                    hwnd = _hostHwnd,
                    lpVerb = verbPtr,
                    nShow = NativeMethods.SW_SHOWNORMAL
                };

                return cm.InvokeCommand(ref cmi) >= 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (verbPtr != IntPtr.Zero) Marshal.FreeHGlobal(verbPtr);
                if (hMenu != IntPtr.Zero) NativeMethods.DestroyMenu(hMenu);
                if (cm != null) Marshal.ReleaseComObject(cm);
                if (pcm != IntPtr.Zero) Marshal.Release(pcm);
                Marshal.ReleaseComObject(sv);
                Marshal.Release(psv);
            }
        }

        public bool TryBeginInlineRename()
        {
            IExplorerBrowser? active = ActiveBrowser;
            if (_isDestroyed || active == null)
                return false;

            FocusNativeView();

            Guid iidFv2 = ShellConstants.IID_IFolderView2;
            if (active.GetCurrentView(ref iidFv2, out IntPtr pfv2) != ShellConstants.S_OK || pfv2 == IntPtr.Zero)
                return false;

            var fv2 = (IFolderView2)Marshal.GetObjectForIUnknown(pfv2);
            try
            {
                return fv2.DoRename() == ShellConstants.S_OK;
            }
            finally
            {
                Marshal.ReleaseComObject(fv2);
                Marshal.Release(pfv2);
            }
        }

        public async void SelectAllItems()
        {
            IExplorerBrowser? active = ActiveBrowser;
            if (_isDestroyed || active == null)
                return;

            int generation = _viewGeneration;
            FocusNativeView();
            // DefView's native Edit/Select All command performs one bulk selection.
            IntPtr viewWindow = GetShellViewWindowHandleFor(active);
            if (viewWindow != IntPtr.Zero)
            {
                NativeMethods.SendMessageW(viewWindow, 0x0111, new IntPtr(0x7021), IntPtr.Zero);
                InvalidateSelectionCache();
                var selected = GetItemCounts();
                if (selected.TotalItems == selected.SelectedItems) return;
            }
            Guid iidFv = ShellConstants.IID_IFolderView;
            if (active.GetCurrentView(ref iidFv, out IntPtr pfv) != ShellConstants.S_OK || pfv == IntPtr.Zero)
                return;

            var fv = (IFolderView)Marshal.GetObjectForIUnknown(pfv);
            try
            {
                if (fv.ItemCount(ShellConstants.SVGIO_ALLVIEW, out int total) == ShellConstants.S_OK && total > 0)
                {
                    for (int i = 0; i < total; i++)
                    {
                        if (_isDestroyed || generation != _viewGeneration || !ReferenceEquals(active, ActiveBrowser)) return;
                        fv.SelectItem(i, ShellConstants.SVSI_SELECT);
                        if ((i + 1) % 64 == 0) await Dispatcher.Yield(DispatcherPriority.Background);
                    }
                    InvalidateSelectionCache();
                }
            }
            finally
            {
                Marshal.ReleaseComObject(fv);
                Marshal.Release(pfv);
            }
        }

        public void ClearSelection()
        {
            IExplorerBrowser? active = ActiveBrowser;
            if (_isDestroyed || active == null)
                return;

            Guid iidSv = ShellConstants.IID_IShellView;
            if (active.GetCurrentView(ref iidSv, out IntPtr psv) != ShellConstants.S_OK || psv == IntPtr.Zero)
                return;

            var sv = (IShellView)Marshal.GetObjectForIUnknown(psv);
            try
            {
                sv.SelectItem(IntPtr.Zero, ShellConstants.SVSI_DESELECTOTHERS);
                InvalidateSelectionCache();
            }
            finally
            {
                Marshal.ReleaseComObject(sv);
                Marshal.Release(psv);
            }
        }

        public List<string> GetSelectedPaths()
        {
            var result = new List<string>();
            IExplorerBrowser? active = ActiveBrowser;
            if (_isDestroyed || active == null)
                return result;

            Guid iidFv2 = ShellConstants.IID_IFolderView2;
            if (active.GetCurrentView(ref iidFv2, out IntPtr pfv2) != ShellConstants.S_OK || pfv2 == IntPtr.Zero)
                return result;

            var fv2 = (IFolderView2)Marshal.GetObjectForIUnknown(pfv2);
            try
            {
                if (fv2.GetSelection(false, out IShellItemArray? array) == ShellConstants.S_OK && array != null)
                {
                    try
                    {
                        if (array.GetCount(out uint count) == ShellConstants.S_OK)
                        {
                            for (uint i = 0; i < count; i++)
                            {
                                if (array.GetItemAt(i, out IShellItem? item) == ShellConstants.S_OK && item != null)
                                {
                                    try
                                    {
                                        string? path = PidlManager.GetShellItemString(item, SIGDN.SIGDN_FILESYSPATH)
                                                       ?? PidlManager.GetShellItemString(item, SIGDN.SIGDN_DESKTOPABSOLUTEEDITING);
                                        if (!string.IsNullOrWhiteSpace(path))
                                            result.Add(path!);
                                    }
                                    finally
                                    {
                                        Marshal.ReleaseComObject(item);
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(array);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(fv2);
                Marshal.Release(pfv2);
            }

            return result;
        }

        public async Task<(bool Succeeded, string? Message)> TryCreateNewItemAsync(NewItemKind kind, string? folderFileSystemPath)
        {
            if (_isDestroyed || _browser == null)
                return (false, "视图未就绪。");

            if (_isShowingSearchResults)
                return (false, "搜索结果视图下无法直接新建项目。");

            if (string.IsNullOrWhiteSpace(folderFileSystemPath) || !Directory.Exists(folderFileSystemPath))
            {
                // Fallback for special namespaces supporting canonical NewFolder verb
                if (kind == NewItemKind.Folder && TryInvokeBackgroundVerb("NewFolder"))
                    return (true, null);

                return (false, "当前位置不支持新建文件或文件夹。");
            }

            string dir = folderFileSystemPath!;
            string baseName;
            string ext;

            switch (kind)
            {
                case NewItemKind.Folder:
                    baseName = "新建文件夹";
                    ext = string.Empty;
                    break;
                case NewItemKind.TextDocument:
                    baseName = "新建文本文档";
                    ext = ".txt";
                    break;
                case NewItemKind.MarkdownDocument:
                    baseName = "新建 Markdown 文档";
                    ext = ".md";
                    break;
                case NewItemKind.ZipArchive:
                    baseName = "新建压缩文件夹";
                    ext = ".zip";
                    break;
                default:
                    return (false, "未知的新建类型。");
            }

            string createdName = string.Empty;
            string fullPath = string.Empty;
            bool created = false;

            try
            {
                for (int suffix = 1; suffix <= 500; suffix++)
                {
                    createdName = suffix == 1
                        ? (baseName + ext)
                        : $"{baseName} ({suffix}){ext}";
                    fullPath = Path.Combine(dir, createdName);

                    if (File.Exists(fullPath) || Directory.Exists(fullPath))
                        continue;

                    if (kind == NewItemKind.Folder)
                    {
                        Directory.CreateDirectory(fullPath);
                        NativeMethods.SHChangeNotifyPath(
                            NativeMethods.SHCNE_MKDIR,
                            NativeMethods.SHCNF_PATHW | NativeMethods.SHCNF_FLUSHNOWAIT,
                            fullPath,
                            null);
                        created = true;
                        break;
                    }

                    try
                    {
                        using (var fs = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            if (kind == NewItemKind.ZipArchive)
                            {
                                // Standard 22-byte End of Central Directory (EOCD) empty ZIP header
                                byte[] emptyZip =
                                {
                                    0x50, 0x4B, 0x05, 0x06, 0x00, 0x00, 0x00, 0x00,
                                    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                                    0x00, 0x00, 0x00, 0x00, 0x00, 0x00
                                };
                                fs.Write(emptyZip, 0, emptyZip.Length);
                            }
                            fs.Flush();
                        }

                        NativeMethods.SHChangeNotifyPath(
                            NativeMethods.SHCNE_CREATE,
                            NativeMethods.SHCNF_PATHW | NativeMethods.SHCNF_FLUSHNOWAIT,
                            fullPath,
                            null);
                        created = true;
                        break;
                    }
                    catch (IOException) when (File.Exists(fullPath) || Directory.Exists(fullPath))
                    {
                        // Concurrent creation won the name; retry atomically with the next suffix
                    }
                }

                if (!created)
                    return (false, "新建失败：同名项目过多。");

                RefreshView();
                await Task.Delay(120).ConfigureAwait(true);
                FocusNativeView();
                TrySelectChildByName(createdName, beginRename: true);
                InvalidateSelectionCache();
                return (true, $"已创建 {createdName}");
            }
            catch (Exception ex)
            {
                return (false, $"新建失败：{ex.Message}");
            }
        }

        public async Task<bool> SelectChildItemWhenReadyAsync(string childFileName)
        {
            if (string.IsNullOrWhiteSpace(childFileName))
                return false;

            for (int attempt = 0; attempt < 18; attempt++)
            {
                if (_isDestroyed)
                    return false;

                if (TrySelectChildByName(childFileName, beginRename: false))
                {
                    InvalidateSelectionCache();
                    FocusNativeView();
                    return true;
                }

                await Task.Delay(40).ConfigureAwait(true);
            }

            return false;
        }

        private bool TrySelectChildByName(string childFileName, bool beginRename)
        {
            if (_isDestroyed || _browser == null || string.IsNullOrWhiteSpace(childFileName))
                return false;

            Guid iidSv = ShellConstants.IID_IShellView;
            Guid iidFv = ShellConstants.IID_IFolderView;
            if (_browser.GetCurrentView(ref iidSv, out IntPtr psv) != ShellConstants.S_OK || psv == IntPtr.Zero)
                return false;

            var sv = (IShellView)Marshal.GetObjectForIUnknown(psv);
            IntPtr pfv = IntPtr.Zero;
            IFolderView? fv = null;
            IntPtr pFolder = IntPtr.Zero;
            IShellFolder? sf = null;

            try
            {
                if (_browser.GetCurrentView(ref iidFv, out pfv) == ShellConstants.S_OK && pfv != IntPtr.Zero)
                {
                    fv = (IFolderView)Marshal.GetObjectForIUnknown(pfv);
                    Guid iidSf = ShellConstants.IID_IShellFolder;
                    if (fv.GetFolder(ref iidSf, out pFolder) == ShellConstants.S_OK && pFolder != IntPtr.Zero)
                    {
                        sf = (IShellFolder)Marshal.GetObjectForIUnknown(pFolder);
                        uint eaten = 0;
                        uint attrs = 0;
                        if (sf.ParseDisplayName(IntPtr.Zero, IntPtr.Zero, childFileName, ref eaten, out IntPtr childPidl, ref attrs) == ShellConstants.S_OK &&
                            childPidl != IntPtr.Zero)
                        {
                            try
                            {
                                uint modeFlag = beginRename ? ShellConstants.SVSI_EDIT : ShellConstants.SVSI_SELECT;
                                uint flags = modeFlag |
                                             ShellConstants.SVSI_DESELECTOTHERS |
                                             ShellConstants.SVSI_ENSUREVISIBLE |
                                             ShellConstants.SVSI_FOCUSED;
                                return sv.SelectItem(childPidl, flags) >= 0;
                            }
                            finally
                            {
                                PidlManager.Free(ref childPidl);
                            }
                        }
                    }
                }

                return false;
            }
            finally
            {
                if (sf != null) Marshal.ReleaseComObject(sf);
                if (pFolder != IntPtr.Zero) Marshal.Release(pFolder);
                if (fv != null) Marshal.ReleaseComObject(fv);
                if (pfv != IntPtr.Zero) Marshal.Release(pfv);
                Marshal.ReleaseComObject(sv);
                Marshal.Release(psv);
            }
        }

        // ==================== VIEW MODES & SORTING ====================

        internal bool TrySetViewStyle(ExplorerViewStyle style)
        {
            IExplorerBrowser? active = ActiveBrowser;
            if (_isDestroyed || active == null)
                return false;

            (FOLDERVIEWMODE mode, int iconSize) = style switch
            {
                ExplorerViewStyle.ExtraLargeIcons => (FOLDERVIEWMODE.FVM_ICON, 256),
                ExplorerViewStyle.LargeIcons => (FOLDERVIEWMODE.FVM_ICON, 96),
                ExplorerViewStyle.MediumIcons => (FOLDERVIEWMODE.FVM_ICON, 48),
                ExplorerViewStyle.SmallIcons => (FOLDERVIEWMODE.FVM_SMALLICON, 16),
                ExplorerViewStyle.List => (FOLDERVIEWMODE.FVM_LIST, -1),
                ExplorerViewStyle.Details => (FOLDERVIEWMODE.FVM_DETAILS, -1),
                ExplorerViewStyle.Tiles => (FOLDERVIEWMODE.FVM_TILE, 48),
                ExplorerViewStyle.Content => (FOLDERVIEWMODE.FVM_CONTENT, 32),
                _ => (FOLDERVIEWMODE.FVM_DETAILS, -1)
            };

            Guid iidFv2 = ShellConstants.IID_IFolderView2;
            if (active.GetCurrentView(ref iidFv2, out IntPtr pfv2) == ShellConstants.S_OK && pfv2 != IntPtr.Zero)
            {
                var fv2 = (IFolderView2)Marshal.GetObjectForIUnknown(pfv2);
                try
                {
                    if (fv2.SetViewModeAndIconSize(mode, iconSize) == ShellConstants.S_OK)
                        return true;
                }
                finally
                {
                    Marshal.ReleaseComObject(fv2);
                    Marshal.Release(pfv2);
                }
            }

            return TrySetViewMode(mode);
        }

        internal ExplorerViewStyle GetCurrentViewStyle()
        {
            IExplorerBrowser? active = ActiveBrowser;
            if (_isDestroyed || active == null)
                return ExplorerViewStyle.Details;

            Guid iidFv2 = ShellConstants.IID_IFolderView2;
            if (active.GetCurrentView(ref iidFv2, out IntPtr pfv2) == ShellConstants.S_OK && pfv2 != IntPtr.Zero)
            {
                var fv2 = (IFolderView2)Marshal.GetObjectForIUnknown(pfv2);
                try
                {
                    if (fv2.GetViewModeAndIconSize(out FOLDERVIEWMODE mode, out int iconSize) == ShellConstants.S_OK)
                    {
                        return mode switch
                        {
                            FOLDERVIEWMODE.FVM_LIST => ExplorerViewStyle.List,
                            FOLDERVIEWMODE.FVM_DETAILS => ExplorerViewStyle.Details,
                            FOLDERVIEWMODE.FVM_TILE => ExplorerViewStyle.Tiles,
                            FOLDERVIEWMODE.FVM_CONTENT => ExplorerViewStyle.Content,
                            FOLDERVIEWMODE.FVM_SMALLICON => ExplorerViewStyle.SmallIcons,
                            FOLDERVIEWMODE.FVM_ICON or FOLDERVIEWMODE.FVM_THUMBNAIL =>
                                iconSize >= 180 ? ExplorerViewStyle.ExtraLargeIcons :
                                iconSize >= 72 ? ExplorerViewStyle.LargeIcons :
                                iconSize <= 24 && iconSize > 0 ? ExplorerViewStyle.SmallIcons :
                                ExplorerViewStyle.MediumIcons,
                            _ => ExplorerViewStyle.Details
                        };
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(fv2);
                    Marshal.Release(pfv2);
                }
            }

            FOLDERVIEWMODE basic = GetCurrentViewMode();
            return basic == FOLDERVIEWMODE.FVM_ICON || basic == FOLDERVIEWMODE.FVM_THUMBNAIL
                ? ExplorerViewStyle.LargeIcons
                : ExplorerViewStyle.Details;
        }

        internal (PROPERTYKEY Key, int Direction) GetCurrentSortState()
        {
            IExplorerBrowser? active = ActiveBrowser;
            if (_isDestroyed || active == null)
                return (PropertyKeys.ItemNameDisplay, ShellConstants.SORT_ASCENDING);

            Guid iidFv2 = ShellConstants.IID_IFolderView2;
            if (active.GetCurrentView(ref iidFv2, out IntPtr pfv2) != ShellConstants.S_OK || pfv2 == IntPtr.Zero)
                return (PropertyKeys.ItemNameDisplay, ShellConstants.SORT_ASCENDING);

            var fv2 = (IFolderView2)Marshal.GetObjectForIUnknown(pfv2);
            try
            {
                if (fv2.GetSortColumnCount(out int count) == ShellConstants.S_OK && count > 0)
                {
                    var cols = new SORTCOLUMN[Math.Min(count, 4)];
                    if (fv2.GetSortColumns(cols, cols.Length) == ShellConstants.S_OK)
                    {
                        return (cols[0].propkey, cols[0].direction >= 0 ? ShellConstants.SORT_ASCENDING : ShellConstants.SORT_DESCENDING);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(fv2);
                Marshal.Release(pfv2);
            }

            return (PropertyKeys.ItemNameDisplay, ShellConstants.SORT_ASCENDING);
        }

        internal bool TrySetSortState(PROPERTYKEY? targetKey, int? targetDirection)
        {
            IExplorerBrowser? active = ActiveBrowser;
            if (_isDestroyed || active == null)
                return false;

            (PROPERTYKEY curKey, int curDir) = GetCurrentSortState();
            PROPERTYKEY finalKey = targetKey ?? curKey;
            int finalDir;

            if (targetDirection.HasValue)
            {
                finalDir = targetDirection.Value;
            }
            else if (targetKey.HasValue && targetKey.Value.Equals(curKey))
            {
                finalDir = curDir == ShellConstants.SORT_ASCENDING
                    ? ShellConstants.SORT_DESCENDING
                    : ShellConstants.SORT_ASCENDING;
            }
            else if (finalKey.Equals(PropertyKeys.DateModified) || finalKey.Equals(PropertyKeys.Size))
            {
                finalDir = ShellConstants.SORT_DESCENDING;
            }
            else
            {
                finalDir = ShellConstants.SORT_ASCENDING;
            }

            Guid iidFv2 = ShellConstants.IID_IFolderView2;
            if (active.GetCurrentView(ref iidFv2, out IntPtr pfv2) != ShellConstants.S_OK || pfv2 == IntPtr.Zero)
                return false;

            var fv2 = (IFolderView2)Marshal.GetObjectForIUnknown(pfv2);
            try
            {
                var cols = new[] { new SORTCOLUMN { propkey = finalKey, direction = finalDir } };
                return fv2.SetSortColumns(cols, 1) == ShellConstants.S_OK;
            }
            finally
            {
                Marshal.ReleaseComObject(fv2);
                Marshal.Release(pfv2);
            }
        }

        internal bool TrySetViewMode(FOLDERVIEWMODE mode)
        {
            IExplorerBrowser? active = ActiveBrowser;
            if (_isDestroyed || active == null)
                return false;

            Guid iidFolderView = ShellConstants.IID_IFolderView;
            if (active.GetCurrentView(ref iidFolderView, out IntPtr pfv) != ShellConstants.S_OK || pfv == IntPtr.Zero)
                return false;

            var fv = (IFolderView)Marshal.GetObjectForIUnknown(pfv);
            try
            {
                return fv.SetCurrentViewMode((uint)mode) == ShellConstants.S_OK;
            }
            finally
            {
                Marshal.ReleaseComObject(fv);
                Marshal.Release(pfv);
            }
        }

        internal FOLDERVIEWMODE GetCurrentViewMode()
        {
            IExplorerBrowser? active = ActiveBrowser;
            if (_isDestroyed || active == null)
                return FOLDERVIEWMODE.FVM_DETAILS;

            Guid iidFolderView = ShellConstants.IID_IFolderView;
            if (active.GetCurrentView(ref iidFolderView, out IntPtr pfv) != ShellConstants.S_OK || pfv == IntPtr.Zero)
                return FOLDERVIEWMODE.FVM_DETAILS;

            var fv = (IFolderView)Marshal.GetObjectForIUnknown(pfv);
            try
            {
                if (fv.GetCurrentViewMode(out uint mode) == ShellConstants.S_OK)
                    return (FOLDERVIEWMODE)mode;
                return FOLDERVIEWMODE.FVM_DETAILS;
            }
            finally
            {
                Marshal.ReleaseComObject(fv);
                Marshal.Release(pfv);
            }
        }

        // ==================== ASYNC DIRECTORY SEARCH (NATIVE IResultsFolder) ====================

        private bool EnsureSearchBrowserReady()
        {
            if (_isDestroyed || _hostHwnd == IntPtr.Zero)
                return false;

            if (_searchBrowser != null && _searchHostHwnd != IntPtr.Zero)
            {
                _searchBrowser.RemoveAll();
                if (_browseHostHwnd != IntPtr.Zero)
                    NativeMethods.ShowWindow(_browseHostHwnd, NativeMethods.SW_HIDE);
                NativeMethods.ShowWindow(_searchHostHwnd, NativeMethods.SW_SHOW);
                SyncBrowserRect();
                AttachDirectUiSubclassIfNeeded();
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
            if (_paneSite != null && _searchBrowser is IObjectWithSite withSite)
            {
                withSite.SetSite(_paneSite);
            }

            _searchBrowser.SetOptions(EXPLORER_BROWSER_OPTIONS.EBO_SHOWFRAMES | EXPLORER_BROWSER_OPTIONS.EBO_NOBORDER);

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

            _searchBrowser.SetPropertyBag("LeafTab.SearchBag.v1");

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

            if (_browseHostHwnd != IntPtr.Zero)
                NativeMethods.ShowWindow(_browseHostHwnd, NativeMethods.SW_HIDE);
            NativeMethods.ShowWindow(_searchHostHwnd, NativeMethods.SW_SHOW);
            SyncBrowserRect();
            return true;
        }

        public async Task<int> StartSearchAsync(string rawQuery, IntPtr currentFolderPidl, string? folderFileSystemPath)
        {
            if (_isDestroyed || _browser == null)
                return 0;

            string query = (rawQuery ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(query))
            {
                ExitSearchMode();
                return 0;
            }

            if (!_isShowingSearchResults && currentFolderPidl != IntPtr.Zero)
            {
                PidlManager.Free(ref _preSearchFolderPidl);
                _preSearchFolderPidl = PidlManager.Clone(currentFolderPidl);
            }

            // Snapshot virtual folder children BEFORE switching view if there is no filesystem path
            List<byte[]>? virtualCandidates = null;
            bool virtualTruncated = false;
            if (string.IsNullOrWhiteSpace(folderFileSystemPath) || !Directory.Exists(folderFileSystemPath))
            {
                IntPtr sourceFolder = _preSearchFolderPidl != IntPtr.Zero ? _preSearchFolderPidl : currentFolderPidl;
                (virtualCandidates, virtualTruncated) = CollectMatchingVirtualChildren(sourceFolder, query);
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
            _isSearchRunning = true;
            _isSearchTruncated = false;
            _searchMatchCount = 0;
            InvalidateSelectionCache();
            _searchBrowser.SetEmptyText($"正在搜索“{query}”…");
            SearchStateChanged?.Invoke(this, EventArgs.Empty);

            // Wait until the ResultsFolder IShellView is mounted and ready to accept items
            for (int waitIdx = 0; waitIdx < (firstInit ? 16 : 6); waitIdx++)
            {
                if (_isDestroyed || token.IsCancellationRequested || myGen != _searchGeneration)
                    return 0;

                if (IsResultsFolderReady())
                    break;

                await Task.Delay(18).ConfigureAwait(true);
            }
            _isFillSearchTransition = false;
            if (_isDestroyed || token.IsCancellationRequested || myGen != _searchGeneration)
                return 0;

            AttachDirectUiSubclassIfNeeded();
            TrySetViewStyle(ExplorerViewStyle.Details);

            const int MaxResults = 400;

            if (virtualCandidates != null)
            {
                int added = AddPidlBatchToResultsFolder(virtualCandidates);
                _searchMatchCount = added;
                _isSearchTruncated = virtualTruncated;
                _isSearchRunning = false;
                _searchBrowser.SetEmptyText(added == 0 ? "没有与搜索条件匹配的项。" : string.Empty);
                InvalidateSelectionCache();
                SearchStateChanged?.Invoke(this, EventArgs.Empty);
                return added;
            }

            string rootDir = folderFileSystemPath!;
            (_, bool includeHidden) = PidlManager.GetShellVisibilityFlags();
            Func<string, bool> predicate = BuildFileNameMatcher(query);
            bool fsTruncated = false;

            try
            {
                await Task.Run(async () =>
                {
                    var pendingBatch = new List<string>(32);
                    var dirStack = new Stack<string>();
                    dirStack.Push(rootDir);
                    int totalMatched = 0;
                    bool stopScan = false;

                    while (dirStack.Count > 0 && !token.IsCancellationRequested && !stopScan)
                    {
                        string currentDir = dirStack.Pop();
                        IEnumerable<string> entries;
                        try
                        {
                            entries = Directory.EnumerateFileSystemEntries(currentDir);
                        }
                        catch
                        {
                            continue;
                        }

                        var childDirsToVisit = new List<string>();
                        try
                        {
                            foreach (string fullPath in entries)
                            {
                                if (token.IsCancellationRequested)
                                {
                                    stopScan = true;
                                    break;
                                }

                                string name = Path.GetFileName(fullPath);
                                if (string.IsNullOrEmpty(name))
                                    continue;

                                FileAttributes attrs;
                                try
                                {
                                    attrs = File.GetAttributes(fullPath);
                                }
                                catch
                                {
                                    continue;
                                }

                                if (!includeHidden && (attrs & FileAttributes.Hidden) != 0)
                                    continue;

                                if (predicate(name))
                                {
                                    if (totalMatched >= MaxResults)
                                    {
                                        fsTruncated = true;
                                        stopScan = true;
                                        break;
                                    }

                                    pendingBatch.Add(fullPath);
                                    totalMatched++;

                                    if (pendingBatch.Count >= 32)
                                    {
                                        string[] snapshot = pendingBatch.ToArray();
                                        pendingBatch.Clear();
                                        await DispatchSearchBatchAsync(snapshot, myGen, token).ConfigureAwait(false);
                                    }
                                }

                                bool isSubDir = (attrs & FileAttributes.Directory) != 0 &&
                                                (attrs & FileAttributes.ReparsePoint) == 0;
                                if (isSubDir)
                                {
                                    childDirsToVisit.Add(fullPath);
                                }
                            }
                        }
                        catch
                        {
                            // Ignore transient IO/permission errors mid-enumeration
                        }

                        if (!stopScan)
                        {
                            for (int i = childDirsToVisit.Count - 1; i >= 0; i--)
                            {
                                dirStack.Push(childDirsToVisit[i]);
                            }
                        }
                    }

                    if (pendingBatch.Count > 0 && !token.IsCancellationRequested)
                    {
                        await DispatchSearchBatchAsync(pendingBatch.ToArray(), myGen, token).ConfigureAwait(false);
                    }
                }, token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }

            if (!_isDestroyed && myGen == _searchGeneration && !token.IsCancellationRequested)
            {
                _isSearchTruncated = fsTruncated;
                _isSearchRunning = false;
                _searchBrowser?.SetEmptyText(_searchMatchCount == 0 ? "没有与搜索条件匹配的项。" : string.Empty);
                InvalidateSelectionCache();
                SearchStateChanged?.Invoke(this, EventArgs.Empty);
            }

            return _searchMatchCount;
        }

        private Task DispatchSearchBatchAsync(string[] paths, int expectedGen, CancellationToken token)
        {
            return Dispatcher.InvokeAsync(() =>
            {
                if (_isDestroyed || token.IsCancellationRequested || expectedGen != _searchGeneration || !_isShowingSearchResults)
                    return;

                int added = AddPathBatchToResultsFolder(paths);
                if (added > 0)
                {
                    _searchMatchCount += added;
                    InvalidateSelectionCache();
                    SearchStateChanged?.Invoke(this, EventArgs.Empty);
                }
            }, DispatcherPriority.Normal).Task;
        }

        private int AddPathBatchToResultsFolder(string[] paths)
        {
            IResultsFolder? rf = AcquireResultsFolder(out IntPtr prf);
            if (rf == null)
                return 0;

            int added = 0;
            Guid iidShellItem = ShellConstants.IID_IShellItem;
            try
            {
                foreach (string path in paths)
                {
                    if (NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iidShellItem, out IShellItem? item) == ShellConstants.S_OK &&
                        item != null)
                    {
                        try
                        {
                            if (rf.AddItem(item) == ShellConstants.S_OK)
                                added++;
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(item);
                        }
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(rf);
                if (prf != IntPtr.Zero) Marshal.Release(prf);
            }

            return added;
        }

        private int AddPidlBatchToResultsFolder(List<byte[]> pidlBytesList)
        {
            IResultsFolder? rf = AcquireResultsFolder(out IntPtr prf);
            if (rf == null)
                return 0;

            int added = 0;
            try
            {
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
                Marshal.ReleaseComObject(rf);
                if (prf != IntPtr.Zero) Marshal.Release(prf);
            }

            return added;
        }

        private bool IsResultsFolderReady()
        {
            IResultsFolder? rf = AcquireResultsFolder(out IntPtr prf);
            if (rf == null)
                return false;

            Marshal.ReleaseComObject(rf);
            if (prf != IntPtr.Zero)
                Marshal.Release(prf);
            return true;
        }

        private IResultsFolder? AcquireResultsFolder(out IntPtr prf)
        {
            prf = IntPtr.Zero;
            if (_isDestroyed || _searchBrowser == null)
                return null;

            Guid iidFv = ShellConstants.IID_IFolderView;
            if (_searchBrowser.GetCurrentView(ref iidFv, out IntPtr pfv) != ShellConstants.S_OK || pfv == IntPtr.Zero)
                return null;

            var fv = (IFolderView)Marshal.GetObjectForIUnknown(pfv);
            try
            {
                Guid iidRf = ShellConstants.IID_IResultsFolder;
                if (fv.GetFolder(ref iidRf, out prf) == ShellConstants.S_OK && prf != IntPtr.Zero)
                {
                    return (IResultsFolder)Marshal.GetObjectForIUnknown(prf);
                }
                return null;
            }
            finally
            {
                Marshal.ReleaseComObject(fv);
                Marshal.Release(pfv);
            }
        }

        private static (List<byte[]> Items, bool Truncated) CollectMatchingVirtualChildren(IntPtr folderPidl, string query)
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
                                    if (matched.Count >= 200)
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
            if (query.IndexOf('*') >= 0 || query.IndexOf('?') >= 0)
            {
                string pattern = "^" + Regex.Escape(query).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
                var rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                return name => rx.IsMatch(name);
            }

            return name => name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public bool ExitSearchMode() => ExitSearchModeInternal(notifyChanged: true);

        private bool ExitSearchModeInternal(bool notifyChanged)
        {
            if (_isDestroyed || _browser == null || !_isShowingSearchResults)
                return false;

            CancelSearchStateInternal(freeSavedPidl: true);
            _searchBrowser?.RemoveAll();
            _searchBrowser?.SetEmptyText(string.Empty);

            if (_searchHostHwnd != IntPtr.Zero)
                NativeMethods.ShowWindow(_searchHostHwnd, NativeMethods.SW_HIDE);
            if (_browseHostHwnd != IntPtr.Zero)
                NativeMethods.ShowWindow(_browseHostHwnd, NativeMethods.SW_SHOW);

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

        private void CancelSearchStateInternal(bool freeSavedPidl)
        {
            CancelSearchWorkerOnly();
            _isShowingSearchResults = false;
            _isFillSearchTransition = false;
            _isSearchTruncated = false;
            _searchMatchCount = 0;

            if (freeSavedPidl)
            {
                PidlManager.Free(ref _preSearchFolderPidl);
            }
        }

        public List<string> GetDisplayedItemNames(int maxCount = 50)
        {
            var names = new List<string>();
            IExplorerBrowser? active = ActiveBrowser;
            if (_isDestroyed || active == null)
                return names;

            Guid iidFv2 = ShellConstants.IID_IFolderView2;
            if (active.GetCurrentView(ref iidFv2, out IntPtr pfv2) == ShellConstants.S_OK && pfv2 != IntPtr.Zero)
            {
                var fv2 = (IFolderView2)Marshal.GetObjectForIUnknown(pfv2);
                try
                {
                    if (fv2.ItemCount(ShellConstants.SVGIO_ALLVIEW, out int total) == ShellConstants.S_OK && total > 0)
                    {
                        int limit = Math.Min(total, maxCount);
                        Guid iidShellItem = ShellConstants.IID_IShellItem;
                        for (int i = 0; i < limit; i++)
                        {
                            if (fv2.GetItem(i, ref iidShellItem, out IntPtr pItem) == ShellConstants.S_OK && pItem != IntPtr.Zero)
                            {
                                var item = (IShellItem)Marshal.GetObjectForIUnknown(pItem);
                                try
                                {
                                    string? name = PidlManager.GetShellItemString(item, SIGDN.SIGDN_NORMALDISPLAY);
                                    if (!string.IsNullOrEmpty(name))
                                        names.Add(name!);
                                }
                                finally
                                {
                                    Marshal.ReleaseComObject(item);
                                    Marshal.Release(pItem);
                                }
                            }
                        }
                    }
                    return names;
                }
                finally
                {
                    Marshal.ReleaseComObject(fv2);
                    Marshal.Release(pfv2);
                }
            }

            return names;
        }

        public void SuppressNativeFocusRect()
        {
            if (_isDestroyed || _hostHwnd == IntPtr.Zero || !NativeMethods.IsWindow(_hostHwnd))
                return;

            IntPtr hideWParam = NativeMethods.MakeUiStateWParam(
                NativeMethods.UIS_SET,
                (ushort)(NativeMethods.UISF_HIDEFOCUS | NativeMethods.UISF_HIDEACCEL));

            IntPtr rootHwnd = NativeMethods.GetAncestor(_hostHwnd, NativeMethods.GA_ROOT);
            if (rootHwnd != IntPtr.Zero && NativeMethods.IsWindow(rootHwnd))
            {
                NativeMethods.SendMessageW(rootHwnd, NativeMethods.WM_CHANGEUISTATE, hideWParam, IntPtr.Zero);
                NativeMethods.SendMessageW(rootHwnd, NativeMethods.WM_UPDATEUISTATE, hideWParam, IntPtr.Zero);
            }

            NativeMethods.SendMessageW(_hostHwnd, NativeMethods.WM_UPDATEUISTATE, hideWParam, IntPtr.Zero);

            NativeMethods.EnumChildWindows(_hostHwnd, (child, _) =>
            {
                NativeMethods.SendMessageW(child, NativeMethods.WM_UPDATEUISTATE, hideWParam, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);

            if (_subclassedDirectUiHwnd != IntPtr.Zero && NativeMethods.IsWindow(_subclassedDirectUiHwnd))
            {
                NativeMethods.InvalidateRect(_subclassedDirectUiHwnd, IntPtr.Zero, false);
            }
        }

        public void ClearUnselectedItemFocusBoxIfNeeded()
        {
            if (_isDestroyed || _hostHwnd == IntPtr.Zero || _isMouseDownInView || _isKeyboardFocusActive)
                return;

            IntPtr focused = NativeMethods.GetFocus();
            if (focused != IntPtr.Zero && _subclassedDirectUiHwnd != IntPtr.Zero && focused == _subclassedDirectUiHwnd)
            {
                InvalidateSelectionCache();
                if (GetItemCounts().SelectedItems == 0)
                {
                    NativeMethods.SetFocus(_hostHwnd);
                }
            }
        }

        public void FocusNativeView()
        {
            if (_isDestroyed || _hostHwnd == IntPtr.Zero) return;

            _isKeyboardFocusActive = false;
            System.Windows.Input.Keyboard.ClearFocus();
            SuppressNativeFocusRect();

            IExplorerBrowser? active = ActiveBrowser;
            if (active != null)
            {
                Guid iidShellView = ShellConstants.IID_IShellView;
                if (active.GetCurrentView(ref iidShellView, out IntPtr psv) == ShellConstants.S_OK && psv != IntPtr.Zero)
                {
                    var sv = (IShellView)Marshal.GetObjectForIUnknown(psv);
                    try
                    {
                        sv.UIActivate(1); // SVUIA_ACTIVATE_NOFOCUS
                        AttachDirectUiSubclassIfNeeded();
                        InvalidateSelectionCache();
                        bool hasSelection = GetItemCounts().SelectedItems > 0;

                        if (hasSelection && _subclassedDirectUiHwnd != IntPtr.Zero && NativeMethods.IsWindow(_subclassedDirectUiHwnd))
                        {
                            NativeMethods.SetFocus(_subclassedDirectUiHwnd);
                            SuppressNativeFocusRect();
                            return;
                        }

                        // Keep keyboard focus on outer _hostHwnd when 0 items are selected so DirectUI ItemsView
                        // never sets keyfocused="true" (the 1px solid black rectangle) on unselected item 0!
                        NativeMethods.SetFocus(_hostHwnd);
                        SuppressNativeFocusRect();
                        return;
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(sv);
                        Marshal.Release(psv);
                    }
                }
            }

            AttachDirectUiSubclassIfNeeded();
            NativeMethods.SetFocus(_hostHwnd);
            SuppressNativeFocusRect();
        }

        public bool IsNativeFocusInside()
        {
            if (_isDestroyed || _hostHwnd == IntPtr.Zero)
                return false;

            IntPtr focused = NativeMethods.GetFocus();
            return focused != IntPtr.Zero && (focused == _hostHwnd || NativeMethods.IsChild(_hostHwnd, focused));
        }

        public bool TryTranslateShellAccelerator(ref MSG wpfMsg)
        {
            IExplorerBrowser? active = ActiveBrowser;
            if (_isDestroyed || active == null || !IsNativeFocusInside())
                return false;

            bool parkedOnHost = NativeMethods.GetFocus() == _hostHwnd;
            if (parkedOnHost && wpfMsg.message == (int)NativeMethods.WM_KEYDOWN)
            {
                int vk = wpfMsg.wParam.ToInt32();
                // Ignore bare modifier presses (Shift=0x10, Ctrl=0x11, Alt=0x12, LWin=0x5B, RWin=0x5C)
                if (vk != 0x10 && vk != 0x11 && vk != 0x12 && vk != 0x5B && vk != 0x5C)
                {
                    _isKeyboardFocusActive = true;
                    AttachDirectUiSubclassIfNeeded();
                    if (_subclassedDirectUiHwnd != IntPtr.Zero && NativeMethods.IsWindow(_subclassedDirectUiHwnd))
                    {
                        NativeMethods.SetFocus(_subclassedDirectUiHwnd);
                        wpfMsg.hwnd = _subclassedDirectUiHwnd;
                    }
                }
            }

            Guid iidShellView = ShellConstants.IID_IShellView;
            if (active.GetCurrentView(ref iidShellView, out IntPtr psv) != ShellConstants.S_OK || psv == IntPtr.Zero)
                return false;

            var sv = (IShellView)Marshal.GetObjectForIUnknown(psv);
            try
            {
                var nativeMsg = new NATIVE_MSG
                {
                    hwnd = wpfMsg.hwnd,
                    message = (uint)wpfMsg.message,
                    wParam = wpfMsg.wParam,
                    lParam = wpfMsg.lParam,
                    time = (uint)wpfMsg.time,
                    pt = new POINT(wpfMsg.pt_x, wpfMsg.pt_y)
                };

                if (sv.TranslateAccelerator(ref nativeMsg) == ShellConstants.S_OK)
                    return true;

                // If focus was parked on _hostHwnd when this keydown began, forward navigation/type-ahead keys directly to DirectUIHWND
                if (parkedOnHost &&
                    _isKeyboardFocusActive &&
                    wpfMsg.message == (int)NativeMethods.WM_KEYDOWN &&
                    _subclassedDirectUiHwnd != IntPtr.Zero &&
                    NativeMethods.IsWindow(_subclassedDirectUiHwnd))
                {
                    NativeMethods.SendMessageW(_subclassedDirectUiHwnd, (uint)wpfMsg.message, wpfMsg.wParam, wpfMsg.lParam);
                    return true;
                }

                return false;
            }
            finally
            {
                Marshal.ReleaseComObject(sv);
                Marshal.Release(psv);
            }
        }

        private void OnNativeViewCreated()
        {
            _innerStatusBarClipPx = -1;
            if (_isDestroyed) return;
            _viewGeneration++;
            InvalidateSelectionCache();
            SyncBrowserRect();
            AttachDirectUiSubclassIfNeeded();
            SuppressNativeFocusRect();
        }

        private void OnNativeNavigationComplete(IntPtr pidlFolder)
        {
            if (_isDestroyed) return;
            _viewGeneration++;
            _isKeyboardFocusActive = false;
            _isMouseDownInView = false;
            InvalidateSelectionCache();
            SyncBrowserRect();
            AttachDirectUiSubclassIfNeeded();
            SuppressNativeFocusRect();
            ClearUnselectedItemFocusBoxIfNeeded();

            // Internal ResultsFolder creation during FillFromObject(null, 0)
            if (_isFillSearchTransition)
            {
                _isFillSearchTransition = false;
                return;
            }

            // User double-clicked a subfolder inside search results or clicked left NavPane: exit search mode
            if (_isShowingSearchResults)
            {
                CancelSearchStateInternal(freeSavedPidl: true);
                _browser?.SetEmptyText(string.Empty);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_isDestroyed)
                        SearchStateChanged?.Invoke(this, EventArgs.Empty);
                }));
            }

            IntPtr cloned = PidlManager.Clone(pidlFolder);
            if (cloned == IntPtr.Zero)
                return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (!_isDestroyed)
                    {
                        SyncBrowserRect();
                        AttachDirectUiSubclassIfNeeded();
                        SuppressNativeFocusRect();
                        NavigationCompleted?.Invoke(this, new NavigationCompletedEventArgs(cloned));
                        ClearUnselectedItemFocusBoxIfNeeded();
                    }
                }
                finally
                {
                    PidlManager.Free(ref cloned);
                }
            }));
        }

        private void OnNativeNavigationFailed(IntPtr pidlFolder)
        {
            if (_isDestroyed) return;
            FolderDisplayInfo info = PidlManager.GetDisplayInfo(pidlFolder);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_isDestroyed)
                    NavigationFailed?.Invoke(this, $"无法打开目录：{info.AddressText}");
            }));
        }

        public void AttachDirectUiSubclassIfNeeded()
        {
            if (_isDestroyed || _hostHwnd == IntPtr.Zero)
                return;

            IntPtr searchRoot = GetShellViewWindowHandle();
            if (searchRoot == IntPtr.Zero)
                searchRoot = _isShowingSearchResults && _searchHostHwnd != IntPtr.Zero ? _searchHostHwnd : _browseHostHwnd;
            if (searchRoot == IntPtr.Zero)
                searchRoot = _hostHwnd;

            IntPtr foundDirectUi = FindFileListDirectUiChild(searchRoot);
            if (foundDirectUi == IntPtr.Zero || foundDirectUi == _subclassedDirectUiHwnd)
                return;

            DetachDirectUiSubclass();
            if (NativeMethods.SetWindowSubclass(foundDirectUi, _directUiSubclassDelegate, DirectUiSubclassId, UIntPtr.Zero))
            {
                _subclassedDirectUiHwnd = foundDirectUi;
                SuppressNativeFocusRect();
            }
        }

        private IntPtr GetShellViewWindowHandle() => GetShellViewWindowHandleFor(ActiveBrowser);

        private IntPtr GetShellViewWindowHandleFor(IExplorerBrowser? browserInstance)
        {
            if (_isDestroyed || browserInstance == null)
                return IntPtr.Zero;

            Guid iidShellView = ShellConstants.IID_IShellView;
            if (browserInstance.GetCurrentView(ref iidShellView, out IntPtr psv) != ShellConstants.S_OK || psv == IntPtr.Zero)
                return IntPtr.Zero;

            var sv = (IShellView)Marshal.GetObjectForIUnknown(psv);
            try
            {
                return sv.GetWindow(out IntPtr viewHwnd) == ShellConstants.S_OK ? viewHwnd : IntPtr.Zero;
            }
            finally
            {
                Marshal.ReleaseComObject(sv);
                Marshal.Release(psv);
            }
        }

        private void DetachDirectUiSubclass()
        {
            if (_subclassedDirectUiHwnd != IntPtr.Zero)
            {
                if (NativeMethods.IsWindow(_subclassedDirectUiHwnd))
                {
                    NativeMethods.RemoveWindowSubclass(_subclassedDirectUiHwnd, _directUiSubclassDelegate, DirectUiSubclassId);
                }
                _subclassedDirectUiHwnd = IntPtr.Zero;
            }
        }

        private static IntPtr FindFileListDirectUiChild(IntPtr rootHwnd)
        {
            IntPtr match = IntPtr.Zero;
            var sb = new StringBuilder(64);
            var parentSb = new StringBuilder(64);

            NativeMethods.EnumChildWindows(rootHwnd, (child, _) =>
            {
                sb.Clear();
                if (NativeMethods.GetClassNameW(child, sb, sb.Capacity) > 0 &&
                    string.Equals(sb.ToString(), "DirectUIHWND", StringComparison.Ordinal))
                {
                    IntPtr parent = NativeMethods.GetParent(child);
                    parentSb.Clear();
                    if (parent != IntPtr.Zero &&
                        NativeMethods.GetClassNameW(parent, parentSb, parentSb.Capacity) > 0 &&
                        string.Equals(parentSb.ToString(), "SHELLDLL_DefView", StringComparison.Ordinal))
                    {
                        match = child;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);

            return match;
        }

        private IntPtr DirectUiSubclassProc(
            IntPtr hWnd,
            uint uMsg,
            IntPtr wParam,
            IntPtr lParam,
            UIntPtr uIdSubclass,
            UIntPtr dwRefData)
        {
            if (uMsg == NativeMethods.WM_NCDESTROY)
            {
                NativeMethods.RemoveWindowSubclass(hWnd, _directUiSubclassDelegate, DirectUiSubclassId);
                if (_subclassedDirectUiHwnd == hWnd)
                    _subclassedDirectUiHwnd = IntPtr.Zero;
                return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
            }

            bool checkFocusParkAfter = false;
            if (!_isDestroyed)
            {
                if (uMsg == NativeMethods.WM_LBUTTONDOWN ||
                    uMsg == NativeMethods.WM_RBUTTONDOWN ||
                    uMsg == NativeMethods.WM_MBUTTONDOWN)
                {
                    _isMouseDownInView = true;
                    _isKeyboardFocusActive = false;
                }
                else if (uMsg == NativeMethods.WM_LBUTTONUP ||
                         uMsg == NativeMethods.WM_RBUTTONUP ||
                         uMsg == NativeMethods.WM_MBUTTONUP)
                {
                    _isMouseDownInView = false;
                    checkFocusParkAfter = true;
                }
                else if (uMsg == NativeMethods.WM_SETFOCUS)
                {
                    checkFocusParkAfter = true;
                }

                if (uMsg == NativeMethods.WM_MBUTTONUP || uMsg == NativeMethods.WM_LBUTTONDBLCLK)
                {
                    POINT ptScreen = NativeMethods.PointFromLParam(lParam);
                    if (NativeMethods.ClientToScreen(hWnd, ref ptScreen))
                    {
                        int gen = _viewGeneration;
                        GestureRequestKind kind = uMsg == NativeMethods.WM_MBUTTONUP
                            ? GestureRequestKind.MiddleClickFolder
                            : GestureRequestKind.DoubleClickBlank;

                        UiaGestureDetector.Shared.EnqueueGestureQuery(kind, hWnd, ptScreen, gen, OnUiaGestureEvaluatedFromMta);
                    }
                }
                else if (uMsg == NativeMethods.WM_LBUTTONUP ||
                         uMsg == NativeMethods.WM_RBUTTONUP ||
                         uMsg == NativeMethods.WM_KEYUP)
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        if (!_isDestroyed)
                            ViewInteractionOccurred?.Invoke(this, EventArgs.Empty);
                    }));
                }
            }

            IntPtr result = NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
            if (checkFocusParkAfter && !_isDestroyed)
            {
                ClearUnselectedItemFocusBoxIfNeeded();
            }
            return result;
        }

        private void OnUiaGestureEvaluatedFromMta(UiaHitResult result, int expectedGeneration)
        {
            if (_isDestroyed) return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isDestroyed || expectedGeneration != _viewGeneration)
                    return;

                ApplyVerifiedUiaResult(result);
            }));
        }

        internal bool ApplyVerifiedUiaResult(UiaHitResult result)
        {
            if (_isDestroyed) return false;

            if (result.Outcome == UiaHitOutcome.HitBlankView)
            {
                BlankAreaDoubleClicked?.Invoke(this, result.ElapsedMs);
                return NavigateUp();
            }

            if (result.Outcome == UiaHitOutcome.HitItem)
            {
                IntPtr folderPidl = TryResolveUnambiguousFolderPidl(result);
                if (folderPidl != IntPtr.Zero)
                {
                    try
                    {
                        FolderMiddleClicked?.Invoke(this, new FolderMiddleClickedEventArgs(folderPidl, result.ElapsedMs));
                        return true;
                    }
                    finally
                    {
                        PidlManager.Free(ref folderPidl);
                    }
                }
            }

            return false;
        }

        internal IntPtr TryResolveUnambiguousFolderPidl(UiaHitResult hit)
        {
            IExplorerBrowser? active = ActiveBrowser;
            if (_isDestroyed || active == null)
                return IntPtr.Zero;

            Guid iidFolderView = ShellConstants.IID_IFolderView;
            if (active.GetCurrentView(ref iidFolderView, out IntPtr pfv) != ShellConstants.S_OK || pfv == IntPtr.Zero)
                return IntPtr.Zero;

            var fv = (IFolderView)Marshal.GetObjectForIUnknown(pfv);
            IntPtr pPersist = IntPtr.Zero;
            IntPtr pShellFolder = IntPtr.Zero;
            IntPtr curFolderPidl = IntPtr.Zero;
            IPersistFolder2? persist = null;
            IShellFolder? shellFolder = null;

            try
            {
                Guid iidPersist = ShellConstants.IID_IPersistFolder2;
                if (fv.GetFolder(ref iidPersist, out pPersist) != ShellConstants.S_OK || pPersist == IntPtr.Zero)
                    return IntPtr.Zero;

                persist = (IPersistFolder2)Marshal.GetObjectForIUnknown(pPersist);
                if (persist.GetCurFolder(out curFolderPidl) != ShellConstants.S_OK || curFolderPidl == IntPtr.Zero)
                    return IntPtr.Zero;

                Guid iidShellFolder = ShellConstants.IID_IShellFolder;
                if (fv.GetFolder(ref iidShellFolder, out pShellFolder) == ShellConstants.S_OK && pShellFolder != IntPtr.Zero)
                {
                    shellFolder = (IShellFolder)Marshal.GetObjectForIUnknown(pShellFolder);
                }

                if (int.TryParse(hit.ItemAutomationId, out int autoIdx) && autoIdx >= 0)
                {
                    var check = InspectCandidateAt(fv, curFolderPidl, shellFolder, autoIdx, hit.ItemName);
                    if (check.NameMatched)
                        return check.FolderFullPidl;
                }

                if (hit.PositionInSet > 0)
                {
                    var check = InspectCandidateAt(fv, curFolderPidl, shellFolder, hit.PositionInSet - 1, hit.ItemName);
                    if (check.NameMatched)
                        return check.FolderFullPidl;
                }

                if (string.IsNullOrEmpty(hit.ItemName))
                    return IntPtr.Zero;

                if (fv.ItemCount(ShellConstants.SVGIO_ALLVIEW, out int totalCount) != ShellConstants.S_OK ||
                    totalCount <= 0 || totalCount > 5000)
                {
                    return IntPtr.Zero;
                }

                IntPtr uniqueFolderMatch = IntPtr.Zero;
                int nameMatchCount = 0;

                for (int i = 0; i < totalCount; i++)
                {
                    var check = InspectCandidateAt(fv, curFolderPidl, shellFolder, i, hit.ItemName);
                    if (!check.NameMatched)
                        continue;

                    nameMatchCount++;
                    if (nameMatchCount > 1)
                    {
                        PidlManager.Free(ref uniqueFolderMatch);
                        IntPtr extra = check.FolderFullPidl;
                        PidlManager.Free(ref extra);
                        return IntPtr.Zero;
                    }

                    uniqueFolderMatch = check.FolderFullPidl;
                }

                return nameMatchCount == 1 ? uniqueFolderMatch : IntPtr.Zero;
            }
            finally
            {
                PidlManager.Free(ref curFolderPidl);
                if (shellFolder != null) Marshal.ReleaseComObject(shellFolder);
                if (pShellFolder != IntPtr.Zero) Marshal.Release(pShellFolder);
                if (persist != null) Marshal.ReleaseComObject(persist);
                if (pPersist != IntPtr.Zero) Marshal.Release(pPersist);
                Marshal.ReleaseComObject(fv);
                Marshal.Release(pfv);
            }
        }

        private static (bool NameMatched, IntPtr FolderFullPidl) InspectCandidateAt(
            IFolderView fv,
            IntPtr curFolderPidl,
            IShellFolder? shellFolder,
            int index,
            string expectedName)
        {
            if (fv.Item(index, out IntPtr childPidl) != ShellConstants.S_OK || childPidl == IntPtr.Zero)
                return (false, IntPtr.Zero);

            try
            {
                Guid iidShellItem = ShellConstants.IID_IShellItem;
                if (NativeMethods.SHCreateItemWithParent(curFolderPidl, shellFolder, childPidl, ref iidShellItem, out IShellItem item) != ShellConstants.S_OK || item == null)
                    return (false, IntPtr.Zero);

                try
                {
                    string? normalName = PidlManager.GetShellItemString(item, SIGDN.SIGDN_NORMALDISPLAY);
                    string? editName = PidlManager.GetShellItemString(item, SIGDN.SIGDN_PARENTRELATIVEEDITING);

                    bool matched =
                        !string.IsNullOrEmpty(expectedName) &&
                        (string.Equals(normalName, expectedName, StringComparison.Ordinal) ||
                         string.Equals(editName, expectedName, StringComparison.Ordinal));

                    if (!matched)
                        return (false, IntPtr.Zero);

                    uint mask = ShellConstants.SFGAO_FOLDER | ShellConstants.SFGAO_STREAM;
                    int attrHr = item.GetAttributes(mask, out uint attrs);
                    if (attrHr >= 0)
                    {
                        bool isNavigableFolder =
                            (attrs & ShellConstants.SFGAO_FOLDER) != 0 &&
                            (attrs & ShellConstants.SFGAO_STREAM) == 0;

                        if (isNavigableFolder)
                        {
                            // Prefer resolving true target PIDL (handles ResultsFolder search items cleanly)
                            if (NativeMethods.SHGetIDListFromObject(item, out IntPtr realPidl) == ShellConstants.S_OK && realPidl != IntPtr.Zero)
                            {
                                string? fsPath = PidlManager.GetShellItemString(item, SIGDN.SIGDN_FILESYSPATH);
                                if (!string.IsNullOrEmpty(fsPath) && NativeMethods.SHParseDisplayName(fsPath!, IntPtr.Zero, out IntPtr fsPidl, 0, out _) == ShellConstants.S_OK && fsPidl != IntPtr.Zero)
                                {
                                    PidlManager.Free(ref realPidl);
                                    return (true, fsPidl);
                                }
                                return (true, realPidl);
                            }

                            return (true, PidlManager.Combine(curFolderPidl, childPidl));
                        }
                    }

                    return (true, IntPtr.Zero);
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }
            finally
            {
                PidlManager.Free(ref childPidl);
            }
        }

        private void DestroySearchBrowserOnly()
        {
            if (_searchEventSink != null)
            {
                _searchEventSink.Detach();
                _searchEventSink = null;
            }

            if (_searchBrowser != null)
            {
                try
                {
                    if (_searchAdviseCookie != 0)
                    {
                        _searchBrowser.Unadvise(_searchAdviseCookie);
                        _searchAdviseCookie = 0;
                    }
                    if (_searchBrowser is IObjectWithSite withSite)
                    {
                        withSite.SetSite(null);
                    }
                    _searchBrowser.Destroy();
                }
                catch
                {
                }
                finally
                {
                    Marshal.FinalReleaseComObject(_searchBrowser);
                    _searchBrowser = null;
                }
            }

            if (_searchHostHwnd != IntPtr.Zero)
            {
                if (NativeMethods.IsWindow(_searchHostHwnd))
                    NativeMethods.DestroyWindow(_searchHostHwnd);
                _searchHostHwnd = IntPtr.Zero;
            }
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            _isDestroyed = true;
            CancelSearchStateInternal(freeSavedPidl: true);
            DetachDirectUiSubclass();

            PidlManager.Free(ref _pendingInitialPidl);

            DestroySearchBrowserOnly();
            DestroyPrimaryBrowserOnly();

            if (_paneSite != null)
            {
                _paneSite = null;
            }

            if (_browseHostHwnd != IntPtr.Zero)
            {
                if (NativeMethods.IsWindow(_browseHostHwnd))
                    NativeMethods.DestroyWindow(_browseHostHwnd);
                _browseHostHwnd = IntPtr.Zero;
            }

            if (hwnd.Handle != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(hwnd.Handle);
                _hostHwnd = IntPtr.Zero;
            }
        }

        private sealed class ExplorerPaneVisibilitySite : LeafTab.Interop.IServiceProvider, IExplorerPaneVisibility
        {
            private const uint EPS_DEFAULT_ON = 0x1;
            private const uint EPS_DEFAULT_OFF = 0x2;
            private const uint EPS_INITIALSTATE = 0x10000;
            private const uint EPS_FORCE = 0x20000;
            private const int E_NOINTERFACE = unchecked((int)0x80004002);

            private static readonly Guid SID_ExplorerPaneVisibility = new Guid("e07010ec-bc17-44c0-97b0-46c7c95b9edc");
            private static readonly Guid EP_NavPane = new Guid("cb316b22-25f7-42b8-8a09-540d23a43c2f");

            public int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject)
            {
                if (guidService == SID_ExplorerPaneVisibility)
                {
                    IntPtr unk = Marshal.GetIUnknownForObject(this);
                    try
                    {
                        return Marshal.QueryInterface(unk, ref riid, out ppvObject);
                    }
                    finally
                    {
                        Marshal.Release(unk);
                    }
                }

                ppvObject = IntPtr.Zero;
                return E_NOINTERFACE;
            }

            public int GetPaneState(ref Guid ep, out uint peps)
            {
                if (ep == EP_NavPane)
                {
                    peps = EPS_DEFAULT_ON | EPS_INITIALSTATE;
                    return ShellConstants.S_OK;
                }

                // Always keep Windows Shell's built-in preview pane disabled; LeafTab renders its own WPF preview sidebar.
                peps = EPS_DEFAULT_OFF | EPS_FORCE;
                return ShellConstants.S_OK;
            }
        }

        private sealed class BrowserEventSink : IExplorerBrowserEvents
        {
            private WeakReference<ExplorerBrowserHost>? _ownerRef;

            public BrowserEventSink(ExplorerBrowserHost owner) =>
                _ownerRef = new WeakReference<ExplorerBrowserHost>(owner);

            public void Detach() => _ownerRef = null;

            public int OnNavigationPending(IntPtr pidlFolder) => ShellConstants.S_OK;

            public int OnViewCreated(IntPtr psv)
            {
                if (_ownerRef != null && _ownerRef.TryGetTarget(out ExplorerBrowserHost? owner))
                    owner.OnNativeViewCreated();
                return ShellConstants.S_OK;
            }

            public int OnNavigationComplete(IntPtr pidlFolder)
            {
                if (_ownerRef != null && _ownerRef.TryGetTarget(out ExplorerBrowserHost? owner))
                    owner.OnNativeNavigationComplete(pidlFolder);
                return ShellConstants.S_OK;
            }

            public int OnNavigationFailed(IntPtr pidlFolder)
            {
                if (_ownerRef != null && _ownerRef.TryGetTarget(out ExplorerBrowserHost? owner))
                    owner.OnNativeNavigationFailed(pidlFolder);
                return ShellConstants.S_OK;
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

                // User double-clicked a subfolder or clicked a location in left NavPane while viewing search results:
                // keep _searchBrowser in ResultsFolder mode and route navigation to the primary _browser!
                if (pidlFolder != IntPtr.Zero)
                {
                    IntPtr cloned = PidlManager.Clone(pidlFolder);
                    if (cloned != IntPtr.Zero)
                    {
                        owner.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                if (!owner._isDestroyed)
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
                    owner._isFillSearchTransition = false;
                    owner.OnNativeViewCreated();
                }
                return ShellConstants.S_OK;
            }

            public int OnNavigationFailed(IntPtr pidlFolder) => ShellConstants.S_OK;
        }
    }
}
