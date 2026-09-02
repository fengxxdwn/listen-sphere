using System.Collections.ObjectModel;

namespace ListenSphere.Controller;

public sealed class RemoteAudioViewModel(ControllerNetworkRuntime runtime)
    : NetworkPresentationModel(runtime)
{
    public ObservableCollection<RemoteChannelItemViewModel> RemoteChannels =>
        Runtime.RemoteChannels;
    public ObservableCollection<RemoteChannelItemViewModel> VisibleRemoteChannels =>
        Runtime.VisibleRemoteChannels;
    public ObservableCollection<RemoteChannelItemViewModel> ActiveRemoteChannels =>
        Runtime.ActiveRemoteChannels;
    public string AudioStatus => Runtime.AudioStatus;
    public string AudioErrorText => Runtime.AudioErrorText;
    public bool IsAudioDetailsOpen
    {
        get => Runtime.IsAudioDetailsOpen;
        set => Runtime.IsAudioDetailsOpen = value;
    }
}
