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
    private bool exitRequested;

    public MainWindow(ControllerViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        viewModel.Updates.InstallLaunched += OnUpdateInstallLaunched;
        DataContext = viewModel;
        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
        SourceInitialized += OnSourceInitialized;
    }

    public event EventHandler? MinimizedToTray;

    private void OnUpdateInstallLaunched(object? sender, EventArgs args) => RequestExit();

    public void ShowAndActivate()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void RequestExit()
    {
        exitRequested = true;
        Close();
    }

    private void OnSourceInitialized(object? sender, EventArgs args) =>
        DwmWindowAppearance.TryEnableRoundedCorners(new WindowInteropHelper(this).Handle);

    private void OnStateChanged(object? sender, EventArgs args)
    {
        if (WindowState != WindowState.Minimized || exitRequested)
        {
            return;
        }

        Hide();
        MinimizedToTray?.Invoke(this, EventArgs.Empty);
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
            _ = Dispatcher.BeginInvoke(RequestExit);
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs args)
    {
        if (!exitRequested)
        {
            args.Cancel = true;
            Hide();
            MinimizedToTray?.Invoke(this, EventArgs.Empty);
            return;
        }

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
            Application.Current.Shutdown();
        }
    }
}
