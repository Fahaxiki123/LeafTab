using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LeafTab.Interop;

namespace LeafTab.Controls
{
    public sealed partial class ExplorerBrowserHost
    {
        private int _externalNavigationSerial;
        private int _externalSelectionSerial;
        private bool _externalNavigationPending;
        private bool _externalNavigationFailed;
        private IntPtr _externalPendingPidl;
        private IntPtr _externalCompletedPidl;

        public event EventHandler? NavigationStarting;

        private void TrackExternalNavigationPending(IntPtr pidl, bool newRequest = false)
        {
            // BrowseToIDList can pump queued callbacks before raising Pending.
            // Explicit requests register before entering COM; their matching
            // native notification must not start a second navigation generation.
            if (!newRequest && _externalNavigationPending && _externalPendingPidl != IntPtr.Zero &&
                PidlManager.AreEqual(_externalPendingPidl, pidl)) return;
            _externalNavigationSerial++;
            _externalSelectionSerial++;
            _externalNavigationPending = true;
            _externalNavigationFailed = false;
            PidlManager.Free(ref _externalPendingPidl);
            _externalPendingPidl = PidlManager.Clone(pidl);
            NavigationStarting?.Invoke(this, EventArgs.Empty);
        }

        private bool TrackExternalNavigationComplete(IntPtr pidl)
        {
            if (_externalPendingPidl != IntPtr.Zero &&
                !PidlManager.AreEqual(_externalPendingPidl, pidl)) return false;
            _externalNavigationPending = false;
            _externalNavigationFailed = false;
            PidlManager.Free(ref _externalCompletedPidl);
            _externalCompletedPidl = PidlManager.Clone(pidl);
            return true;
        }

        // A browser accepting BrowseToIDList is not proof that the folder is open.
        // A redirected Explorer window may only close after this check succeeds.
        internal bool IsFolderNavigationComplete(IntPtr expectedPidl) =>
            !_isDestroyed && !_externalNavigationPending && !_externalNavigationFailed && !_isShowingSearchResults &&
            expectedPidl != IntPtr.Zero && _externalCompletedPidl != IntPtr.Zero &&
            PidlManager.AreEqual(_externalCompletedPidl, expectedPidl);

        internal async Task<bool> WaitForFolderNavigationAsync(IntPtr expectedPidl, int timeoutMs = 5000,
            CancellationToken cancellationToken = default)
        {
            if (expectedPidl == IntPtr.Zero) return false;
            int initialSerial = _externalNavigationSerial;
            var timer = Stopwatch.StartNew();
            while (!_isDestroyed && !cancellationToken.IsCancellationRequested && timer.ElapsedMilliseconds <= timeoutMs)
            {
                if (_externalNavigationFailed) return false;
                if (IsFolderNavigationComplete(expectedPidl))
                    return true;
                if (_externalNavigationSerial != initialSerial &&
                    (_externalPendingPidl == IntPtr.Zero || !PidlManager.AreEqual(_externalPendingPidl, expectedPidl)))
                    return false; // The user or a newer request navigated somewhere else.
                await Task.Delay(60).ConfigureAwait(true);
            }
            return false;
        }

        public async Task<bool> SelectChildItemsWhenReadyAsync(IEnumerable<string> childFileNames,
            CancellationToken cancellationToken = default, int timeoutMs = 5000)
        {
            if (childFileNames == null) return false;
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in childFileNames)
                if (!string.IsNullOrWhiteSpace(name) && seen.Add(name)) names.Add(name);
            if (names.Count == 0 || names.Count > 128 || _isDestroyed) return false;

            int selectionSerial = ++_externalSelectionSerial;
            int navigationSerial = _externalNavigationSerial;
            bool searchView = _isShowingSearchResults;
            var initialVisibility = Visibility;
            var timer = Stopwatch.StartNew();
            EventHandler cancelForInteraction = (_, _) => _externalSelectionSerial++;
            ViewInteractionOccurred += cancelForInteraction;
            try
            {
                while (!_isDestroyed && !cancellationToken.IsCancellationRequested &&
                       selectionSerial == _externalSelectionSerial && navigationSerial == _externalNavigationSerial &&
                       searchView == _isShowingSearchResults &&
                       Visibility == initialVisibility &&
                       timer.ElapsedMilliseconds <= timeoutMs)
                {
                    if (!_externalNavigationPending)
                    {
                        for (int i = 0; i < names.Count; i++)
                            TrySelectChildByName(names[i], beginRename: false, deselectOthers: i == 0, focusItem: i == 0);

                        if (SelectionContainsAll(names, GetSelectedPaths()))
                        {
                            InvalidateSelectionCache();
                            FocusNativeView();
                            return true;
                        }
                    }
                    await Task.Delay(80).ConfigureAwait(true);
                }
                return false;
            }
            finally { ViewInteractionOccurred -= cancelForInteraction; }
        }

        internal static bool SelectionContainsAll(IReadOnlyList<string> requested, IReadOnlyList<string> selectedPaths)
        {
            if (selectedPaths.Count != requested.Count) return false;
            var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in selectedPaths)
            {
                selected.Add(path);
                selected.Add(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            }
            foreach (string name in requested) if (!selected.Contains(name)) return false;
            return true;
        }

        private void DisposeExternalNavigationTracking()
        {
            _externalSelectionSerial++;
            PidlManager.Free(ref _externalPendingPidl);
            PidlManager.Free(ref _externalCompletedPidl);
        }
    }
}
