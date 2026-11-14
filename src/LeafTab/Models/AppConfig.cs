using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Windows.Media;

namespace LeafTab.Models
{
    [DataContract]
    public sealed class AppConfig
    {
        [DataMember(Order = 1)]
        public int Version { get; set; } = 1;

        [DataMember(Order = 2)]
        public double? WindowLeft { get; set; }

        [DataMember(Order = 3)]
        public double? WindowTop { get; set; }

        [DataMember(Order = 4)]
        public double WindowWidth { get; set; } = 1160;

        [DataMember(Order = 5)]
        public double WindowHeight { get; set; } = 740;

        [DataMember(Order = 6)]
        public bool IsMaximized { get; set; }

        [DataMember(Order = 7)]
        public bool ShowBookmarkBar { get; set; }

        [DataMember(Order = 8)]
        public bool BookmarksInitialized { get; set; }

        [DataMember(Order = 9)]
        public bool ShowPreviewPane { get; set; }

        [DataMember(Order = 10)]
        public List<BookmarkConfigItem> Bookmarks { get; set; } = new List<BookmarkConfigItem>();

        [DataMember(Order = 11)]
        public List<SavedTabConfigItem> OpenTabs { get; set; } = new List<SavedTabConfigItem>();

        [DataMember(Order = 12)]
        public int SelectedTabIndex { get; set; }

        [DataMember(Order = 13)]
        public bool EnableWarmStandby { get; set; } = true;

        [DataMember(Order = 14)]
        public List<string> RecentFolders { get; set; } = new List<string>();

        [DataMember(Order = 15)]
        public double PreviewPaneWidthDip { get; set; } = 340.0;

        [DataMember(Order = 16)]
        public bool RestorePreviousTabs { get; set; }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            EnableWarmStandby = true;
            RecentFolders = new List<string>();
            PreviewPaneWidthDip = 340.0;
            RestorePreviousTabs = false;
        }

        [OnDeserialized]
        private void OnDeserialized(StreamingContext context)
        {
            Bookmarks ??= new List<BookmarkConfigItem>();
            OpenTabs ??= new List<SavedTabConfigItem>();
            RecentFolders ??= new List<string>();
            if (double.IsNaN(PreviewPaneWidthDip) ||
                double.IsInfinity(PreviewPaneWidthDip) ||
                PreviewPaneWidthDip < 220.0 ||
                PreviewPaneWidthDip > 1600.0)
            {
                PreviewPaneWidthDip = 340.0;
            }
        }
    }

    [DataContract]
    public sealed class BookmarkConfigItem
    {
        [DataMember(Order = 1)]
        public string Title { get; set; } = string.Empty;

        [DataMember(Order = 2)]
        public string TargetPath { get; set; } = string.Empty;
    }

    [DataContract]
    public sealed class SavedTabConfigItem
    {
        [DataMember(Order = 1)]
        public string CurrentPath { get; set; } = string.Empty;

        [DataMember(Order = 2)]
        public List<string> HistoryPaths { get; set; } = new List<string>();

        [DataMember(Order = 3)]
        public int HistoryIndex { get; set; }
    }

    public sealed class BookmarkItemViewModel : INotifyPropertyChanged
    {
        private string _title = string.Empty;
        private string _targetPath = string.Empty;
        private ImageSource? _icon;

        public string Title
        {
            get => _title;
            set
            {
                if (_title != value)
                {
                    _title = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ToolTipText));
                }
            }
        }

        public string TargetPath
        {
            get => _targetPath;
            set
            {
                if (_targetPath != value)
                {
                    _targetPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ToolTipText));
                }
            }
        }

        public ImageSource? Icon
        {
            get => _icon;
            set
            {
                if (!ReferenceEquals(_icon, value))
                {
                    _icon = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ToolTipText => string.IsNullOrWhiteSpace(_targetPath)
            ? _title
            : $"{_title}\n{_targetPath}";

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
