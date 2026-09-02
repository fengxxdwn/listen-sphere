using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using ListenSphere.Controller.Presentation;
using Serilog;

namespace ListenSphere.Controller.Views;

public partial class MicrophoneRoutingView : UserControl
{
    public MicrophoneRoutingView() => InitializeComponent();

    private void OpenSelectedMicrophoneSettings_Click(object sender, RoutedEventArgs args)
    {
        if (DataContext is not ControllerDashboardViewModel viewModel ||
            viewModel.Network.Microphone.SelectedComputerMicrophoneDevice is not { } device)
        {
            return;
        }

        try
        {
            string endpointId = Uri.EscapeDataString(device.Id);
            Process.Start(new ProcessStartInfo(
                $"ms-settings:sound-properties?endpointId={endpointId}")
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            Log.Warning(exception,
                "Failed to open Windows microphone settings for {DeviceId}",
                device.Id);
            viewModel.ReportError(
                $"无法打开所选麦克风的 Windows 设置：{exception.Message}");
        }
    }
}
