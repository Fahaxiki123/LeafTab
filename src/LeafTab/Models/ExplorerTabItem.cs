using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using LeafTab.Controls;
using LeafTab.Interop;

namespace LeafTab.Models
{
    public sealed class ExplorerTabItem : INotifyPropertyChanged, IDisposable
    {
        private string _title = "此电脑";
        private string _addressText = "此电脑";
        private string _toolTipText = "此电脑";
        private ImageSource? _icon;
        private bool _isSelected;
        private bool _isDragHoverTarget;
        private IntPtr _currentPidl;
        private bool _disposed;

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

        public string ToolTipText
        {
            get => _toolTipText;
            set { if (_toolTipText != value) { _toolTipText = value; OnPropertyChanged(); } }
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

        public ExplorerTabItem(IntPtr initialPidl)
        {
            _currentPidl = PidlManager.Clone(initialPidl);
            if (_currentPidl == IntPtr.Zero)
            {
                _currentPidl = PidlManager.CreateThisPcPidl();
            }

            UpdateDisplayMetadata(_currentPidl);
            BrowserHost = new ExplorerBrowserHost(_currentPidl);
        }

        internal void UpdateFromNavigation(IntPtr navigatedPidl)
        {
            if (_disposed || navigatedPidl == IntPtr.Zero)
                return;

            PidlManager.Free(ref _currentPidl);
            _currentPidl = PidlManager.Clone(navigatedPidl);
            UpdateDisplayMetadata(_currentPidl);
        }

        private void UpdateDisplayMetadata(IntPtr pidl)
        {
            FolderDisplayInfo info = PidlManager.GetDisplayInfo(pidl);
            Title = string.IsNullOrWhiteSpace(info.Title) ? "文件夹" : info.Title;
            AddressText = info.AddressText;
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
            _icon = null;
        }
    }
}
