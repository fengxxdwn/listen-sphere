using System.Collections.ObjectModel;
using ListenSphere.Audio.Abstractions;

namespace ListenSphere.Controller;

public sealed class MicrophoneViewModel(ControllerNetworkRuntime runtime)
    : NetworkPresentationModel(runtime)
{
    public ObservableCollection<IAudioDevice> MicrophoneOutputDevices =>
        Runtime.MicrophoneOutputDevices;
    public ObservableCollection<IAudioDevice> RecordingDevices => Runtime.RecordingDevices;
    public ObservableCollection<IAudioDevice> PlaybackDevices => Runtime.PlaybackDevices;
    public IAudioDevice? SelectedMicrophoneOutputDevice
    {
        get => Runtime.SelectedMicrophoneOutputDevice;
        set => Runtime.SelectedMicrophoneOutputDevice = value;
    }
    public int SelectedMicrophoneOutputDeviceIndex
    {
        get => Runtime.SelectedMicrophoneOutputDeviceIndex;
        set => Runtime.SelectedMicrophoneOutputDeviceIndex = value;
    }
    public string SelectedMicrophoneOutputDeviceDisplayName =>
        Runtime.SelectedMicrophoneOutputDeviceDisplayName;
    public IAudioDevice? SelectedMicrophoneMonitoringDevice
    {
        get => Runtime.SelectedMicrophoneMonitoringDevice;
        set => Runtime.SelectedMicrophoneMonitoringDevice = value;
    }
    public int SelectedMicrophoneMonitoringDeviceIndex
    {
        get => Runtime.SelectedMicrophoneMonitoringDeviceIndex;
        set => Runtime.SelectedMicrophoneMonitoringDeviceIndex = value;
    }
    public string SelectedMicrophoneMonitoringDeviceDisplayName =>
        Runtime.SelectedMicrophoneMonitoringDeviceDisplayName;
    public IAudioDevice? SelectedComputerMicrophoneDevice
    {
        get => Runtime.SelectedComputerMicrophoneDevice;
        set => Runtime.SelectedComputerMicrophoneDevice = value;
    }
    public int SelectedComputerMicrophoneDeviceIndex
    {
        get => Runtime.SelectedComputerMicrophoneDeviceIndex;
        set => Runtime.SelectedComputerMicrophoneDeviceIndex = value;
    }
    public string SelectedComputerMicrophoneDeviceDisplayName =>
        Runtime.SelectedComputerMicrophoneDeviceDisplayName;
    public bool MicrophoneOutputEnabled
    {
        get => Runtime.MicrophoneOutputEnabled;
        set => Runtime.MicrophoneOutputEnabled = value;
    }
    public float MicrophoneHubPeakPercent => Runtime.MicrophoneHubPeakPercent;
    public float ComputerMicrophonePeakPercent => Runtime.ComputerMicrophonePeakPercent;
    public float MicrophoneOutputGlowLevel => Runtime.MicrophoneOutputGlowLevel;
    public float MicrophoneOutputVolumePercent
    {
        get => Runtime.MicrophoneOutputVolumePercent;
        set => Runtime.MicrophoneOutputVolumePercent = value;
    }
    public bool MicrophoneOutputMuted
    {
        get => Runtime.MicrophoneOutputMuted;
        set => Runtime.MicrophoneOutputMuted = value;
    }
    public bool MicrophoneMonitoringEnabled
    {
        get => Runtime.MicrophoneMonitoringEnabled;
        set => Runtime.MicrophoneMonitoringEnabled = value;
    }
    public string MicrophoneOutputStatus => Runtime.MicrophoneOutputStatus;
}
