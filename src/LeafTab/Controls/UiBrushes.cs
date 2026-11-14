using System.Collections.Generic;
using System.Windows.Media;

namespace LeafTab.Controls
{
    internal static class UiBrushes
    {
        private static readonly Dictionary<Color, SolidColorBrush> Cache = new Dictionary<Color, SolidColorBrush>();
        internal static SolidColorBrush Get(Color color)
        {
            if (Cache.TryGetValue(color, out var brush)) return brush;
            brush = new SolidColorBrush(color);
            brush.Freeze();
            Cache[color] = brush;
            return brush;
        }
    }
}
