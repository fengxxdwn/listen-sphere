namespace ListenSphere.Windows.Devices;

public enum WindowsDeviceChange
{
    Added,
    Removed,
    DefaultChanged,
    StateChanged
}

/// <summary>Publishes coalesced Windows device change notifications.</summary>
public interface IWindowsDeviceNotificationSource
{
    event EventHandler<WindowsDeviceChange>? Changed;
}
