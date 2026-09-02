using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ListenSphere.Controller.Views;

internal sealed class AudioRoutingDragState
{
    public const string DataFormat = "ListenSphere.RemoteChannel";

    private Point start;
    private Guid? channelId;
    private bool allowed;

    public void Begin(Point position, Guid? nextChannelId, object originalSource)
    {
        start = position;
        channelId = nextChannelId;
        allowed = channelId is not null && !IsInteractiveControl(originalSource);
    }

    public void Continue(
        FrameworkElement coordinateRoot,
        object sender,
        MouseEventArgs args)
    {
        if (!allowed || channelId is null || args.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Point current = args.GetPosition(coordinateRoot);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var data = new DataObject(DataFormat, channelId.Value);
        allowed = false;
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Link);
        channelId = null;
    }

    public static void SetDragOverEffect(DragEventArgs args)
    {
        args.Effects = args.Data.GetDataPresent(DataFormat)
            ? DragDropEffects.Link
            : DragDropEffects.None;
        args.Handled = true;
    }

    public static bool TryGetChannelId(DragEventArgs args, out Guid channelId)
    {
        if (args.Data.GetData(DataFormat) is Guid value)
        {
            channelId = value;
            return true;
        }

        channelId = Guid.Empty;
        return false;
    }

    private static bool IsInteractiveControl(object originalSource)
    {
        for (DependencyObject? current = originalSource as DependencyObject;
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase or Slider or ComboBox or ScrollBar)
            {
                return true;
            }
        }

        return false;
    }
}
