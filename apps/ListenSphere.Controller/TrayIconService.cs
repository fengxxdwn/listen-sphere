using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace ListenSphere.Controller;

internal sealed class TrayIconService : IDisposable
{
    private readonly MainWindow mainWindow;
    private readonly TrayFlyoutWindow flyout;
    private readonly Forms.NotifyIcon notifyIcon;
    private readonly Icon? icon;
    private bool backgroundNotificationShown;
    private int disposed;

    public TrayIconService(
        MainWindow mainWindow,
        ControllerViewModel viewModel,
        Action exitApplication)
    {
        this.mainWindow = mainWindow;
        flyout = new TrayFlyoutWindow(viewModel, exitApplication);
        icon = LoadApplicationIcon();
        notifyIcon = new Forms.NotifyIcon
        {
            Icon = icon ?? SystemIcons.Application,
            Text = "聆界 · ListenSphere",
            Visible = true
        };
        notifyIcon.MouseClick += OnMouseClick;
    }

    public void ShowBackgroundNotification()
    {
        if (backgroundNotificationShown)
        {
            return;
        }

        backgroundNotificationShown = true;
        notifyIcon.ShowBalloonTip(
            2500,
            "聆界仍在运行",
            "左键打开主界面，右键管理已连接设备。",
            Forms.ToolTipIcon.Info);
    }

    private void OnMouseClick(object? sender, Forms.MouseEventArgs args)
    {
        _ = Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (args.Button == Forms.MouseButtons.Left)
            {
                flyout.Hide();
                mainWindow.ShowAndActivate();
            }
            else if (args.Button == Forms.MouseButtons.Right)
            {
                flyout.ToggleAtCursor();
            }
        });
    }

    private static Icon? LoadApplicationIcon()
    {
        try
        {
            string? executablePath = Environment.ProcessPath;
            return string.IsNullOrWhiteSpace(executablePath)
                ? null
                : Icon.ExtractAssociatedIcon(executablePath);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        notifyIcon.MouseClick -= OnMouseClick;
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        icon?.Dispose();
        flyout.CloseForApplicationExit();
    }
}
