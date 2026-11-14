using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using LeafTab.Models;
using LeafTab.Shared;

namespace LeafTab.Services
{
    // One UI-thread-owned source of truth. Windows share collections immediately;
    // only disk persistence is debounced, never the propagation of user edits.
    internal sealed class AppConfigStore
    {
        private static AppConfigStore? _current;
        internal static AppConfigStore Current => _current ??= new AppConfigStore(AppConfigService.Load(), c => AppConfigService.Save(c));
        private readonly AppConfig _config;
        private readonly Func<AppConfig, bool> _save;
        private readonly DispatcherTimer _timer;
        internal ObservableCollection<BookmarkItemViewModel> Bookmarks { get; } = new ObservableCollection<BookmarkItemViewModel>();
        internal List<string> RecentFolders { get; }
        internal AppConfig Settings => _config;
        internal event EventHandler? Changed;

        internal AppConfigStore(AppConfig config, Func<AppConfig, bool> save)
        {
            _config = config;
            _save = save;
            RecentFolders = new List<string>(config.RecentFolders.Where(s => !string.IsNullOrWhiteSpace(s)).Take(15));
            foreach (var b in config.Bookmarks)
                if (!string.IsNullOrWhiteSpace(b.TargetPath)) Bookmarks.Add(AppConfigService.CreateBookmarkViewModelFast(b.Title, b.TargetPath));
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1800) };
            _timer.Tick += (_, _) => Flush();
            Bookmarks.CollectionChanged += (_, _) => { ScheduleSave(); Changed?.Invoke(this, EventArgs.Empty); };
        }

        internal void UpdatePreferences(bool bookmarkBar, bool previewPane, bool warmStandby) =>
            UpdatePreferences(bookmarkBar, previewPane, warmStandby, _config.PreviewPaneWidthDip, _config.RestorePreviousTabs);

        internal void UpdatePreferences(
            bool bookmarkBar,
            bool previewPane,
            bool warmStandby,
            double previewPaneWidthDip,
            bool restorePreviousTabs)
        {
            double sanitizedWidth = (double.IsNaN(previewPaneWidthDip) || double.IsInfinity(previewPaneWidthDip) || previewPaneWidthDip < 220.0)
                ? 340.0
                : Math.Min(1600.0, previewPaneWidthDip);

            bool changed =
                _config.ShowBookmarkBar != bookmarkBar ||
                _config.ShowPreviewPane != previewPane ||
                _config.EnableWarmStandby != warmStandby ||
                Math.Abs(_config.PreviewPaneWidthDip - sanitizedWidth) >= 0.5 ||
                _config.RestorePreviousTabs != restorePreviousTabs;

            _config.ShowBookmarkBar = bookmarkBar;
            _config.ShowPreviewPane = previewPane;
            _config.EnableWarmStandby = warmStandby;
            _config.PreviewPaneWidthDip = sanitizedWidth;
            _config.RestorePreviousTabs = restorePreviousTabs;
            if (!restorePreviousTabs)
            {
                _config.OpenTabs.Clear();
                _config.SelectedTabIndex = 0;
            }

            ScheduleSave();
            if (changed) Changed?.Invoke(this, EventArgs.Empty);
        }

        internal void UpdateSavedTabs(IReadOnlyList<SavedTabConfigItem>? openTabs, int selectedTabIndex)
        {
            _config.OpenTabs.Clear();
            if (_config.RestorePreviousTabs && openTabs != null && openTabs.Count > 0)
            {
                foreach (SavedTabConfigItem item in openTabs)
                {
                    if (item != null && !string.IsNullOrWhiteSpace(item.CurrentPath))
                    {
                        _config.OpenTabs.Add(item);
                    }
                }
                _config.SelectedTabIndex = Math.Max(0, Math.Min(selectedTabIndex, Math.Max(0, _config.OpenTabs.Count - 1)));
            }
            else
            {
                _config.SelectedTabIndex = 0;
            }
        }

        internal void UpdatePlacement(Rect bounds, bool maximized)
        {
            if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0 &&
                !double.IsNaN(bounds.Left) && !double.IsNaN(bounds.Top))
            {
                _config.WindowLeft = bounds.Left; _config.WindowTop = bounds.Top;
                _config.WindowWidth = bounds.Width; _config.WindowHeight = bounds.Height;
            }
            _config.IsMaximized = maximized;
        }

        internal void ScheduleSave() { _timer.Stop(); _timer.Start(); }

        internal bool Flush()
        {
            _timer.Stop();
            _config.BookmarksInitialized = true;
            _config.Bookmarks = Bookmarks.Select(b => new BookmarkConfigItem { Title = b.Title, TargetPath = b.TargetPath }).ToList();
            _config.RecentFolders = new List<string>(RecentFolders);
            if (!_config.RestorePreviousTabs)
            {
                // Closing a window intentionally closes its tabs unless opt-in tab restore is enabled.
                _config.OpenTabs.Clear();
                _config.SelectedTabIndex = 0;
            }
            bool saved = _save(_config);
            if (!saved) DiagnosticLog.Write("config-save-failed");
            return saved;
        }
    }
}
