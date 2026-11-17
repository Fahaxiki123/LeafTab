using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace LeafTab.Views
{
    public partial class MainWindow
    {
        private void TabsScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (Math.Abs(e.ViewportWidthChange) > 0.1)
            {
                EnsureActiveTabVisible();
            }
        }

        private void EnsureActiveTabVisible()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!_isClosingWindow && _selectedTab != null &&
                    TabsItemsControl.ItemContainerGenerator.ContainerFromItem(_selectedTab) is FrameworkElement item)
                    item.BringIntoView();
            }));
        }

        private void TabsOverflowButton_Click(object sender, RoutedEventArgs e)
        {
            var menu = CreateLiquidGlassContextMenu(TabsOverflowButton);
            foreach (var tab in _tabs)
            {
                var item = new MenuItem { Header = tab.Title.Replace("_", "__"), ToolTip = tab.ToolTipText, IsChecked = ReferenceEquals(tab, _selectedTab) };
                item.Click += (_, _) => ActivateTab(tab);
                menu.Items.Add(item);
            }
            menu.IsOpen = menu.Items.Count > 0;
        }
    }
}
