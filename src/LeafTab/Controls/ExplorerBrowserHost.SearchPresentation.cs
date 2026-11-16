using System;
using LeafTab.Interop;

namespace LeafTab.Controls
{
    public sealed partial class ExplorerBrowserHost
    {
        private static readonly UIntPtr BrowseViewLayoutSubclassId = new UIntPtr(0x4C534C59); // LSLY
        private readonly SUBCLASSPROC _browseViewLayoutSubclassDelegate;
        private IntPtr _browseViewLayoutHwnd;
        private bool _syncingSearchOverlay;
        private EXPLORER_BROWSER_OPTIONS? _browseOptionsBeforeSearch;
        private SearchResultsSite? _searchPaneSite;
        private IntPtr _searchResultsPidl;

        private void ShowSearchPresentation()
        {
            if (_browser != null && !_browseOptionsBeforeSearch.HasValue &&
                _browser.GetOptions(out EXPLORER_BROWSER_OPTIONS options) == ShellConstants.S_OK)
            {
                _browseOptionsBeforeSearch = options;
                // Clicking the source directory in the navigation tree must also
                // leave search, even though the primary browser is already there.
                _browser.SetOptions(options | EXPLORER_BROWSER_OPTIONS.EBO_ALWAYSNAVIGATE);
            }

            SyncBrowserRect();
            AttachDirectUiSubclassIfNeeded();
        }

        private void HideSearchPresentation()
        {
            if (_searchHostHwnd != IntPtr.Zero)
                NativeMethods.ShowWindow(_searchHostHwnd, NativeMethods.SW_HIDE);

            IntPtr view = GetShellViewWindowHandleFor(_browser);
            if (view != IntPtr.Zero)
                NativeMethods.ShowWindow(view, NativeMethods.SW_SHOW);

            if (_browseOptionsBeforeSearch.HasValue)
            {
                _browser?.SetOptions(_browseOptionsBeforeSearch.Value);
                _browseOptionsBeforeSearch = null;
            }
        }

        private void SyncSearchOverlayRect()
        {
            if (_syncingSearchOverlay || _isDestroyed || _browser == null ||
                _searchBrowser == null || _searchHostHwnd == IntPtr.Zero)
                return;

            _syncingSearchOverlay = true;
            try
            {
                IntPtr view = GetShellViewWindowHandleFor(_browser);
                if (view != _browseViewLayoutHwnd)
                {
                    DetachBrowseViewLayoutSubclass();
                    if (view != IntPtr.Zero && NativeMethods.SetWindowSubclass(
                        view, _browseViewLayoutSubclassDelegate, BrowseViewLayoutSubclassId, UIntPtr.Zero))
                        _browseViewLayoutHwnd = view;
                }

                if (!_isShowingSearchResults)
                    return;

                // Keep the primary navigation pane and its splitter visible. The
                // anonymous ResultsFolder is a frameless file list, never a second tree.
                if (view == IntPtr.Zero ||
                    !NativeMethods.GetWindowRect(view, out RECT viewScreen) ||
                    !NativeMethods.GetWindowRect(_hostHwnd, out RECT hostScreen) ||
                    !NativeMethods.GetClientRect(_hostHwnd, out RECT hostClient))
                {
                    NativeMethods.ShowWindow(_searchHostHwnd, NativeMethods.SW_HIDE);
                    return;
                }

                int left = Math.Max(0, viewScreen.Left - hostScreen.Left);
                int top = Math.Max(0, viewScreen.Top - hostScreen.Top);
                int right = Math.Min(hostClient.Right, viewScreen.Right - hostScreen.Left);
                int bottom = Math.Min(hostClient.Bottom, viewScreen.Bottom - hostScreen.Top);
                int width = right - left;
                int height = bottom - top;
                if (width <= 0 || height <= 0)
                {
                    NativeMethods.ShowWindow(_searchHostHwnd, NativeMethods.SW_HIDE);
                    return;
                }

                NativeMethods.SetWindowPos(_searchHostHwnd, IntPtr.Zero, left, top, width, height,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
                // The covered source list must not remain keyboard/accessibility
                // reachable behind the results. Its parent and navigation tree stay visible.
                NativeMethods.ShowWindow(view, NativeMethods.SW_HIDE);
                var rect = new RECT(0, 0, width, height);
                IntPtr deferred = IntPtr.Zero;
                _searchBrowser.SetRect(ref deferred, rect);
            }
            finally
            {
                _syncingSearchOverlay = false;
            }
        }

        private IntPtr BrowseViewLayoutSubclassProc(IntPtr hwnd, uint message, IntPtr wParam,
            IntPtr lParam, UIntPtr subclassId, UIntPtr referenceData)
        {
            if (message == NativeMethods.WM_NCDESTROY)
            {
                NativeMethods.RemoveWindowSubclass(hwnd, _browseViewLayoutSubclassDelegate, BrowseViewLayoutSubclassId);
                if (_browseViewLayoutHwnd == hwnd)
                    _browseViewLayoutHwnd = IntPtr.Zero;
            }

            IntPtr result = NativeMethods.DefSubclassProc(hwnd, message, wParam, lParam);
            // Includes splitter moves and native relayout, which do not resize the
            // outer WPF host. Use the native view's final rectangle after processing.
            if (message == NativeMethods.WM_WINDOWPOSCHANGED && _isShowingSearchResults)
                SyncSearchOverlayRect();
            return result;
        }

        private void DetachBrowseViewLayoutSubclass()
        {
            if (_browseViewLayoutHwnd == IntPtr.Zero)
                return;
            if (NativeMethods.IsWindow(_browseViewLayoutHwnd))
                NativeMethods.RemoveWindowSubclass(_browseViewLayoutHwnd,
                    _browseViewLayoutSubclassDelegate, BrowseViewLayoutSubclassId);
            _browseViewLayoutHwnd = IntPtr.Zero;
        }
    }
}
