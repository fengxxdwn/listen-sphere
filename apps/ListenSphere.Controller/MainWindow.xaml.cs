using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Interop;
using ListenSphere.Windows.Devices;
using System.ComponentModel;
using System.Diagnostics;
using Serilog;

namespace ListenSphere.Controller;

public partial class MainWindow : Window
{
    private readonly ControllerViewModel viewModel;
    private Task initializationTask = Task.CompletedTask;
    private bool shutdownStarted;
    private bool shutdownComplete;
    private Point sourceDragStart;
    private Guid? sourceDragChannelId;
    private bool sourceDragAllowed;
    private const string RemoteChannelDragFormat = "ListenSphere.RemoteChannel";

    public MainWindow(ControllerViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnWindowStateChanged;
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs args) =>
        DwmWindowAppearance.TryEnableRoundedCorners(new WindowInteropHelper(this).Handle);

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (args.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            RestoreForDrag(args);
        }

        DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs args) =>
        WindowState = WindowState.Minimized;

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs args) =>
        ToggleMaximizeRestore();

    private void CloseButton_Click(object sender, RoutedEventArgs args) => Close();

    private void OnWindowStateChanged(object? sender, EventArgs args) => UpdateMaximizeRestoreGlyph();

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void RestoreForDrag(MouseButtonEventArgs args)
    {
        Point position = args.GetPosition(this);
        double horizontalRatio = ActualWidth <= 0 ? 0.5 : position.X / ActualWidth;
        Point screenPosition = PointToScreen(position);
        PresentationSource? source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is not null)
        {
            screenPosition = source.CompositionTarget.TransformFromDevice.Transform(screenPosition);
        }

        double restoredWidth = RestoreBounds.Width > 0 ? RestoreBounds.Width : ActualWidth;

        WindowState = WindowState.Normal;
        Left = screenPosition.X - (restoredWidth * horizontalRatio);
        Top = Math.Max(SystemParameters.VirtualScreenTop, screenPosition.Y - 18);
    }

    private void UpdateMaximizeRestoreGlyph()
    {
        MaximizeRestoreGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void RemoteSource_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs args)
    {
        sourceDragStart = args.GetPosition(this);
        sourceDragChannelId = ((sender as FrameworkElement)?.DataContext as RemoteChannelItemViewModel)?.ChannelId;
        sourceDragAllowed = sourceDragChannelId is not null && !IsInteractiveControl(args.OriginalSource);
    }

    private void LocalSource_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs args)
    {
        sourceDragStart = args.GetPosition(this);
        sourceDragChannelId = ControllerNetworkViewModel.LocalSoundChannelId;
        sourceDragAllowed = !IsInteractiveControl(args.OriginalSource);
    }

    private void LocalSource_MouseMove(object sender, MouseEventArgs args) =>
        RemoteSource_MouseMove(sender, args);

    private void LocalApplicationSource_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs args)
    {
        sourceDragStart = args.GetPosition(this);
        AudioSessionItemViewModel? session =
            (sender as FrameworkElement)?.DataContext as AudioSessionItemViewModel;
        sourceDragChannelId = session?.CanRouteToListenSphere == true
            ? session.RoutingChannelId
            : null;
        sourceDragAllowed = sourceDragChannelId is not null &&
            !IsInteractiveControl(args.OriginalSource);
    }

    private void LocalApplicationSource_MouseMove(object sender, MouseEventArgs args) =>
        RemoteSource_MouseMove(sender, args);

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
            viewModel.ReportError($"无法打开 Windows 应用音量设置：{exception.Message}");
        }
    }

    private void OpenSelectedMicrophoneSettings_Click(object sender, RoutedEventArgs args)
    {
        var device = viewModel.Network.SelectedComputerMicrophoneDevice;
        if (device is null)
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
            Log.Warning(
                exception,
                "Failed to open Windows microphone settings for {DeviceId}",
                device.Id);
            viewModel.ReportError($"无法打开所选麦克风的 Windows 设置：{exception.Message}");
        }
    }

    private async void AddApplicationOutput_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not AudioSessionItemViewModel
            {
                SelectedAdditionalOutput: { } output
            } session)
        {
            return;
        }

        await viewModel.Network.AddSecondaryOutputRouteAsync(
            session.RoutingChannelId,
            output.DeviceId);
    }

    private void RemoteSource_MouseMove(object sender, MouseEventArgs args)
    {
        if (!sourceDragAllowed || sourceDragChannelId is null ||
            args.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Point current = args.GetPosition(this);
        if (Math.Abs(current.X - sourceDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - sourceDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var data = new DataObject(RemoteChannelDragFormat, sourceDragChannelId.Value);
        sourceDragAllowed = false;
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Link);
        sourceDragChannelId = null;
    }

    private void AdditionalOutput_DragOver(object sender, DragEventArgs args)
    {
        args.Effects = args.Data.GetDataPresent(RemoteChannelDragFormat)
            ? DragDropEffects.Link
            : DragDropEffects.None;
        args.Handled = true;
    }

    private async void AdditionalOutput_Drop(object sender, DragEventArgs args)
    {
        args.Handled = true;
        if (sender is not FrameworkElement
            {
                DataContext: AdditionalOutputDeviceItemViewModel target
            } ||
            args.Data.GetData(RemoteChannelDragFormat) is not Guid channelId)
        {
            return;
        }

        await viewModel.Network.AddSecondaryOutputRouteAsync(channelId, target.DeviceId);
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

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        try
        {
            initializationTask = viewModel.InitializeAsync();
            await initializationTask;
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "Controller initialization failed");
            MessageBox.Show(
                $"聆界启动失败：{exception.Message}",
                "聆界",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            _ = Dispatcher.BeginInvoke(Close);
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs args)
    {
        if (shutdownComplete)
        {
            return;
        }

        args.Cancel = true;
        if (shutdownStarted)
        {
            return;
        }

        shutdownStarted = true;
        IsEnabled = false;
        try
        {
            try
            {
                await initializationTask;
            }
            catch (Exception exception)
            {
                Log.Debug(exception, "Controller initialization completed with an error before shutdown");
            }

            await viewModel.DisposeAsync();
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Controller shutdown cleanup failed");
        }
        finally
        {
            shutdownComplete = true;
            Close();
        }
    }
}
