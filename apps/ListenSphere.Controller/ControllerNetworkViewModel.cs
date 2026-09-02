using System.Collections.ObjectModel;
using ListenSphere.Audio.Abstractions;
using ListenSphere.Configuration;
using ListenSphere.Device;
using ListenSphere.Diagnostics;
using ListenSphere.Network;
using ListenSphere.Windows.Audio;
using ListenSphere.Windows.Bluetooth;
using ListenSphere.Windows.Devices;
using ListenSphere.Windows.Usb;

namespace ListenSphere.Controller;

public sealed class ControllerNetworkViewModel : IAsyncDisposable
{
    public static readonly Guid LocalSoundChannelId =
        ControllerNetworkRuntime.LocalSoundChannelId;

    private readonly ControllerNetworkRuntime runtime;
    private readonly Func<ValueTask> disposeRuntime;
    private bool disposed;

    public ControllerNetworkViewModel(
        ListenSphereControlServer server,
        PairingCodeService pairingCodes,
        ITrustedDeviceStore trustStore,
        IAudioDeviceManager audioDeviceManager,
        IAudioOutputVolumeController outputVolume,
        IWasapiCaptureSourceFactory captureSourceFactory,
        IWasapiRecordingCaptureSourceFactory recordingCaptureSourceFactory,
        IProcessLoopbackCaptureSourceFactory processCaptureSourceFactory,
        IWindowsDeviceNotificationSource deviceNotifications,
        DiagnosticArchiveService diagnostics,
        BluetoothRfcommProbeHost bluetoothHost,
        UsbAccessoryHost usbHost,
        LocalDeviceIdentity identity)
        : this(new ControllerNetworkRuntime(
            server,
            pairingCodes,
            trustStore,
            audioDeviceManager,
            outputVolume,
            captureSourceFactory,
            recordingCaptureSourceFactory,
            processCaptureSourceFactory,
            deviceNotifications,
            diagnostics,
            bluetoothHost,
            usbHost,
            identity))
    {
    }

    internal ControllerNetworkViewModel(
        ControllerNetworkRuntime runtime,
        Func<ValueTask>? disposeRuntime = null)
    {
        this.runtime = runtime;
        this.disposeRuntime = disposeRuntime ?? runtime.DisposeAsync;
        Transport = new TransportViewModel(runtime);
        RemoteDevices = new RemoteDevicesViewModel(runtime);
        AudioOutput = new AudioOutputViewModel(runtime);
        LocalRouting = new LocalRoutingViewModel(runtime);
        Microphone = new MicrophoneViewModel(runtime);
        RemoteAudio = new RemoteAudioViewModel(runtime);
        GroupMixer = new GroupMixerViewModel(runtime);
    }

    public TransportViewModel Transport { get; }
    public RemoteDevicesViewModel RemoteDevices { get; }
    public AudioOutputViewModel AudioOutput { get; }
    public LocalRoutingViewModel LocalRouting { get; }
    public MicrophoneViewModel Microphone { get; }
    public RemoteAudioViewModel RemoteAudio { get; }
    public GroupMixerViewModel GroupMixer { get; }

    public event EventHandler? AudioSettingsChanged
    {
        add => runtime.AudioSettingsChanged += value;
        remove => runtime.AudioSettingsChanged -= value;
    }

    public event EventHandler? LocalSourceControlChanged
    {
        add => runtime.LocalSourceControlChanged += value;
        remove => runtime.LocalSourceControlChanged -= value;
    }

    public ObservableCollection<TrustedDevice> TrustedDevices => RemoteDevices.TrustedDevices;
    public ObservableCollection<RemoteChannelItemViewModel> RemoteChannels =>
        RemoteAudio.RemoteChannels;
    public ObservableCollection<AdditionalOutputDeviceItemViewModel> AdditionalOutputs =>
        AudioOutput.AdditionalOutputs;
    public AsyncRelayCommand GenerateCodeCommand => RemoteDevices.GenerateCodeCommand;
    public IAudioDevice? SelectedPlaybackDevice => AudioOutput.SelectedPlaybackDevice;
    public float MasterVolumePercent
    {
        get => AudioOutput.MasterVolumePercent;
        set => AudioOutput.MasterVolumePercent = value;
    }
    public bool FollowSystemDefaultPlayback
    {
        get => AudioOutput.FollowSystemDefaultPlayback;
        set => AudioOutput.FollowSystemDefaultPlayback = value;
    }
    public bool AutomaticRoutingEnabled
    {
        get => GroupMixer.AutomaticRoutingEnabled;
        set => GroupMixer.AutomaticRoutingEnabled = value;
    }
    public IAudioDevice? SelectedMicrophoneOutputDevice =>
        Microphone.SelectedMicrophoneOutputDevice;
    public IAudioDevice? SelectedMicrophoneMonitoringDevice =>
        Microphone.SelectedMicrophoneMonitoringDevice;
    public IAudioDevice? SelectedComputerMicrophoneDevice =>
        Microphone.SelectedComputerMicrophoneDevice;
    public bool MicrophoneOutputEnabled
    {
        get => Microphone.MicrophoneOutputEnabled;
        set => Microphone.MicrophoneOutputEnabled = value;
    }
    public float MicrophoneOutputVolumePercent
    {
        get => Microphone.MicrophoneOutputVolumePercent;
        set => Microphone.MicrophoneOutputVolumePercent = value;
    }
    public bool MicrophoneOutputMuted
    {
        get => Microphone.MicrophoneOutputMuted;
        set => Microphone.MicrophoneOutputMuted = value;
    }
    public bool MicrophoneMonitoringEnabled
    {
        get => Microphone.MicrophoneMonitoringEnabled;
        set => Microphone.MicrophoneMonitoringEnabled = value;
    }
    public float LocalSourceVolumePercent
    {
        get => LocalRouting.LocalSourceVolumePercent;
        set => LocalRouting.LocalSourceVolumePercent = value;
    }
    public bool IsLocalSourceMuted
    {
        get => LocalRouting.IsLocalSourceMuted;
        set => LocalRouting.IsLocalSourceMuted = value;
    }

    public Task InitializeAsync(
        string? preferredPlaybackDeviceId = null,
        float preferredMasterVolume = 1f,
        bool followSystemDefault = true,
        bool enableAutomaticRouting = true,
        IReadOnlyList<AudioRoutingRuleSettings>? savedRoutingRules = null,
        IReadOnlyList<ChannelLayoutSettings>? savedChannelLayouts = null,
        IReadOnlyList<AudioOutputRouteSettings>? savedOutputRoutes = null,
        string? preferredMicrophoneOutputDeviceId = null,
        float preferredMicrophoneOutputVolume = 1f,
        bool microphoneOutputMuted = false,
        bool microphoneMonitoringEnabled = false,
        float localSourceVolume = 1f,
        bool localSourceMuted = false,
        string? preferredMicrophoneMonitoringDeviceId = null,
        bool microphoneOutputEnabled = false,
        string? preferredComputerMicrophoneDeviceId = null) =>
        runtime.InitializeAsync(
            preferredPlaybackDeviceId,
            preferredMasterVolume,
            followSystemDefault,
            enableAutomaticRouting,
            savedRoutingRules,
            savedChannelLayouts,
            savedOutputRoutes,
            preferredMicrophoneOutputDeviceId,
            preferredMicrophoneOutputVolume,
            microphoneOutputMuted,
            microphoneMonitoringEnabled,
            localSourceVolume,
            localSourceMuted,
            preferredMicrophoneMonitoringDeviceId,
            microphoneOutputEnabled,
            preferredComputerMicrophoneDeviceId);

    public Task ApplyAudioSettingsAsync(
        string? playbackDeviceId,
        float masterVolume,
        bool followSystemDefault,
        IReadOnlyList<ChannelSettings> channels,
        IReadOnlyList<GroupBusSettings> groupBuses) =>
        runtime.ApplyAudioSettingsAsync(
            playbackDeviceId,
            masterVolume,
            followSystemDefault,
            channels,
            groupBuses);

    public IReadOnlyList<GroupBusSettings> CaptureGroupBusSettings() =>
        runtime.CaptureGroupBusSettings();

    public IReadOnlyList<AudioRoutingRuleSettings> CaptureRoutingRules() =>
        runtime.CaptureRoutingRules();

    public IReadOnlyList<ChannelLayoutSettings> CaptureChannelLayouts() =>
        runtime.CaptureChannelLayouts();

    public IReadOnlyList<AudioOutputRouteSettings> CaptureOutputRoutes() =>
        runtime.CaptureOutputRoutes();

    public void RegisterLocalApplicationSource(
        Guid channelId,
        int processId,
        string displayName,
        string identityKey) =>
        runtime.RegisterLocalApplicationSource(channelId, processId, displayName, identityKey);

    public Task UnregisterLocalApplicationSourceAsync(Guid channelId) =>
        runtime.UnregisterLocalApplicationSourceAsync(channelId);

    public Task RefreshAsync() => runtime.RefreshAsync();

    public Task AddSecondaryOutputRouteAsync(Guid channelId, string deviceId) =>
        LocalRouting.AddSecondaryOutputRouteAsync(channelId, deviceId);

    public Task RemoveSecondaryOutputRouteAsync(Guid channelId, string deviceId) =>
        LocalRouting.RemoveSecondaryOutputRouteAsync(channelId, deviceId);

    public IReadOnlyList<ApplicationOutputRouteInfo> GetApplicationOutputRoutes(Guid channelId) =>
        runtime.GetApplicationOutputRoutes(channelId)
            .Select(route => new ApplicationOutputRouteInfo(
                route.DeviceId,
                route.DeviceName,
                route.IsActive))
            .ToArray();

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Transport.Dispose();
        RemoteDevices.Dispose();
        AudioOutput.Dispose();
        LocalRouting.Dispose();
        Microphone.Dispose();
        RemoteAudio.Dispose();
        GroupMixer.Dispose();
        await disposeRuntime();
    }

    public sealed record ApplicationOutputRouteInfo(
        string DeviceId,
        string DeviceName,
        bool IsActive);
}
