using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ListenSphere.Controller.Presentation;

namespace ListenSphere.Controller.Views;

public partial class AudioSourceRoutingView : UserControl
{
    private readonly AudioRoutingDragState dragState = new();

    public AudioSourceRoutingView() => InitializeComponent();

    private void RemoteSource_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs args)
    {
        Guid? channelId =
            ((sender as FrameworkElement)?.DataContext as RemoteChannelItemViewModel)?.ChannelId;
        dragState.Begin(args.GetPosition(this), channelId, args.OriginalSource);
    }

    private void LocalSource_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs args) =>
        dragState.Begin(
            args.GetPosition(this),
            ControllerNetworkViewModel.LocalSoundChannelId,
            args.OriginalSource);

    private void LocalSource_MouseMove(object sender, MouseEventArgs args) =>
        dragState.Continue(this, sender, args);

    private void RemoteSource_MouseMove(object sender, MouseEventArgs args) =>
        dragState.Continue(this, sender, args);

    private void AdditionalOutput_DragOver(object sender, DragEventArgs args) =>
        AudioRoutingDragState.SetDragOverEffect(args);

    private async void AdditionalOutput_Drop(object sender, DragEventArgs args)
    {
        args.Handled = true;
        if (sender is not FrameworkElement
            {
                DataContext: AdditionalOutputDeviceItemViewModel target
            } ||
            !AudioRoutingDragState.TryGetChannelId(args, out Guid channelId) ||
            DataContext is not ControllerDashboardViewModel viewModel)
        {
            return;
        }

        await viewModel.Network.LocalRouting.AddSecondaryOutputRouteAsync(
            channelId,
            target.DeviceId);
    }

}
