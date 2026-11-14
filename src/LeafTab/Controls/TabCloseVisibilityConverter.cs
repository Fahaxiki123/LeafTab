using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LeafTab.Controls
{
    public sealed class TabCloseVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            (values.Length > 0 && values[0] is bool active && active) ||
            (values.Length > 1 && values[1] is double width && width >= 96)
                ? Visibility.Visible : Visibility.Collapsed;
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
