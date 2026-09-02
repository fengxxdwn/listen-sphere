using System.Collections.ObjectModel;
using ListenSphere.Network;

namespace ListenSphere.Controller;

public sealed class RemoteDevicesViewModel(ControllerNetworkRuntime runtime)
    : NetworkPresentationModel(runtime)
{
    public ObservableCollection<TrustedDevice> TrustedDevices => Runtime.TrustedDevices;
    public ObservableCollection<RemoteChannelItemViewModel> VisibleRemoteChannels =>
        Runtime.VisibleRemoteChannels;
    public ObservableCollection<RemoteChannelItemViewModel> ActiveRemoteChannels =>
        Runtime.ActiveRemoteChannels;
    public string PairingCode => Runtime.PairingCode;
    public string PairingCodeActionText => Runtime.PairingCodeActionText;
    public string PairingHint => Runtime.PairingHint;
    public string DeleteConfirmationDeviceName => Runtime.DeleteConfirmationDeviceName;
    public bool IsDeleteConfirmationVisible => Runtime.IsDeleteConfirmationVisible;
    public AsyncRelayCommand GenerateCodeCommand => Runtime.GenerateCodeCommand;
    public AsyncRelayCommand ConfirmDeleteCommand => Runtime.ConfirmDeleteCommand;
    public AsyncRelayCommand CancelDeleteCommand => Runtime.CancelDeleteCommand;
}
