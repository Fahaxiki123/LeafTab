using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using LeafTab.Interop;

namespace LeafTab.Controls
{
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
    /// Implements:
    /// 1. Clean COM lifecycle (Advise via WeakReference sink + Unadvise + Destroy + FinalReleaseComObject).
    /// 2. IShellView::TranslateAccelerator forwarding for F2, Delete, Ctrl+C/V/X/Z, Alt+Enter, etc.
    /// 3. Dynamic DirectUIHWND subclassing in OnViewCreated/OnNavigationComplete dispatching gesture coordinates
    ///    to the dedicated MTA UIA worker thread (never querying UIA synchronously on the UI thread).
    /// </summary>
    public sealed class ExplorerBrowserHost : HwndHost
    {
        private static readonly UIntPtr DirectUiSubclassId = new UIntPtr(0x4C454146); // 'LEAF'

        private IntPtr _hostHwnd;
        private IExplorerBrowser? _browser;
        private BrowserEventSink? _eventSink;
        private uint _adviseCookie;
        private IntPtr _pendingInitialPidl;

        private IntPtr _subclassedDirectUiHwnd;
        private readonly SUBCLASSPROC _directUiSubclassDelegate;
        private int _viewGeneration;
        private bool _isDestroyed;

        public IntPtr DirectUiHwnd => _subclassedDirectUiHwnd;
        public int ViewGeneration => _viewGeneration;

        public event EventHandler<NavigationCompletedEventArgs>? NavigationCompleted;
        public event EventHandler<string>? NavigationFailed;
        public event EventHandler<FolderMiddleClickedEventArgs>? FolderMiddleClicked;
        public event EventHandler<double>? BlankAreaDoubleClicked;

        public ExplorerBrowserHost(IntPtr initialPidl)
        {
            _pendingInitialPidl = PidlManager.Clone(initialPidl);
            _directUiSubclassDelegate = DirectUiSubclassProc;
            Focusable = true;
        }

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            int visibleFlag = Visibility == Visibility.Visible ? NativeMethods.WS_VISIBLE : 0;
            _hostHwnd = NativeMethods.CreateWindowExW(
                0,
                "Static",
                string.Empty,
                NativeMethods.WS_CHILD | visibleFlag | NativeMethods.WS_CLIPCHILDREN | NativeMethods.WS_CLIPSIBLINGS,
                0,
                0,
                Math.Max(1, (int)ActualWidth),
                Math.Max(1, (int)ActualHeight),
                hwndParent.Handle,
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

            // Enable native left Navigation Pane (EBO_SHOWFRAMES) per Stage 1 design
            _browser.SetOptions(EXPLORER_BROWSER_OPTIONS.EBO_SHOWFRAMES | EXPLORER_BROWSER_OPTIONS.EBO_NOBORDER);

            NativeMethods.GetClientRect(_hostHwnd, out RECT rc);
            if (rc.Width <= 0) rc.Right = 800;
            if (rc.Height <= 0) rc.Bottom = 500;

            var fs = new FOLDERSETTINGS
            {
                ViewMode = FOLDERVIEWMODE.FVM_AUTO,
                fFlags = FOLDERFLAGS.FWF_AUTOARRANGE | FOLDERFLAGS.FWF_SHOWSELALWAYS
            };

            int hr = _browser.Initialize(_hostHwnd, ref rc, ref fs);
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

        protected override void OnWindowPositionChanged(Rect rcBoundingBox)
        {
            base.OnWindowPositionChanged(rcBoundingBox);
            SyncBrowserRect();
        }

        public void SyncBrowserRect()
        {
            if (_isDestroyed || _browser == null || _hostHwnd == IntPtr.Zero)
                return;

            if (NativeMethods.GetClientRect(_hostHwnd, out RECT rc) && rc.Width > 0 && rc.Height > 0)
            {
                IntPtr hdwp = IntPtr.Zero;
                _browser.SetRect(ref hdwp, rc);
            }
        }

        public bool NavigateToPidl(IntPtr pidl)
        {
            if (_isDestroyed || _browser == null || pidl == IntPtr.Zero)
                return false;

            int hr = _browser.BrowseToIDList(pidl, ShellConstants.SBSP_ABSOLUTE | ShellConstants.SBSP_SAMEBROWSER);
            return hr == ShellConstants.S_OK;
        }

        public bool NavigateBack()
        {
            if (_isDestroyed || _browser == null) return false;
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
            return _browser.BrowseToIDList(IntPtr.Zero, ShellConstants.SBSP_PARENT) == ShellConstants.S_OK;
        }

        public void RefreshView()
        {
            if (_isDestroyed || _browser == null) return;

            Guid iidShellView = ShellConstants.IID_IShellView;
            if (_browser.GetCurrentView(ref iidShellView, out IntPtr psv) == ShellConstants.S_OK && psv != IntPtr.Zero)
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

        public (int TotalItems, int SelectedItems) GetItemCounts()
        {
            if (_isDestroyed || _browser == null)
                return (0, 0);

            Guid iidFolderView = ShellConstants.IID_IFolderView;
            if (_browser.GetCurrentView(ref iidFolderView, out IntPtr pfv) != ShellConstants.S_OK || pfv == IntPtr.Zero)
                return (0, 0);

            var fv = (IFolderView)Marshal.GetObjectForIUnknown(pfv);
            try
            {
                fv.ItemCount(ShellConstants.SVGIO_ALLVIEW, out int total);
                fv.ItemCount(ShellConstants.SVGIO_SELECTION, out int selected);
                return (Math.Max(0, total), Math.Max(0, selected));
            }
            finally
            {
                Marshal.ReleaseComObject(fv);
                Marshal.Release(pfv);
            }
        }

        public System.Collections.Generic.List<string> GetDisplayedItemNames(int maxCount = 50)
        {
            var names = new System.Collections.Generic.List<string>();
            if (_isDestroyed || _browser == null)
                return names;

            Guid iidFolderView = ShellConstants.IID_IFolderView;
            if (_browser.GetCurrentView(ref iidFolderView, out IntPtr pfv) != ShellConstants.S_OK || pfv == IntPtr.Zero)
                return names;

            var fv = (IFolderView)Marshal.GetObjectForIUnknown(pfv);
            IntPtr pPersist = IntPtr.Zero;
            IntPtr curFolderPidl = IntPtr.Zero;
            IPersistFolder2? persist = null;

            try
            {
                Guid iidPersist = ShellConstants.IID_IPersistFolder2;
                if (fv.GetFolder(ref iidPersist, out pPersist) != ShellConstants.S_OK || pPersist == IntPtr.Zero)
                    return names;

                persist = (IPersistFolder2)Marshal.GetObjectForIUnknown(pPersist);
                if (persist.GetCurFolder(out curFolderPidl) != ShellConstants.S_OK || curFolderPidl == IntPtr.Zero)
                    return names;

                if (fv.ItemCount(ShellConstants.SVGIO_ALLVIEW, out int total) != ShellConstants.S_OK || total <= 0)
                    return names;

                int limit = Math.Min(total, maxCount);
                Guid iidShellItem = ShellConstants.IID_IShellItem;

                for (int i = 0; i < limit; i++)
                {
                    if (fv.Item(i, out IntPtr childPidl) == ShellConstants.S_OK && childPidl != IntPtr.Zero)
                    {
                        try
                        {
                            if (NativeMethods.SHCreateItemWithParent(curFolderPidl, null, childPidl, ref iidShellItem, out IShellItem item) == ShellConstants.S_OK && item != null)
                            {
                                try
                                {
                                    string? name = PidlManager.GetShellItemString(item, SIGDN.SIGDN_NORMALDISPLAY);
                                    if (!string.IsNullOrEmpty(name))
                                        names.Add(name!);
                                }
                                finally
                                {
                                    Marshal.ReleaseComObject(item);
                                }
                            }
                        }
                        finally
                        {
                            PidlManager.Free(ref childPidl);
                        }
                    }
                }
            }
            finally
            {
                PidlManager.Free(ref curFolderPidl);
                if (persist != null) Marshal.ReleaseComObject(persist);
                if (pPersist != IntPtr.Zero) Marshal.Release(pPersist);
                Marshal.ReleaseComObject(fv);
                Marshal.Release(pfv);
            }

            return names;
        }

        internal bool TrySetViewMode(FOLDERVIEWMODE mode)
        {
            if (_isDestroyed || _browser == null)
                return false;

            Guid iidFolderView = ShellConstants.IID_IFolderView;
            if (_browser.GetCurrentView(ref iidFolderView, out IntPtr pfv) != ShellConstants.S_OK || pfv == IntPtr.Zero)
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

        public void FocusNativeView()
        {
            if (_isDestroyed || _hostHwnd == IntPtr.Zero) return;

            AttachDirectUiSubclassIfNeeded();
            if (_subclassedDirectUiHwnd != IntPtr.Zero && NativeMethods.IsWindow(_subclassedDirectUiHwnd))
            {
                NativeMethods.SetFocus(_subclassedDirectUiHwnd);
                return;
            }

            if (_browser != null)
            {
                Guid iidShellView = ShellConstants.IID_IShellView;
                if (_browser.GetCurrentView(ref iidShellView, out IntPtr psv) == ShellConstants.S_OK && psv != IntPtr.Zero)
                {
                    var sv = (IShellView)Marshal.GetObjectForIUnknown(psv);
                    try
                    {
                        if (sv.GetWindow(out IntPtr viewHwnd) == ShellConstants.S_OK && viewHwnd != IntPtr.Zero)
                        {
                            NativeMethods.SetFocus(viewHwnd);
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(sv);
                        Marshal.Release(psv);
                    }
                }
            }
        }

        public bool IsNativeFocusInside()
        {
            if (_isDestroyed || _hostHwnd == IntPtr.Zero)
                return false;

            IntPtr focused = NativeMethods.GetFocus();
            return focused != IntPtr.Zero && (focused == _hostHwnd || NativeMethods.IsChild(_hostHwnd, focused));
        }

        /// <summary>
        /// Forwards keyboard accelerators (F2, Delete, Ctrl+C/V/X/Z/A, Alt+Enter, Backspace, Enter) to the active IShellView.
        /// Returns true ONLY if IShellView::TranslateAccelerator returns S_OK (meaning consumed), leaving S_FALSE untouched
        /// so inline rename Edit controls continue receiving WM_CHAR characters normally.
        /// </summary>
        public bool TryTranslateShellAccelerator(ref MSG wpfMsg)
        {
            if (_isDestroyed || _browser == null || !IsNativeFocusInside())
                return false;

            Guid iidShellView = ShellConstants.IID_IShellView;
            if (_browser.GetCurrentView(ref iidShellView, out IntPtr psv) != ShellConstants.S_OK || psv == IntPtr.Zero)
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

                return sv.TranslateAccelerator(ref nativeMsg) == ShellConstants.S_OK;
            }
            finally
            {
                Marshal.ReleaseComObject(sv);
                Marshal.Release(psv);
            }
        }

        private void OnNativeViewCreated()
        {
            if (_isDestroyed) return;
            _viewGeneration++;
            AttachDirectUiSubclassIfNeeded();
        }

        private void OnNativeNavigationComplete(IntPtr pidlFolder)
        {
            if (_isDestroyed) return;
            _viewGeneration++;
            AttachDirectUiSubclassIfNeeded();

            IntPtr cloned = PidlManager.Clone(pidlFolder);
            if (cloned == IntPtr.Zero)
                return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (!_isDestroyed)
                    {
                        AttachDirectUiSubclassIfNeeded();
                        NavigationCompleted?.Invoke(this, new NavigationCompletedEventArgs(cloned));
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
                searchRoot = _hostHwnd;

            IntPtr foundDirectUi = FindFileListDirectUiChild(searchRoot);
            if (foundDirectUi == IntPtr.Zero || foundDirectUi == _subclassedDirectUiHwnd)
                return;

            DetachDirectUiSubclass();
            if (NativeMethods.SetWindowSubclass(foundDirectUi, _directUiSubclassDelegate, DirectUiSubclassId, UIntPtr.Zero))
            {
                _subclassedDirectUiHwnd = foundDirectUi;
            }
        }

        private IntPtr GetShellViewWindowHandle()
        {
            if (_isDestroyed || _browser == null)
                return IntPtr.Zero;

            Guid iidShellView = ShellConstants.IID_IShellView;
            if (_browser.GetCurrentView(ref iidShellView, out IntPtr psv) != ShellConstants.S_OK || psv == IntPtr.Zero)
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
                    // With EBO_SHOWFRAMES enabled, ExplorerBrowser also creates an outer DUIView DirectUIHWND
                    // and a bottom Details/Status DirectUIHWND. Strictly match the DirectUIHWND hosted inside SHELLDLL_DefView.
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

            if (!_isDestroyed && (uMsg == NativeMethods.WM_MBUTTONUP || uMsg == NativeMethods.WM_LBUTTONDBLCLK))
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

            return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
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

        /// <summary>
        /// Resolves a UIA-identified UIItem to an unambiguous folder PIDL on the UI STA thread.
        /// Fails closed (returns IntPtr.Zero) if the target item is a plain file/ZIP stream or if matching is ambiguous.
        /// </summary>
        internal IntPtr TryResolveUnambiguousFolderPidl(UiaHitResult hit)
        {
            if (_isDestroyed || _browser == null)
                return IntPtr.Zero;

            Guid iidFolderView = ShellConstants.IID_IFolderView;
            if (_browser.GetCurrentView(ref iidFolderView, out IntPtr pfv) != ShellConstants.S_OK || pfv == IntPtr.Zero)
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

                // 1. Fast O(1) index check via AutomationId or (PositionInSet - 1) validated by display name
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

                // 2. Fallback scan for grouped views (e.g. "此电脑" where PositionInSet is per-group):
                //    Strict fail-closed rule: succeed ONLY if there is a SINGLE exact display-name match.
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
                        // Ambiguous display name: fail closed immediately
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
                        (!string.IsNullOrEmpty(expectedName) &&
                         (string.Equals(normalName, expectedName, StringComparison.Ordinal) ||
                          string.Equals(editName, expectedName, StringComparison.Ordinal)));

                    if (!matched)
                        return (false, IntPtr.Zero);

                    // Note: IShellItem::GetAttributes returns S_OK (0) ONLY when ALL bits in mask match,
                    // and returns S_FALSE (1) when a subset of bits match (e.g. SFGAO_FOLDER without SFGAO_STREAM).
                    uint mask = ShellConstants.SFGAO_FOLDER | ShellConstants.SFGAO_STREAM;
                    int attrHr = item.GetAttributes(mask, out uint attrs);
                    if (attrHr >= 0)
                    {
                        // Real navigable folder/drive/cloud namespace (exclude .zip/.cab SFGAO_STREAM archives)
                        bool isNavigableFolder =
                            (attrs & ShellConstants.SFGAO_FOLDER) != 0 &&
                            (attrs & ShellConstants.SFGAO_STREAM) == 0;

                        if (isNavigableFolder)
                        {
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

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            _isDestroyed = true;
            DetachDirectUiSubclass();

            PidlManager.Free(ref _pendingInitialPidl);

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

            if (hwnd.Handle != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(hwnd.Handle);
                _hostHwnd = IntPtr.Zero;
            }
        }

        private sealed class BrowserEventSink : IExplorerBrowserEvents
        {
            private WeakReference<ExplorerBrowserHost>? _ownerRef;

            public BrowserEventSink(ExplorerBrowserHost owner) =>
                _ownerRef = new WeakReference<ExplorerBrowserHost>(owner);

            public void Detach() => _ownerRef = null;

            public int OnNavigationPending(IntPtr pidlFolder) => ShellConstants.S_OK;

            public int OnViewCreated(IShellView? psv)
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
    }
}
