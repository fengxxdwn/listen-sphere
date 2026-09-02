using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using ListenSphere.Controller.Presentation;
using Xunit;

namespace ListenSphere.Windows.TechnicalTests;

public sealed class PresentationConverterTests
{
    private readonly ZeroCountToVisibilityConverter converter = new();

    [Fact]
    public void ZeroCountConverter_HandlesCountsAndCollections()
    {
        Assert.Equal(
            Visibility.Visible,
            converter.Convert(0, typeof(Visibility), null, CultureInfo.InvariantCulture));
        Assert.Equal(
            Visibility.Collapsed,
            converter.Convert(2, typeof(Visibility), null, CultureInfo.InvariantCulture));
        Assert.Equal(
            Visibility.Visible,
            converter.Convert(
                new ObservableCollection<string>(),
                typeof(Visibility),
                null,
                CultureInfo.InvariantCulture));
        Assert.Equal(
            Visibility.Collapsed,
            converter.Convert(null, typeof(Visibility), null, CultureInfo.InvariantCulture));
        Assert.Same(
            Binding.DoNothing,
            converter.ConvertBack(
                Visibility.Visible,
                typeof(int),
                null,
                CultureInfo.InvariantCulture));
    }
}
