using System.Windows;
using System.Windows.Media;

namespace ListenSphere.Controller;

public sealed class EqualizerCurveControl : FrameworkElement
{
    public static readonly DependencyProperty GainsProperty = DependencyProperty.Register(
        nameof(Gains),
        typeof(IReadOnlyList<float>),
        typeof(EqualizerCurveControl),
        new FrameworkPropertyMetadata(
            Array.Empty<float>(),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<float> Gains
    {
        get => (IReadOnlyList<float>)GetValue(GainsProperty);
        set => SetValue(GainsProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 1 || height <= 1)
        {
            return;
        }

        const double horizontalPadding = 14;
        const double verticalPadding = 10;
        var plot = new Rect(
            horizontalPadding,
            verticalPadding,
            Math.Max(1, width - (horizontalPadding * 2)),
            Math.Max(1, height - (verticalPadding * 2)));
        var minorGrid = new Pen(new SolidColorBrush(Color.FromArgb(70, 65, 78, 96)), 1);
        var zeroGrid = new Pen(new SolidColorBrush(Color.FromArgb(150, 92, 119, 160)), 1);

        for (int row = 0; row <= 4; row++)
        {
            double y = plot.Top + (plot.Height * row / 4d);
            drawingContext.DrawLine(row == 2 ? zeroGrid : minorGrid,
                new Point(plot.Left, y), new Point(plot.Right, y));
        }
        for (int column = 0; column < 10; column++)
        {
            double x = plot.Left + (plot.Width * column / 9d);
            drawingContext.DrawLine(minorGrid,
                new Point(x, plot.Top), new Point(x, plot.Bottom));
        }

        IReadOnlyList<float> gains = Gains;
        if (gains.Count != 10)
        {
            return;
        }

        Point GetPoint(int index)
        {
            double x = plot.Left + (plot.Width * index / 9d);
            double normalized = (Math.Clamp(gains[index], -20f, 20f) + 20d) / 40d;
            return new Point(x, plot.Bottom - (normalized * plot.Height));
        }

        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(GetPoint(0), false, false);
            for (int index = 1; index < gains.Count; index++)
            {
                context.LineTo(GetPoint(index), true, false);
            }
        }
        geometry.Freeze();

        var glowPen = new Pen(new SolidColorBrush(Color.FromArgb(65, 76, 141, 255)), 6);
        var curvePen = new Pen(new SolidColorBrush(Color.FromRgb(76, 141, 255)), 2);
        drawingContext.DrawGeometry(null, glowPen, geometry);
        drawingContext.DrawGeometry(null, curvePen, geometry);
        var nodeBrush = new SolidColorBrush(Color.FromRgb(100, 161, 255));
        for (int index = 0; index < gains.Count; index++)
        {
            drawingContext.DrawEllipse(nodeBrush, null, GetPoint(index), 3.5, 3.5);
        }
    }
}
