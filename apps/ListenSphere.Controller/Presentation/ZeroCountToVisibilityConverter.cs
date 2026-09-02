using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ListenSphere.Controller.Presentation;

public sealed class ZeroCountToVisibilityConverter : IValueConverter
{
    public object Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        int? count = value switch
        {
            int number => number,
            ICollection collection => collection.Count,
            _ => null
        };
        return count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) => Binding.DoNothing;
}
