using System.Windows;
using System.ComponentModel;
using Serilog;

namespace ListenSphere.Sender;

public partial class MainWindow : Window
{
    private readonly SenderViewModel viewModel;
    private bool shutdownStarted;
    private bool shutdownComplete;

    public MainWindow(SenderViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        try
        {
            await viewModel.InitializeAsync();
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "Sender initialization failed");
            MessageBox.Show(
                $"聆界发送端启动失败：{exception.Message}",
                "聆界",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
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
            await viewModel.DisposeAsync();
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Sender shutdown cleanup failed");
        }
        finally
        {
            shutdownComplete = true;
            Close();
        }
    }
}
