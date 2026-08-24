using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace ListenSphere.Windows.Devices;

/// <summary>Bridges Windows MMDevice endpoint notifications into coalescible events.</summary>
public sealed class WasapiDeviceNotificationSource :
    IWindowsDeviceNotificationSource,
    IDisposable
{
    private readonly MMDeviceEnumerator enumerator = new();
    private readonly NotificationClient client;
    private bool disposed;

    public WasapiDeviceNotificationSource()
    {
        client = new NotificationClient(this);
        enumerator.RegisterEndpointNotificationCallback(client);
    }

    public event EventHandler<WindowsDeviceChange>? Changed;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        enumerator.UnregisterEndpointNotificationCallback(client);
        enumerator.Dispose();
        disposed = true;
    }

    private void Raise(WindowsDeviceChange change) =>
        Changed?.Invoke(this, change);

    private sealed class NotificationClient(WasapiDeviceNotificationSource owner) :
        IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) =>
            owner.Raise(WindowsDeviceChange.StateChanged);

        public void OnDeviceAdded(string deviceId) =>
            owner.Raise(WindowsDeviceChange.Added);

        public void OnDeviceRemoved(string deviceId) =>
            owner.Raise(WindowsDeviceChange.Removed);

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) =>
            owner.Raise(WindowsDeviceChange.DefaultChanged);

        public void OnPropertyValueChanged(string deviceId, PropertyKey key)
        {
        }
    }
}
