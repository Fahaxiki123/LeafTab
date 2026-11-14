using System;
using System.Windows;
using System.Windows.Controls;

namespace LeafTab.Controls
{
    /// <summary>
    /// Apple/Safari-inspired adaptive tab strip panel with zero scrollbars:
    /// - Gives each tab a generous natural width (up to MaxTabWidth = 192px) when space permits;
    /// - Smoothly compresses all tabs proportionally as more tabs are opened;
    /// - Supports animated OpenProgress (0..1) on individual item containers for silky tab-open expansion;
    /// - Reserves 9px left/right ear clearance so active-tab outward concave bottom curves never clip.
    /// </summary>
    public sealed class AdaptiveTabStripPanel : Panel
    {
        public const double EarClearanceDip = 9.0;

        public static readonly DependencyProperty OpenProgressProperty =
            DependencyProperty.RegisterAttached(
                "OpenProgress",
                typeof(double),
                typeof(AdaptiveTabStripPanel),
                new FrameworkPropertyMetadata(
                    1.0,
                    FrameworkPropertyMetadataOptions.AffectsParentMeasure |
                    FrameworkPropertyMetadataOptions.AffectsParentArrange));

        public static void SetOpenProgress(DependencyObject element, double value) =>
            element.SetValue(OpenProgressProperty, value);

        public static double GetOpenProgress(DependencyObject element) =>
            (double)element.GetValue(OpenProgressProperty);

        public double MaxTabWidth { get; set; } = 192.0;

        public double MinTabWidth { get; set; } = 48.0;

        protected override Size MeasureOverride(Size availableSize)
        {
            int count = InternalChildren.Count;
            double totalInset = EarClearanceDip * 2.0;
            double maxHeight = 0.0;

            if (count == 0)
            {
                return new Size(totalInset, 0.0);
            }

            double totalWeight = 0.0;
            for (int i = 0; i < count; i++)
            {
                UIElement child = InternalChildren[i];
                if (child == null || child.Visibility == Visibility.Collapsed)
                    continue;

                double progress = Clamp(GetOpenProgress(child), 0.05, 1.0);
                totalWeight += progress;
            }

            if (totalWeight <= 0.0)
            {
                return new Size(totalInset, 0.0);
            }

            double viewport = Math.Min(availableSize.Width, TabBarTrackPanel.GetAvailableTabsWidth(this));
            double unitWidth = MaxTabWidth;
            if (!double.IsInfinity(viewport))
            {
                double availForTabs = Math.Max(0.0, viewport - totalInset);
                double rawUnit = availForTabs / totalWeight;
                unitWidth = Math.Max(MinTabWidth, Math.Min(MaxTabWidth, rawUnit));
            }

            double occupiedTabsWidth = 0.0;
            double childAvailHeight = double.IsInfinity(availableSize.Height) ? 40.0 : availableSize.Height;

            for (int i = 0; i < count; i++)
            {
                UIElement child = InternalChildren[i];
                if (child == null || child.Visibility == Visibility.Collapsed)
                    continue;

                double progress = Clamp(GetOpenProgress(child), 0.05, 1.0);
                double w = unitWidth * progress;
                child.Measure(new Size(w, childAvailHeight));
                occupiedTabsWidth += w;
                if (child.DesiredSize.Height > maxHeight)
                {
                    maxHeight = child.DesiredSize.Height;
                }
            }

            double desiredWidth = occupiedTabsWidth + totalInset;

            return new Size(
                desiredWidth,
                double.IsInfinity(availableSize.Height) ? maxHeight : Math.Max(maxHeight, availableSize.Height));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            int count = InternalChildren.Count;
            if (count == 0)
                return finalSize;

            double totalInset = EarClearanceDip * 2.0;
            double totalWeight = 0.0;

            for (int i = 0; i < count; i++)
            {
                UIElement child = InternalChildren[i];
                if (child == null || child.Visibility == Visibility.Collapsed)
                    continue;

                totalWeight += Clamp(GetOpenProgress(child), 0.05, 1.0);
            }

            if (totalWeight <= 0.0)
                return finalSize;

            double availForTabs = Math.Max(0.0, finalSize.Width - totalInset);
            double unitWidth = Math.Max(MinTabWidth, Math.Min(MaxTabWidth, availForTabs / totalWeight));

            double x = EarClearanceDip;
            for (int i = 0; i < count; i++)
            {
                UIElement child = InternalChildren[i];
                if (child == null || child.Visibility == Visibility.Collapsed)
                    continue;

                double progress = Clamp(GetOpenProgress(child), 0.05, 1.0);
                double w = unitWidth * progress;
                child.Arrange(new Rect(x, 0.0, w, finalSize.Height));
                x += w;
            }

            return finalSize;
        }

        private static double Clamp(double value, double min, double max)
        {
            if (double.IsNaN(value)) return max;
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }

    /// <summary>
    /// Hosts [TabsItemsControl] + [NewTabButton (+)] in the Row 0 title bar:
    /// - Gives TabsItemsControl all available width minus the [+] button;
    /// - Positions [+] immediately to the right of the last tab;
    /// - Leaves Background null so empty space to the right of [+] remains native HTCAPTION draggable space.
    /// </summary>
    public sealed class TabBarTrackPanel : Panel
    {
        // The scroll content is measured with infinite width. Inherit the current track
        // budget instead of feeding its previous viewport back into the next measure.
        internal static readonly DependencyProperty AvailableTabsWidthProperty =
            DependencyProperty.RegisterAttached("AvailableTabsWidth", typeof(double), typeof(TabBarTrackPanel),
                new FrameworkPropertyMetadata(double.PositiveInfinity,
                    FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));

        internal static double GetAvailableTabsWidth(DependencyObject element) =>
            (double)element.GetValue(AvailableTabsWidthProperty);

        protected override Size MeasureOverride(Size availableSize)
        {
            if (InternalChildren.Count == 0)
                return new Size(0, 0);

            if (InternalChildren.Count == 1)
            {
                InternalChildren[0].Measure(availableSize);
                return InternalChildren[0].DesiredSize;
            }

            UIElement tabsControl = InternalChildren[0];
            UIElement plusButton = InternalChildren[1];

            plusButton.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            double plusWidth = plusButton.DesiredSize.Width;

            double maxTabsWidth = double.IsInfinity(availableSize.Width)
                ? double.PositiveInfinity
                : Math.Max(0.0, availableSize.Width - plusWidth - 4.0);

            tabsControl.SetValue(AvailableTabsWidthProperty, maxTabsWidth);
            tabsControl.Measure(new Size(maxTabsWidth, availableSize.Height));

            double totalW = tabsControl.DesiredSize.Width + plusWidth;
            if (!double.IsInfinity(availableSize.Width))
            {
                totalW = Math.Min(availableSize.Width, totalW);
            }

            double maxH = Math.Max(tabsControl.DesiredSize.Height, plusButton.DesiredSize.Height);
            return new Size(totalW, double.IsInfinity(availableSize.Height) ? maxH : availableSize.Height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (InternalChildren.Count == 0)
                return finalSize;

            if (InternalChildren.Count == 1)
            {
                InternalChildren[0].Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
                return finalSize;
            }

            UIElement tabsControl = InternalChildren[0];
            UIElement plusButton = InternalChildren[1];

            double plusWidth = plusButton.DesiredSize.Width;
            double maxTabsWidth = Math.Max(0.0, finalSize.Width - plusWidth - 2.0);
            double tabsWidth = Math.Min(tabsControl.DesiredSize.Width, maxTabsWidth);

            tabsControl.Arrange(new Rect(0, 0, tabsWidth, finalSize.Height));

            // Offset by -3px from tabsWidth because AdaptiveTabStripPanel includes 9px right ear clearance,
            // yielding a clean ~6px visual gap after the last tab pill.
            double plusX = Math.Max(0.0, tabsWidth - 3.0);
            plusButton.Arrange(new Rect(plusX, 0, plusWidth, finalSize.Height));

            return finalSize;
        }
    }
}
