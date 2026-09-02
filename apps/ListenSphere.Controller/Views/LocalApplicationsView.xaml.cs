using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Serilog;

namespace ListenSphere.Controller.Views;

public partial class LocalApplicationsView : UserControl
{
    private readonly AudioRoutingDragState dragState = new();

    public LocalApplicationsView() => InitializeComponent();

    private void LocalApplicationSource_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs args)
    {
        AudioSessionItemViewModel? session =
            (sender as FrameworkElement)?.DataContext as AudioSessionItemViewModel;
        Guid? channelId = session?.CanRouteToListenSphere == true
            ? session.RoutingChannelId
            : null;
        dragState.Begin(args.GetPosition(this), channelId, args.OriginalSource);
    }

    private void LocalApplicationSource_MouseMove(object sender, MouseEventArgs args) =>
        dragState.Continue(this, sender, args);

    private void OpenWindowsAppVolumeSettings_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:apps-volume")
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Failed to open Windows app volume settings");
            (DataContext as ControllerViewModel)?.ReportError(
                $"无法打开 Windows 应用音量设置：{exception.Message}");
        }
    }

    private async void AddApplicationOutput_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not AudioSessionItemViewModel
            {
                SelectedAdditionalOutput: { } output
            } session ||
            DataContext is not ControllerViewModel viewModel)
        {
            return;
        }

        await viewModel.Network.AddSecondaryOutputRouteAsync(
            session.RoutingChannelId,
            output.DeviceId);
    }
}
