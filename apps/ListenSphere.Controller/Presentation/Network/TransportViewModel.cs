using ListenSphere.Network;

namespace ListenSphere.Controller;

public sealed class TransportViewModel(ControllerNetworkRuntime runtime)
    : NetworkPresentationModel(runtime)
{
    public LocalDeviceIdentity Identity => Runtime.Identity;
    public RemoteTransportMode SelectedTransport => Runtime.SelectedTransport;
    public bool IsWirelessSelected => Runtime.IsWirelessSelected;
    public bool IsBluetoothSelected => Runtime.IsBluetoothSelected;
    public bool IsWiredSelected => Runtime.IsWiredSelected;
    public string EmptyTransportText => Runtime.EmptyTransportText;
    public string NetworkStatus => Runtime.NetworkStatus;
    public string WirelessIpAddressText => Runtime.WirelessIpAddressText;
    public string WirelessPortText => Runtime.WirelessPortText;
    public string BluetoothStatus => Runtime.BluetoothStatus;
    public string UsbStatus => Runtime.UsbStatus;
    public AsyncRelayCommand SelectWirelessCommand => Runtime.SelectWirelessCommand;
    public AsyncRelayCommand SelectBluetoothCommand => Runtime.SelectBluetoothCommand;
    public AsyncRelayCommand SelectWiredCommand => Runtime.SelectWiredCommand;
}
