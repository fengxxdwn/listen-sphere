using System.Windows;
using System.Windows.Input;
using System.ComponentModel;
using Serilog;
using System.Windows.Interop;
using ListenSphere.Windows.Devices;

namespace ListenSphere.Sender;

public partial class MainWindow : Window
{
    private readonly SenderViewModel viewModel;
    private Task initializationTask = Task.CompletedTask;
    private bool shutdownStarted;
    private bool shutdownComplete;

    public MainWindow(SenderViewModel viewModel)
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

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        try
        {
            initializationTask = viewModel.InitializeAsync();
            await initializationTask;
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "Sender initialization failed");
            MessageBox.Show(
                $"聆界发送端启动失败：{exception.Message}",
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
                Log.Debug(exception, "Sender initialization completed with an error before shutdown");
            }

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
