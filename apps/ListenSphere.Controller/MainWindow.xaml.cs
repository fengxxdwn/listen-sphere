using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Interop;
using ListenSphere.Windows.Devices;
using System.ComponentModel;
using Serilog;

namespace ListenSphere.Controller;

public partial class MainWindow : Window
{
    private readonly ControllerViewModel viewModel;
    private Task initializationTask = Task.CompletedTask;
    private bool shutdownStarted;
    private bool shutdownComplete;

    public MainWindow(ControllerViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
        Loaded += OnLoaded;
        Closing += OnClosing;
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs args) =>
        DwmWindowAppearance.TryEnableRoundedCorners(new WindowInteropHelper(this).Handle);

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
