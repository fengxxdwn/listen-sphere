using System.Collections.ObjectModel;
using ListenSphere.Audio.Abstractions;

namespace ListenSphere.Controller;

public sealed class AudioOutputViewModel(ControllerNetworkRuntime runtime)
    : NetworkPresentationModel(runtime)
{
    public ObservableCollection<IAudioDevice> PlaybackDevices => Runtime.PlaybackDevices;
    public ObservableCollection<AdditionalOutputDeviceItemViewModel> AdditionalOutputs =>
        Runtime.AdditionalOutputs;
    public AsyncRelayCommand RefreshOutputsCommand => Runtime.RefreshOutputsCommand;
    public bool IsSystemMuted
    {
        get => Runtime.IsSystemMuted;
        set => Runtime.IsSystemMuted = value;
    }
    public string SystemMuteButtonText => Runtime.SystemMuteButtonText;
    public float MasterVolumePercent
    {
        get => Runtime.MasterVolumePercent;
        set => Runtime.MasterVolumePercent = value;
    }
    public IAudioDevice? SelectedPlaybackDevice
    {
        get => Runtime.SelectedPlaybackDevice;
        set => Runtime.SelectedPlaybackDevice = value;
    }
    public string? SelectedPlaybackDeviceId
    {
        get => Runtime.SelectedPlaybackDeviceId;
        set => Runtime.SelectedPlaybackDeviceId = value;
    }
    public int SelectedPlaybackDeviceIndex
    {
        get => Runtime.SelectedPlaybackDeviceIndex;
        set => Runtime.SelectedPlaybackDeviceIndex = value;
    }
    public string SelectedPlaybackDeviceDisplayName => Runtime.SelectedPlaybackDeviceDisplayName;
    public bool FollowSystemDefaultPlayback
    {
        get => Runtime.FollowSystemDefaultPlayback;
        set => Runtime.FollowSystemDefaultPlayback = value;
    }
}
