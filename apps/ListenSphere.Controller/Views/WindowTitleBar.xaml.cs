using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ListenSphere.Controller.Views;

public partial class WindowTitleBar : UserControl
{
    private Window? owner;

    public WindowTitleBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        Window nextOwner = Window.GetWindow(this);
        if (ReferenceEquals(owner, nextOwner))
        {
            return;
        }

        DetachOwner();
        owner = nextOwner;
        owner.StateChanged += OnOwnerStateChanged;
        UpdateMaximizeRestoreGlyph();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) => DetachOwner();

    private void DetachOwner()
    {
        if (owner is not null)
        {
            owner.StateChanged -= OnOwnerStateChanged;
            owner = null;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        Window? window = owner ?? Window.GetWindow(this);
        if (window is null || args.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (args.ClickCount == 2)
        {
            ToggleMaximizeRestore(window);
            return;
        }

        if (window.WindowState == WindowState.Maximized)
        {
            RestoreForDrag(window, args);
        }

        window.DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs args)
    {
        if (owner is not null)
        {
            owner.WindowState = WindowState.Minimized;
        }
    }

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs args)
    {
        if (owner is not null)
        {
            ToggleMaximizeRestore(owner);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs args) => owner?.Close();

    private void OnOwnerStateChanged(object? sender, EventArgs args) =>
        UpdateMaximizeRestoreGlyph();

    private static void ToggleMaximizeRestore(Window window) =>
        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void RestoreForDrag(Window window, MouseButtonEventArgs args)
    {
        Point position = args.GetPosition(window);
        double horizontalRatio = window.ActualWidth <= 0
            ? 0.5
            : position.X / window.ActualWidth;
        Point screenPosition = window.PointToScreen(position);
        PresentationSource? source = PresentationSource.FromVisual(window);
        if (source?.CompositionTarget is not null)
        {
            screenPosition = source.CompositionTarget.TransformFromDevice.Transform(screenPosition);
        }

        double restoredWidth = window.RestoreBounds.Width > 0
            ? window.RestoreBounds.Width
            : window.ActualWidth;
        window.WindowState = WindowState.Normal;
        window.Left = screenPosition.X - (restoredWidth * horizontalRatio);
        window.Top = Math.Max(SystemParameters.VirtualScreenTop, screenPosition.Y - 18);
    }

    private void UpdateMaximizeRestoreGlyph() =>
        MaximizeRestoreGlyph.Text = owner?.WindowState == WindowState.Maximized
            ? "\uE923"
            : "\uE922";
}
