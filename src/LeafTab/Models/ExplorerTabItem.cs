using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using LeafTab.Controls;
using LeafTab.Interop;

namespace LeafTab.Models
{
    internal sealed class TabHistoryEntry
    {
        public string ParsingName { get; set; } = string.Empty;
        public byte[] PidlBytes { get; set; } = Array.Empty<byte>();
    }

    internal sealed class ClosedTabSnapshot
    {
        public string CurrentPath { get; set; } = string.Empty;
        public byte[] CurrentPidlBytes { get; set; } = Array.Empty<byte>();
        public List<TabHistoryEntry> History { get; set; } = new List<TabHistoryEntry>();
        public int HistoryIndex { get; set; }
    }

    public sealed class ExplorerTabItem : INotifyPropertyChanged, IDisposable
    {
        private const int MaxHistoryEntries = 50;

        private string _title = "此电脑";
        private string _addressText = "此电脑";
        private string _parsingName = string.Empty;
        private string? _fileSystemPath;
        private string _toolTipText = "此电脑";
        private string _searchQuery = string.Empty;
        private ImageSource? _icon;
        private bool _isSelected;
        private bool _isDragHoverTarget;
        private IntPtr _currentPidl;
        private bool _disposed;

        private readonly List<TabHistoryEntry> _history = new List<TabHistoryEntry>();
        private int _historyIndex = -1;
        private int _pendingHistoryIndex = -1;
        private bool _suppressNextHistoryPush;
        private bool _initialNavigationCompleted;

        public Guid Id { get; } = Guid.NewGuid();

        public ExplorerBrowserHost BrowserHost { get; }

        public string Title
        {
            get => _title;
            set { if (_title != value) { _title = value; OnPropertyChanged(); } }
        }

        public string AddressText
        {
            get => _addressText;
            set { if (_addressText != value) { _addressText = value; OnPropertyChanged(); } }
        }

        public string ParsingName => _parsingName;

        public string? FileSystemPath => _fileSystemPath;

        public string ToolTipText
        {
            get => _toolTipText;
            set { if (_toolTipText != value) { _toolTipText = value; OnPropertyChanged(); } }
        }

        public string SearchQuery
        {
            get => _searchQuery;
            set { if (_searchQuery != value) { _searchQuery = value; OnPropertyChanged(); } }
        }

        public ImageSource? Icon
        {
            get => _icon;
            set { if (!ReferenceEquals(_icon, value)) { _icon = value; OnPropertyChanged(); } }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
        }

        public bool IsDragHoverTarget
        {
            get => _isDragHoverTarget;
            set { if (_isDragHoverTarget != value) { _isDragHoverTarget = value; OnPropertyChanged(); } }
        }

        public IntPtr CurrentPidl => _currentPidl;

        public bool CanNavigateBack => BrowserHost.IsShowingSearchResults || _historyIndex > 0;

        public bool CanNavigateForward => !BrowserHost.IsShowingSearchResults && _historyIndex >= 0 && _historyIndex < _history.Count - 1;

        public bool CanNavigateUp => BrowserHost.IsShowingSearchResults || PidlManager.CanNavigateUp(_currentPidl);

        public ExplorerTabItem(IntPtr initialPidl, bool showPreviewPane = false, ExplorerBrowserHost? prewarmedHost = null)
        {
            _currentPidl = PidlManager.Clone(initialPidl);
            if (_currentPidl == IntPtr.Zero)
            {
                _currentPidl = PidlManager.CreateThisPcPidl();
            }

            UpdateDisplayMetadata(_currentPidl);
            BrowserHost = prewarmedHost ?? new ExplorerBrowserHost(_currentPidl, showPreviewPane);
            if (prewarmedHost != null)
            {
                IntPtr thisPcPidl = PidlManager.CreateThisPcPidl();
                try
                {
                    bool isAlreadyAtTarget = thisPcPidl != IntPtr.Zero && PidlManager.AreEqual(_currentPidl, thisPcPidl);
                    if (isAlreadyAtTarget)
                    {
                        _initialNavigationCompleted = prewarmedHost.DirectUiHwnd != IntPtr.Zero;
                        RecordHistory(_currentPidl, _parsingName);
                    }
                    else
                    {
                        _initialNavigationCompleted = false;
                        RecordHistory(_currentPidl, _parsingName);
                        prewarmedHost.NavigateToPidl(_currentPidl);
                    }
                }
                finally
                {
                    PidlManager.Free(ref thisPcPidl);
                }
            }
        }

        internal void SuppressNextHistoryRecord() => _suppressNextHistoryPush = true;

        internal void NotifyNavigationFailed()
        {
            _pendingHistoryIndex = -1;
            _suppressNextHistoryPush = false;
        }

        internal void UpdateFromNavigation(IntPtr navigatedPidl)
        {
            if (_disposed || navigatedPidl == IntPtr.Zero)
                return;

            _initialNavigationCompleted = true;
            PidlManager.Free(ref _currentPidl);
            _currentPidl = PidlManager.Clone(navigatedPidl);
            UpdateDisplayMetadata(_currentPidl);

            if (_pendingHistoryIndex >= 0 && _pendingHistoryIndex < _history.Count)
            {
                _historyIndex = _pendingHistoryIndex;
                _pendingHistoryIndex = -1;
                _suppressNextHistoryPush = false;
                if (_history[_historyIndex].PidlBytes.Length == 0)
                {
                    _history[_historyIndex].PidlBytes = PidlManager.ToBytes(_currentPidl);
                }
                return;
            }

            _pendingHistoryIndex = -1;

            if (_suppressNextHistoryPush)
            {
                _suppressNextHistoryPush = false;
                if (_historyIndex >= 0 && _historyIndex < _history.Count && _history[_historyIndex].PidlBytes.Length == 0)
                {
                    _history[_historyIndex].PidlBytes = PidlManager.ToBytes(_currentPidl);
                }
                return;
            }

            RecordHistory(_currentPidl, _parsingName);
        }

        private void RecordHistory(IntPtr pidl, string parsingName)
        {
            if (pidl == IntPtr.Zero)
                return;

            if (_historyIndex >= 0 && _historyIndex < _history.Count)
            {
                IntPtr curEntryPidl = PidlManager.FromBytes(_history[_historyIndex].PidlBytes);
                try
                {
                    if (curEntryPidl != IntPtr.Zero && PidlManager.AreEqual(curEntryPidl, pidl))
                        return;
                    if (curEntryPidl == IntPtr.Zero &&
                        string.Equals(_history[_historyIndex].ParsingName, parsingName, StringComparison.OrdinalIgnoreCase))
                    {
                        _history[_historyIndex].PidlBytes = PidlManager.ToBytes(pidl);
                        return;
                    }
                }
                finally
                {
                    PidlManager.Free(ref curEntryPidl);
                }
            }

            if (_historyIndex < _history.Count - 1)
            {
                _history.RemoveRange(_historyIndex + 1, _history.Count - (_historyIndex + 1));
            }

            _history.Add(new TabHistoryEntry
            {
                ParsingName = parsingName,
                PidlBytes = PidlManager.ToBytes(pidl)
            });

            if (_history.Count > MaxHistoryEntries)
            {
                _history.RemoveAt(0);
            }

            _historyIndex = _history.Count - 1;
        }

        internal bool NavigateHistoryStep(int delta)
        {
            if (_disposed)
                return false;

            if (BrowserHost.IsShowingSearchResults && delta < 0)
            {
                SearchQuery = string.Empty;
                return BrowserHost.ExitSearchMode();
            }

            int baseIdx = _pendingHistoryIndex >= 0 ? _pendingHistoryIndex : _historyIndex;
            int targetIdx = baseIdx + delta;
            if (targetIdx < 0 || targetIdx >= _history.Count)
                return false;

            TabHistoryEntry entry = _history[targetIdx];
            IntPtr targetPidl = PidlManager.FromBytes(entry.PidlBytes);
            if (targetPidl == IntPtr.Zero && !string.IsNullOrWhiteSpace(entry.ParsingName))
            {
                targetPidl = PidlManager.TryParseLocalOrShellFast(entry.ParsingName);
            }

            if (targetPidl == IntPtr.Zero)
                return false;

            try
            {
                _pendingHistoryIndex = targetIdx;
                _suppressNextHistoryPush = true;
                SearchQuery = string.Empty;
                if (!BrowserHost.NavigateToPidl(targetPidl))
                {
                    _pendingHistoryIndex = -1;
                    _suppressNextHistoryPush = false;
                    return false;
                }
                return true;
            }
            finally
            {
                PidlManager.Free(ref targetPidl);
            }
        }

        internal void RestoreHistory(IReadOnlyList<string>? historyPaths, int savedIndex)
        {
            _history.Clear();
            _pendingHistoryIndex = -1;

            if (historyPaths != null)
            {
                foreach (string p in historyPaths)
                {
                    if (!string.IsNullOrWhiteSpace(p))
                    {
                        _history.Add(new TabHistoryEntry { ParsingName = p });
                    }
                }
            }

            if (_history.Count == 0)
            {
                _history.Add(new TabHistoryEntry
                {
                    ParsingName = !string.IsNullOrWhiteSpace(_fileSystemPath) ? _fileSystemPath! : _parsingName,
                    PidlBytes = PidlManager.ToBytes(_currentPidl)
                });
                _historyIndex = 0;
                _suppressNextHistoryPush = !_initialNavigationCompleted;
                return;
            }

            _historyIndex = Math.Max(0, Math.Min(savedIndex, _history.Count - 1));
            _history[_historyIndex].PidlBytes = PidlManager.ToBytes(_currentPidl);
            _suppressNextHistoryPush = !_initialNavigationCompleted;
        }

        internal void RestoreHistoryFromSnapshot(ClosedTabSnapshot snapshot)
        {
            _history.Clear();
            _pendingHistoryIndex = -1;

            foreach (var h in snapshot.History)
            {
                _history.Add(new TabHistoryEntry
                {
                    ParsingName = h.ParsingName,
                    PidlBytes = h.PidlBytes
                });
            }

            if (_history.Count == 0)
            {
                _history.Add(new TabHistoryEntry
                {
                    ParsingName = !string.IsNullOrWhiteSpace(_fileSystemPath) ? _fileSystemPath! : _parsingName,
                    PidlBytes = PidlManager.ToBytes(_currentPidl)
                });
                _historyIndex = 0;
                _suppressNextHistoryPush = !_initialNavigationCompleted;
                return;
            }

            _historyIndex = Math.Max(0, Math.Min(snapshot.HistoryIndex, _history.Count - 1));
            _suppressNextHistoryPush = !_initialNavigationCompleted;
        }

        internal ClosedTabSnapshot CreateClosedSnapshot()
        {
            var copy = new List<TabHistoryEntry>(_history.Count);
            foreach (var h in _history)
            {
                copy.Add(new TabHistoryEntry
                {
                    ParsingName = h.ParsingName,
                    PidlBytes = h.PidlBytes
                });
            }

            return new ClosedTabSnapshot
            {
                CurrentPath = GetPersistablePath(),
                CurrentPidlBytes = PidlManager.ToBytes(_currentPidl),
                History = copy,
                HistoryIndex = _historyIndex
            };
        }

        internal SavedTabConfigItem ExportSavedConfig()
        {
            var paths = new List<string>(_history.Count);
            foreach (var h in _history)
            {
                if (!string.IsNullOrWhiteSpace(h.ParsingName))
                    paths.Add(h.ParsingName);
            }

            string curPath = GetPersistablePath();
            if (paths.Count == 0 && !string.IsNullOrWhiteSpace(curPath))
            {
                paths.Add(curPath);
            }

            return new SavedTabConfigItem
            {
                CurrentPath = curPath,
                HistoryPaths = paths,
                HistoryIndex = Math.Max(0, Math.Min(_historyIndex, Math.Max(0, paths.Count - 1)))
            };
        }

        public string GetPersistablePath() =>
            !string.IsNullOrWhiteSpace(_fileSystemPath)
                ? _fileSystemPath!
                : (!string.IsNullOrWhiteSpace(_parsingName) ? _parsingName : _addressText);

        private void UpdateDisplayMetadata(IntPtr pidl)
        {
            FolderDisplayInfo info = PidlManager.GetDisplayInfo(pidl);
            Title = string.IsNullOrWhiteSpace(info.Title) ? "文件夹" : info.Title;
            AddressText = info.AddressText;
            _parsingName = info.ParsingName;
            _fileSystemPath = info.FileSystemPath;
            ToolTipText = string.IsNullOrWhiteSpace(info.AddressText)
                ? Title
                : $"{Title}\n{info.AddressText}";
            Icon = info.Icon;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            BrowserHost.Dispose();
            PidlManager.Free(ref _currentPidl);
            _history.Clear();
            _icon = null;
        }
    }
}
