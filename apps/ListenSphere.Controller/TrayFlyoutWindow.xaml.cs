using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace ListenSphere.Controller;

public partial class TrayFlyoutWindow : Window
{
    private const double CursorGap = 8;
    private readonly Action exitApplication;
    private bool allowClose;

    public TrayFlyoutWindow(
        ControllerViewModel viewModel,
        Action exitApplication)
    {
        InitializeComponent();
        DataContext = viewModel;
        this.exitApplication = exitApplication;
        Closing += OnClosing;
    }

    public void ToggleAtCursor()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        Opacity = 0;
        Show();
        UpdateLayout();
        PositionNearCursor();
        Opacity = 1;
        Activate();
    }

    public void CloseForApplicationExit()
    {
        allowClose = true;
        Close();
    }

    private void PositionNearCursor()
    {
        System.Drawing.Point cursor = Forms.Cursor.Position;
        Forms.Screen screen = Forms.Screen.FromPoint(cursor);
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double cursorX = cursor.X / dpi.DpiScaleX;
        double cursorY = cursor.Y / dpi.DpiScaleY;
        double workLeft = screen.WorkingArea.Left / dpi.DpiScaleX;
        double workTop = screen.WorkingArea.Top / dpi.DpiScaleY;
        double workRight = screen.WorkingArea.Right / dpi.DpiScaleX;
        double workBottom = screen.WorkingArea.Bottom / dpi.DpiScaleY;

        Left = Math.Clamp(
            cursorX - ActualWidth,
            workLeft + CursorGap,
            Math.Max(workLeft + CursorGap, workRight - ActualWidth - CursorGap));
        Top = Math.Clamp(
            cursorY - ActualHeight - CursorGap,
            workTop + CursorGap,
            Math.Max(workTop + CursorGap, workBottom - ActualHeight - CursorGap));
    }

    private void OnDeactivated(object sender, EventArgs args)
    {
        if (!allowClose)
        {
            Hide();
        }
    }

    private void OnExitClick(object sender, RoutedEventArgs args)
    {
        Hide();
        exitApplication();
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (allowClose)
        {
            return;
        }

        args.Cancel = true;
        Hide();
    }
}
