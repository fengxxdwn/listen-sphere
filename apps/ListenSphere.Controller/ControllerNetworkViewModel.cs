using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using ListenSphere.Audio.Abstractions;
using ListenSphere.Audio.Engine;
using ListenSphere.Device;
using ListenSphere.Diagnostics;
using ListenSphere.Network;
using ListenSphere.Protocol;
using ListenSphere.Windows.Audio;
using ListenSphere.Windows.Bluetooth;
using ListenSphere.Windows.Devices;
using ListenSphere.Windows.Usb;
using ListenSphere.Controller.Coordinators;
using Serilog;

namespace ListenSphere.Controller;

public sealed class ControllerNetworkViewModel :
    INotifyPropertyChanged,
    IAsyncDisposable,
    IRemoteAudioFrameProcessor,
    IGroupMixerSettingsProvider
{
    public static readonly Guid LocalSoundChannelId = new("4c5f5426-fdc1-47b3-b7b4-60c31c6ea601");
    private static readonly Guid ComputerMicrophoneChannelId =
        new("603669e2-6a20-4d81-8bc0-24df59ed6d42");
    private static readonly string[] GroupBusNames =
        ["未分组", "游戏", "语音", "媒体", "系统", "自定义"];
    private readonly ListenSphereControlServer server;
    private readonly DiagnosticArchiveService diagnostics;
    private readonly BluetoothRfcommProbeHost bluetoothHost;
    private readonly UsbAccessoryHost usbHost;
    private readonly TransportCoordinator transportCoordinator;
    private readonly RemoteDeviceCoordinator remoteDeviceCoordinator;
    private readonly AudioOutputCoordinator audioOutputCoordinator;
    private readonly LocalAudioRoutingCoordinator localAudioRoutingCoordinator;
    private readonly MicrophoneHubCoordinator microphoneHubCoordinator;
    private readonly RemoteAudioCoordinator remoteAudioCoordinator;
    private readonly GroupMixerCoordinator groupMixerCoordinator;
    private readonly ConcurrentDictionary<Guid, RemoteChannelItemViewModel> channelsBySession = [];
    private readonly Dictionary<string, GroupBusItemViewModel> groupBusesByName =
        new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, ListenSphere.Configuration.ChannelSettings>
        rememberedChannelSettings = [];
    private readonly Dictionary<Guid, ListenSphere.Configuration.ChannelLayoutSettings>
        rememberedChannelLayouts = [];
    private string audioStatus = "远程音频接收尚未启动";
    private string audioErrorText = string.Empty;
    private float localSourceVolumePercent = 100;
    private bool isLocalSourceMuted;
    private bool initialized;
    private bool initializingAudioOutput;
    private bool applyingSettings;
    private bool isAudioDetailsOpen;
    private bool isGroupMixerOpen;
    private bool automaticRoutingEnabled = true;
    private RemoteTransportMode projectedTransport = RemoteTransportMode.Wireless;
    private IReadOnlyList<TrustedDevice>? projectedTrustedDevices;
    private IReadOnlyList<IAudioDevice>? projectedPlaybackDevices;
    private string? projectedPlaybackDeviceId;
    private string projectedAudioOutputStatus = string.Empty;
    private string projectedAudioOutputError = string.Empty;
    private long projectedLocalAudioRoutingRevision;
    private MicrophoneHubSnapshot projectedMicrophoneHubSnapshot =
        MicrophoneHubSnapshot.Empty;
    private string projectedMicrophoneActivityStatus = string.Empty;
    private string projectedMicrophoneError = string.Empty;
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
    {
        this.server = server;
        this.diagnostics = diagnostics;
        this.bluetoothHost = bluetoothHost;
        this.usbHost = usbHost;
        Identity = identity;
        transportCoordinator = new TransportCoordinator(
            server,
            bluetoothHost,
            usbHost,
            identity);
        remoteDeviceCoordinator = new RemoteDeviceCoordinator(
            pairingCodes,
            new RemoteDeviceRuntime(trustStore, bluetoothHost, usbHost, server));
        audioOutputCoordinator = new AudioOutputCoordinator(
            audioDeviceManager,
            outputVolume,
            deviceNotifications,
            () => server.AudioReceiver.Port);
        localAudioRoutingCoordinator = new LocalAudioRoutingCoordinator(
            captureSourceFactory,
            processCaptureSourceFactory,
            LocalSoundChannelId);
        microphoneHubCoordinator = new MicrophoneHubCoordinator(
            audioDeviceManager,
            recordingCaptureSourceFactory,
            ComputerMicrophoneChannelId);
        groupMixerCoordinator = new GroupMixerCoordinator(this);
        remoteAudioCoordinator = new RemoteAudioCoordinator(
            new ControllerRemoteAudioRuntime(
                server,
                bluetoothHost,
                usbHost,
                audioOutputCoordinator,
                diagnostics),
            this);
        transportCoordinator.SnapshotChanged += OnTransportSnapshotChanged;
        remoteDeviceCoordinator.SnapshotChanged += OnRemoteDeviceSnapshotChanged;
        audioOutputCoordinator.SnapshotChanged += OnAudioOutputSnapshotChanged;
        audioOutputCoordinator.OutputEvent += OnAudioOutputEvent;
        localAudioRoutingCoordinator.SnapshotChanged += OnLocalAudioRoutingSnapshotChanged;
        localAudioRoutingCoordinator.RoutesChanged += OnLocalAudioRoutesChanged;
        microphoneHubCoordinator.SnapshotChanged += OnMicrophoneHubSnapshotChanged;
        microphoneHubCoordinator.SettingsChanged += OnMicrophoneHubSettingsChanged;
        remoteAudioCoordinator.SnapshotChanged += OnRemoteAudioSnapshotChanged;
        remoteAudioCoordinator.MeterChanged += OnRemoteAudioMeterChanged;
        groupMixerCoordinator.SnapshotChanged += OnGroupMixerSnapshotChanged;
        foreach (string groupName in GroupBusNames)
        {
            GroupBusItemViewModel group = null!;
            group = new GroupBusItemViewModel(
                groupName,
                () => OnGroupBusSettingsChanged(group));
            groupBusesByName.Add(groupName, group);
            GroupBuses.Add(group);
            UpdateGroupMixerBus(group);
        }
        GenerateCodeCommand = new AsyncRelayCommand(remoteDeviceCoordinator.GenerateCodeAsync);
        RefreshOutputsCommand = new AsyncRelayCommand(RefreshOutputsAsync);
        SelectWirelessCommand = new AsyncRelayCommand(
            () => SelectTransportAsync(RemoteTransportMode.Wireless));
        SelectBluetoothCommand = new AsyncRelayCommand(
            () => SelectTransportAsync(RemoteTransportMode.Bluetooth));
        SelectWiredCommand = new AsyncRelayCommand(
            () => SelectTransportAsync(RemoteTransportMode.Wired));
        ConfirmDeleteCommand = new AsyncRelayCommand(
            () => remoteDeviceCoordinator.CompleteDeleteConfirmationAsync(true),
            () => IsDeleteConfirmationVisible);
        CancelDeleteCommand = new AsyncRelayCommand(
            () => remoteDeviceCoordinator.CompleteDeleteConfirmationAsync(false),
            () => IsDeleteConfirmationVisible);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? AudioSettingsChanged;
    public event EventHandler? LocalSourceControlChanged;

    public LocalDeviceIdentity Identity { get; }
    public ObservableCollection<TrustedDevice> TrustedDevices { get; } = [];
    public ObservableCollection<IAudioDevice> PlaybackDevices { get; } = [];
    public ObservableCollection<IAudioDevice> MicrophoneOutputDevices { get; } = [];
    public ObservableCollection<IAudioDevice> RecordingDevices { get; } = [];
    public ObservableCollection<RemoteChannelItemViewModel> RemoteChannels { get; } = [];
    public ObservableCollection<RemoteChannelItemViewModel> VisibleRemoteChannels { get; } = [];
    public ObservableCollection<RemoteChannelItemViewModel> ActiveRemoteChannels { get; } = [];
    public ObservableCollection<GroupBusItemViewModel> GroupBuses { get; } = [];
    public ObservableCollection<GroupBusItemViewModel> ActiveGroupBuses { get; } = [];
    public ObservableCollection<RoutingRuleItemViewModel> RoutingRules { get; } = [];
    public ObservableCollection<AdditionalOutputDeviceItemViewModel> AdditionalOutputs { get; } = [];
    public AsyncRelayCommand GenerateCodeCommand { get; }
    public AsyncRelayCommand RefreshOutputsCommand { get; }
    public AsyncRelayCommand SelectWirelessCommand { get; }
    public AsyncRelayCommand SelectBluetoothCommand { get; }
    public AsyncRelayCommand SelectWiredCommand { get; }
    public AsyncRelayCommand ConfirmDeleteCommand { get; }
    public AsyncRelayCommand CancelDeleteCommand { get; }

    public bool AutomaticRoutingEnabled
    {
        get => automaticRoutingEnabled;
        set
        {
            if (SetField(ref automaticRoutingEnabled, value))
            {
                OnPropertyChanged(nameof(AutomaticRoutingStatus));
                AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string AutomaticRoutingStatus => AutomaticRoutingEnabled
        ? $"已启用 · {RoutingRules.Count} 条已学习规则"
        : "已关闭 · 新声道不会自动套用规则";

    public string PairingCode => remoteDeviceCoordinator.Snapshot.PairingCode;

    public string PairingCodeActionText =>
        remoteDeviceCoordinator.Snapshot.PairingCodeActionText;

    public bool IsAudioDetailsOpen
    {
        get => isAudioDetailsOpen;
        set => SetField(ref isAudioDetailsOpen, value);
    }

    public bool IsGroupMixerOpen
    {
        get => isGroupMixerOpen;
        set => SetField(ref isGroupMixerOpen, value);
    }

    public bool IsSystemMuted
    {
        get => audioOutputCoordinator.Snapshot.IsSystemMuted;
        set => _ = audioOutputCoordinator.SetSystemMuteAsync(value);
    }

    public string SystemMuteButtonText => IsSystemMuted ? "取消系统静音" : "系统静音";

    public float LocalSourceVolumePercent
    {
        get => localSourceVolumePercent;
        set
        {
            float normalized = Math.Clamp(value, 0, 100);
            if (!SetField(ref localSourceVolumePercent, normalized) || applyingSettings)
            {
                return;
            }

            localAudioRoutingCoordinator.SetLocalSourceGain(
                LocalSourceVolumePercent / 100f,
                IsLocalSourceMuted);
            LocalSourceControlChanged?.Invoke(this, EventArgs.Empty);
            AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsLocalSourceMuted
    {
        get => isLocalSourceMuted;
        set
        {
            if (!SetField(ref isLocalSourceMuted, value)) return;
            OnPropertyChanged(nameof(LocalSourceMuteButtonText));
            if (applyingSettings) return;
            localAudioRoutingCoordinator.SetLocalSourceGain(
                LocalSourceVolumePercent / 100f,
                IsLocalSourceMuted);
            LocalSourceControlChanged?.Invoke(this, EventArgs.Empty);
            AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string LocalSourceMuteButtonText =>
        IsLocalSourceMuted ? "取消本机音源静音" : "静音本机音源";

    public RemoteTransportMode SelectedTransport =>
        transportCoordinator.Snapshot.SelectedTransport;

    public bool IsWirelessSelected => SelectedTransport == RemoteTransportMode.Wireless;
    public bool IsBluetoothSelected => SelectedTransport == RemoteTransportMode.Bluetooth;
    public bool IsWiredSelected => SelectedTransport == RemoteTransportMode.Wired;
    public string EmptyTransportText => SelectedTransport switch
    {
        RemoteTransportMode.Bluetooth => "尚无通过蓝牙连接的设备。",
        RemoteTransportMode.Wired => "尚无通过 USB 网络共享连接的设备。",
        _ => "尚无通过无线网络连接的设备。"
    };

    private async Task SelectTransportAsync(RemoteTransportMode transport)
    {
        await transportCoordinator.SelectTransportAsync(transport);
    }

    public string PairingHint => remoteDeviceCoordinator.Snapshot.PairingHint;

    public string NetworkStatus
    {
        get => transportCoordinator.Snapshot.NetworkStatus;
        private set => transportCoordinator.ReportNetworkStatus(value);
    }

    public string WirelessIpAddressText =>
        transportCoordinator.Snapshot.WirelessIpAddressText;

    public string WirelessPortText => transportCoordinator.Snapshot.WirelessPortText;

    public string AudioStatus
    {
        get => audioStatus;
        private set => SetField(ref audioStatus, value);
    }

    public string AudioErrorText
    {
        get => audioErrorText;
        private set => SetField(ref audioErrorText, value);
    }

    public string BluetoothStatus
    {
        get => transportCoordinator.Snapshot.BluetoothStatus;
        private set => transportCoordinator.ReportBluetoothStatus(value);
    }

    public string UsbStatus
    {
        get => transportCoordinator.Snapshot.UsbStatus;
        private set => transportCoordinator.ReportUsbStatus(value);
    }

    public string DeleteConfirmationDeviceName =>
        remoteDeviceCoordinator.Snapshot.DeleteConfirmationDeviceName;

    public bool IsDeleteConfirmationVisible =>
        remoteDeviceCoordinator.Snapshot.IsDeleteConfirmationVisible;

    public float MasterVolumePercent
    {
        get => audioOutputCoordinator.Snapshot.MasterVolumePercent;
        set
        {
            if (Math.Abs(MasterVolumePercent - Math.Clamp(value, 0, 100)) < 0.01f)
            {
                return;
            }
            audioOutputCoordinator.SetMasterVolume(value);
            AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IAudioDevice? SelectedPlaybackDevice
    {
        get
        {
            IAudioDevice? selected = audioOutputCoordinator.Snapshot.SelectedDevice;
            return PlaybackDevices.FirstOrDefault(device => string.Equals(
                    device.Id,
                    selected?.Id,
                    StringComparison.Ordinal)) ?? selected;
        }
        set
        {
            if (value is not null && !applyingSettings && !string.Equals(
                    value.Id,
                    SelectedPlaybackDevice?.Id,
                    StringComparison.Ordinal))
            {
                _ = audioOutputCoordinator.SelectDeviceAsync(value);
                AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string? SelectedPlaybackDeviceId
    {
        get => audioOutputCoordinator.Snapshot.SelectedDevice?.Id;
        set
        {
            if (value is null)
            {
                return;
            }
            IAudioDevice? device = PlaybackDevices.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, value, StringComparison.Ordinal));
            if (device is not null)
            {
                SelectedPlaybackDevice = device;
            }
        }
    }

    public int SelectedPlaybackDeviceIndex
    {
        get
        {
            string? selectedId = audioOutputCoordinator.Snapshot.SelectedDevice?.Id;
            if (selectedId is null)
            {
                return -1;
            }
            for (int index = 0; index < PlaybackDevices.Count; index++)
            {
                if (string.Equals(
                        PlaybackDevices[index].Id,
                        selectedId,
                        StringComparison.Ordinal))
                {
                    return index;
                }
            }
            return -1;
        }
        set
        {
            if (value >= 0 && value < PlaybackDevices.Count)
            {
                SelectedPlaybackDevice = PlaybackDevices[value];
            }
        }
    }

    public string SelectedPlaybackDeviceDisplayName =>
        audioOutputCoordinator.Snapshot.SelectedDevice?.DisplayName ?? string.Empty;

    public IAudioDevice? SelectedMicrophoneOutputDevice
    {
        get
        {
            IAudioDevice? selected = microphoneHubCoordinator.Snapshot.SelectedVirtualOutput;
            return MicrophoneOutputDevices.FirstOrDefault(device => string.Equals(
                    device.Id,
                    selected?.Id,
                    StringComparison.Ordinal)) ?? selected;
        }
        set
        {
            if (value is null || applyingSettings || string.Equals(
                    value?.Id,
                    SelectedMicrophoneOutputDevice?.Id,
                    StringComparison.Ordinal))
            {
                return;
            }
            _ = microphoneHubCoordinator.SelectVirtualOutputAsync(value);
        }
    }

    public string? SelectedMicrophoneOutputDeviceId
    {
        get => microphoneHubCoordinator.Snapshot.SelectedVirtualOutput?.Id;
        set
        {
            IAudioDevice? device = MicrophoneOutputDevices.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, value, StringComparison.Ordinal));
            if (device is not null)
            {
                SelectedMicrophoneOutputDevice = device;
            }
        }
    }

    public string SelectedMicrophoneOutputDeviceDisplayName =>
        microphoneHubCoordinator.Snapshot.SelectedVirtualOutput?.DisplayName ?? string.Empty;

    public IAudioDevice? SelectedMicrophoneMonitoringDevice
    {
        get
        {
            IAudioDevice? selected = microphoneHubCoordinator.Snapshot.SelectedMonitoringDevice;
            return PlaybackDevices.FirstOrDefault(device => string.Equals(
                    device.Id,
                    selected?.Id,
                    StringComparison.Ordinal)) ?? selected;
        }
        set
        {
            if (value is null || applyingSettings || string.Equals(
                    value?.Id,
                    SelectedMicrophoneMonitoringDevice?.Id,
                    StringComparison.Ordinal))
            {
                return;
            }
            _ = microphoneHubCoordinator.SelectMonitoringDeviceAsync(value);
        }
    }

    public string? SelectedMicrophoneMonitoringDeviceId
    {
        get => microphoneHubCoordinator.Snapshot.SelectedMonitoringDevice?.Id;
        set
        {
            IAudioDevice? device = PlaybackDevices.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, value, StringComparison.Ordinal));
            if (device is not null)
            {
                SelectedMicrophoneMonitoringDevice = device;
            }
        }
    }

    public string SelectedMicrophoneMonitoringDeviceDisplayName =>
        microphoneHubCoordinator.Snapshot.SelectedMonitoringDevice?.DisplayName ?? string.Empty;

    public IAudioDevice? SelectedComputerMicrophoneDevice
    {
        get
        {
            IAudioDevice? selected = microphoneHubCoordinator.Snapshot.SelectedComputerMicrophone;
            return RecordingDevices.FirstOrDefault(device => string.Equals(
                    device.Id,
                    selected?.Id,
                    StringComparison.Ordinal)) ?? selected;
        }
        set
        {
            if (value is null || applyingSettings || string.Equals(
                    value?.Id,
                    SelectedComputerMicrophoneDevice?.Id,
                    StringComparison.Ordinal))
            {
                return;
            }
            _ = microphoneHubCoordinator.SelectComputerMicrophoneAsync(value);
        }
    }

    public string? SelectedComputerMicrophoneDeviceId
    {
        get => microphoneHubCoordinator.Snapshot.SelectedComputerMicrophone?.Id;
        set
        {
            IAudioDevice? device = RecordingDevices.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, value, StringComparison.Ordinal));
            if (device is not null)
            {
                SelectedComputerMicrophoneDevice = device;
            }
        }
    }

    public string SelectedComputerMicrophoneDeviceDisplayName =>
        microphoneHubCoordinator.Snapshot.SelectedComputerMicrophone?.DisplayName ?? string.Empty;

    public bool MicrophoneOutputEnabled
    {
        get => microphoneHubCoordinator.Snapshot.IsEnabled;
        set
        {
            if (applyingSettings || value == MicrophoneOutputEnabled)
            {
                return;
            }
            _ = microphoneHubCoordinator.SetEnabledAsync(value);
        }
    }

    public float MicrophoneHubPeakPercent
    {
        get => microphoneHubCoordinator.Snapshot.AggregatePeakPercent;
    }

    public float ComputerMicrophonePeakPercent
    {
        get => microphoneHubCoordinator.Snapshot.ComputerPeakPercent;
    }

    public float MicrophoneOutputGlowLevel =>
        MicrophoneOutputEnabled &&
        !MicrophoneOutputMuted &&
        SelectedMicrophoneOutputDevice is not null
            ? MicrophoneHubPeakPercent * MicrophoneOutputVolumePercent / 100f
            : 0;

    public float MicrophoneOutputVolumePercent
    {
        get => microphoneHubCoordinator.Snapshot.VolumePercent;
        set
        {
            if (!applyingSettings)
            {
                microphoneHubCoordinator.SetVolume(value);
            }
        }
    }

    public bool MicrophoneOutputMuted
    {
        get => microphoneHubCoordinator.Snapshot.IsMuted;
        set
        {
            if (!applyingSettings)
            {
                microphoneHubCoordinator.SetMuted(value);
            }
        }
    }

    public bool MicrophoneMonitoringEnabled
    {
        get => microphoneHubCoordinator.Snapshot.IsMonitoringEnabled;
        set
        {
            if (!applyingSettings)
            {
                _ = microphoneHubCoordinator.SetMonitoringEnabledAsync(value);
            }
        }
    }

    public string MicrophoneOutputStatus
    {
        get => microphoneHubCoordinator.Snapshot.OutputStatus;
    }

    public bool FollowSystemDefaultPlayback
    {
        get => audioOutputCoordinator.Snapshot.FollowSystemDefault;
        set
        {
            if (!applyingSettings && value != FollowSystemDefaultPlayback)
            {
                _ = audioOutputCoordinator.SetFollowSystemDefaultAsync(value);
                AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public async Task InitializeAsync(
        string? preferredPlaybackDeviceId = null,
        float preferredMasterVolume = 1f,
        bool followSystemDefault = true,
        bool enableAutomaticRouting = true,
        IReadOnlyList<ListenSphere.Configuration.AudioRoutingRuleSettings>? savedRoutingRules = null,
        IReadOnlyList<ListenSphere.Configuration.ChannelLayoutSettings>? savedChannelLayouts = null,
        IReadOnlyList<ListenSphere.Configuration.AudioOutputRouteSettings>? savedOutputRoutes = null,
        string? preferredMicrophoneOutputDeviceId = null,
        float preferredMicrophoneOutputVolume = 1f,
        bool microphoneOutputMuted = false,
        bool microphoneMonitoringEnabled = false,
        float localSourceVolume = 1f,
        bool localSourceMuted = false,
        string? preferredMicrophoneMonitoringDeviceId = null,
        bool microphoneOutputEnabled = false,
        string? preferredComputerMicrophoneDeviceId = null)
    {
        if (initialized)
        {
            return;
        }

        initialized = true;
        applyingSettings = true;
        LocalSourceVolumePercent = Math.Clamp(localSourceVolume, 0f, 1f) * 100;
        IsLocalSourceMuted = localSourceMuted;
        automaticRoutingEnabled = enableAutomaticRouting;
        RoutingRules.Clear();
        foreach (ListenSphere.Configuration.AudioRoutingRuleSettings rule in savedRoutingRules ?? [])
        {
            RoutingRules.Add(CreateRoutingRuleItem(rule));
        }
        rememberedChannelLayouts.Clear();
        foreach (ListenSphere.Configuration.ChannelLayoutSettings layout in savedChannelLayouts ?? [])
        {
            rememberedChannelLayouts[layout.ChannelId] = layout;
        }
        localAudioRoutingCoordinator.ConfigureRoutes(savedOutputRoutes ?? []);
        localAudioRoutingCoordinator.SetLocalSourceGain(
            LocalSourceVolumePercent / 100f,
            IsLocalSourceMuted);
        OnPropertyChanged(nameof(AutomaticRoutingEnabled));
        OnPropertyChanged(nameof(AutomaticRoutingStatus));
        applyingSettings = false;
        server.PeerChanged += OnPeerChanged;
        bluetoothHost.ProbeReceived += OnBluetoothProbeReceived;
        bluetoothHost.SessionChanged += OnBluetoothSessionChanged;
        bluetoothHost.Faulted += OnBluetoothHostFaulted;
        usbHost.StatusChanged += OnUsbStatusChanged;
        usbHost.SessionChanged += OnUsbSessionChanged;
        usbHost.Faulted += OnUsbHostFaulted;
        await transportCoordinator.InitializeAsync();
        await remoteDeviceCoordinator.InitializeAsync();
        initializingAudioOutput = true;
        try
        {
            await audioOutputCoordinator.InitializeAsync(
                preferredPlaybackDeviceId,
                Math.Clamp(preferredMasterVolume, 0f, 1f) * 100,
                followSystemDefault);
            ApplyAudioOutputSnapshot(audioOutputCoordinator.Snapshot);
            AudioOutputSnapshot output = audioOutputCoordinator.Snapshot;
            await microphoneHubCoordinator.InitializeAsync(
                output.Devices,
                output.SelectedDevice?.Id,
                preferredMicrophoneOutputDeviceId,
                preferredMicrophoneMonitoringDeviceId,
                preferredComputerMicrophoneDeviceId,
                microphoneOutputEnabled,
                microphoneMonitoringEnabled,
                Math.Clamp(preferredMicrophoneOutputVolume, 0f, 1f) * 100,
                microphoneOutputMuted);
            ApplyMicrophoneHubSnapshot(microphoneHubCoordinator.Snapshot);
            await UpdateLocalAudioRoutingAsync();
        }
        finally
        {
            initializingAudioOutput = false;
        }
        remoteAudioCoordinator.Initialize();
    }

    public async Task ApplyAudioSettingsAsync(
        string? playbackDeviceId,
        float masterVolume,
        bool followSystemDefault,
        IReadOnlyList<ListenSphere.Configuration.ChannelSettings> channels,
        IReadOnlyList<ListenSphere.Configuration.GroupBusSettings> groupBuses)
    {
        applyingSettings = true;
        try
        {
            rememberedChannelSettings.Clear();
            foreach (ListenSphere.Configuration.ChannelSettings settings in channels)
            {
                rememberedChannelSettings[settings.ChannelId] = settings;
            }
            foreach (ListenSphere.Configuration.GroupBusSettings settings in groupBuses)
            {
                if (groupBusesByName.TryGetValue(settings.Name, out GroupBusItemViewModel? group))
                {
                    group.Apply(settings.Volume, settings.IsMuted, settings.EqualizerPreset);
                    UpdateGroupMixerBus(group);
                }
            }
            foreach (ListenSphere.Configuration.ChannelSettings settings in channels)
            {
                RemoteChannelItemViewModel? channel = RemoteChannels.FirstOrDefault(
                    item => item.ChannelId == settings.ChannelId);
                channel?.Apply(
                    settings.Volume,
                    settings.IsMuted,
                    settings.EqualizerPreset,
                    settings.EqualizerGains,
                    settings.EqualizerEnabled,
                    settings.ChannelGroup,
                    settings.PreampDb,
                    settings.NoiseGateEnabled,
                    settings.NoiseGateThresholdDb,
                    settings.CompressorEnabled,
                    settings.CompressorThresholdDb,
                    settings.CompressorRatio,
                    settings.LimiterEnabled,
                    settings.LimiterCeilingDb,
                    settings.IsVoiceDuckingTrigger,
                    settings.IsVoiceDuckingTarget,
                    settings.VoiceDuckingReductionDb);
            }

        }
        finally
        {
            applyingSettings = false;
        }

        await audioOutputCoordinator.ApplySettingsAsync(
            playbackDeviceId,
            Math.Clamp(masterVolume, 0f, 1f) * 100,
            followSystemDefault);
        ApplyAudioOutputSnapshot(audioOutputCoordinator.Snapshot);

        AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<ListenSphere.Configuration.GroupBusSettings> CaptureGroupBusSettings() =>
        GroupBuses.Select(group => new ListenSphere.Configuration.GroupBusSettings(
            group.Name,
            group.VolumePercent / 100,
            group.IsMuted,
            group.SelectedEqualizerPreset.Name)).ToArray();

    public IReadOnlyList<ListenSphere.Configuration.AudioRoutingRuleSettings>
        CaptureRoutingRules() => RoutingRules.Select(rule => rule.ToSettings()).ToArray();

    public IReadOnlyList<ListenSphere.Configuration.ChannelLayoutSettings>
        CaptureChannelLayouts() => rememberedChannelLayouts.Values
            .OrderByDescending(layout => layout.IsPinned)
            .ThenBy(layout => layout.SortOrder)
            .ToArray();

    public IReadOnlyList<ListenSphere.Configuration.AudioOutputRouteSettings>
        CaptureOutputRoutes() => localAudioRoutingCoordinator.CaptureRoutes();

    public void RegisterLocalApplicationSource(
        Guid channelId,
        int processId,
        string displayName,
        string identityKey)
    {
        localAudioRoutingCoordinator.RegisterApplicationSource(
            channelId,
            processId,
            displayName,
            identityKey);
    }

    public async Task UnregisterLocalApplicationSourceAsync(Guid channelId)
    {
        await localAudioRoutingCoordinator.UnregisterApplicationSourceAsync(channelId);
    }

    private async Task RefreshOutputsAsync()
    {
        await audioOutputCoordinator.RefreshAsync();
        ApplyAudioOutputSnapshot(audioOutputCoordinator.Snapshot);
        await RefreshMicrophoneDevicesAsync();
    }

    public async Task RefreshAsync()
    {
        await transportCoordinator.RefreshAsync();
        await RefreshOutputsAsync();
        await remoteDeviceCoordinator.RefreshTrustedDevicesAsync();
    }

    private void OnTransportSnapshotChanged(object? sender, TransportSnapshot snapshot)
    {
        void ApplySnapshot()
        {
            bool transportChanged = projectedTransport != snapshot.SelectedTransport;
            projectedTransport = snapshot.SelectedTransport;
            OnPropertyChanged(nameof(SelectedTransport));
            OnPropertyChanged(nameof(IsWirelessSelected));
            OnPropertyChanged(nameof(IsBluetoothSelected));
            OnPropertyChanged(nameof(IsWiredSelected));
            OnPropertyChanged(nameof(EmptyTransportText));
            OnPropertyChanged(nameof(NetworkStatus));
            OnPropertyChanged(nameof(WirelessIpAddressText));
            OnPropertyChanged(nameof(WirelessPortText));
            OnPropertyChanged(nameof(BluetoothStatus));
            OnPropertyChanged(nameof(UsbStatus));
            if (transportChanged)
            {
                RebuildVisibleChannels();
            }
        }

        if (Application.Current.Dispatcher.CheckAccess())
        {
            ApplySnapshot();
        }
        else
        {
            _ = Application.Current.Dispatcher.BeginInvoke(ApplySnapshot);
        }
    }

    private void OnRemoteDeviceSnapshotChanged(
        object? sender,
        RemoteDeviceSnapshot snapshot)
    {
        void ApplySnapshot()
        {
            OnPropertyChanged(nameof(PairingCode));
            OnPropertyChanged(nameof(PairingCodeActionText));
            OnPropertyChanged(nameof(PairingHint));
            OnPropertyChanged(nameof(DeleteConfirmationDeviceName));
            OnPropertyChanged(nameof(IsDeleteConfirmationVisible));
            ConfirmDeleteCommand.RaiseCanExecuteChanged();
            CancelDeleteCommand.RaiseCanExecuteChanged();

            if (ReferenceEquals(projectedTrustedDevices, snapshot.TrustedDevices))
            {
                return;
            }

            projectedTrustedDevices = snapshot.TrustedDevices;
            TrustedDevices.Clear();
            foreach (TrustedDevice device in snapshot.TrustedDevices)
            {
                TrustedDevices.Add(device);
                RemoteChannelItemViewModel channel = EnsureRemoteChannel(
                    device.DeviceId,
                    device.DisplayName,
                    ParseTransport(device.Transport));
                foreach (string transport in device.ObservedTransports)
                {
                    channel.RememberTransport(ParseTransport(transport));
                }
            }
            RebuildVisibleChannels();
        }

        if (Application.Current.Dispatcher.CheckAccess())
        {
            ApplySnapshot();
        }
        else
        {
            _ = Application.Current.Dispatcher.BeginInvoke(ApplySnapshot);
        }
    }

    private void OnAudioOutputSnapshotChanged(
        object? sender,
        AudioOutputSnapshot snapshot)
    {
        void ApplySnapshot() => ApplyAudioOutputSnapshot(audioOutputCoordinator.Snapshot);
        if (Application.Current.Dispatcher.CheckAccess())
        {
            ApplySnapshot();
        }
        else
        {
            _ = Application.Current.Dispatcher.BeginInvoke(ApplySnapshot);
        }
    }

    private void ApplyAudioOutputSnapshot(AudioOutputSnapshot snapshot)
    {
        bool devicesChanged = !ReferenceEquals(projectedPlaybackDevices, snapshot.Devices);
        bool selectedChanged = !string.Equals(
            projectedPlaybackDeviceId,
            snapshot.SelectedDevice?.Id,
            StringComparison.Ordinal);
        projectedPlaybackDevices = snapshot.Devices;
        projectedPlaybackDeviceId = snapshot.SelectedDevice?.Id;

        if (devicesChanged)
        {
            PlaybackDevices.Clear();
            foreach (IAudioDevice device in snapshot.Devices)
            {
                PlaybackDevices.Add(device);
            }
        }

        OnPropertyChanged(nameof(SelectedPlaybackDevice));
        OnPropertyChanged(nameof(SelectedPlaybackDeviceId));
        OnPropertyChanged(nameof(SelectedPlaybackDeviceIndex));
        OnPropertyChanged(nameof(SelectedPlaybackDeviceDisplayName));
        OnPropertyChanged(nameof(FollowSystemDefaultPlayback));
        OnPropertyChanged(nameof(MasterVolumePercent));
        OnPropertyChanged(nameof(IsSystemMuted));
        OnPropertyChanged(nameof(SystemMuteButtonText));
        if (!string.Equals(projectedAudioOutputStatus, snapshot.Status, StringComparison.Ordinal))
        {
            projectedAudioOutputStatus = snapshot.Status;
            AudioStatus = snapshot.Status;
        }
        if (!string.Equals(projectedAudioOutputError, snapshot.ErrorText, StringComparison.Ordinal))
        {
            projectedAudioOutputError = snapshot.ErrorText;
            AudioErrorText = snapshot.ErrorText;
        }

        if (!initialized || applyingSettings || initializingAudioOutput)
        {
            return;
        }
        if (devicesChanged)
        {
            _ = RefreshMicrophoneDevicesAsync();
        }
        if (devicesChanged || selectedChanged)
        {
            _ = RefreshOutputProjectionAsync();
        }
    }

    private async Task RefreshOutputProjectionAsync()
    {
        await UpdateLocalAudioRoutingAsync();
    }

    private Task UpdateLocalAudioRoutingAsync()
    {
        AudioOutputSnapshot output = audioOutputCoordinator.Snapshot;
        return localAudioRoutingCoordinator.UpdateOutputDevicesAsync(
            output.Devices,
            output.Endpoints,
            output.SelectedDevice?.Id);
    }

    private void OnLocalAudioRoutingSnapshotChanged(
        object? sender,
        LocalAudioRoutingSnapshot snapshot)
    {
        void ApplySnapshot() => ApplyLocalAudioRoutingSnapshot(
            localAudioRoutingCoordinator.Snapshot);
        if (Application.Current.Dispatcher.CheckAccess())
        {
            ApplySnapshot();
        }
        else
        {
            _ = Application.Current.Dispatcher.BeginInvoke(ApplySnapshot);
        }
    }

    private void ApplyLocalAudioRoutingSnapshot(LocalAudioRoutingSnapshot snapshot)
    {
        if (snapshot.Revision <= projectedLocalAudioRoutingRevision)
        {
            return;
        }
        projectedLocalAudioRoutingRevision = snapshot.Revision;
        AdditionalOutputs.Clear();
        foreach (LocalAudioOutputDeviceSnapshot output in snapshot.Outputs)
        {
            var target = new AdditionalOutputDeviceItemViewModel(
                output.DeviceId,
                output.DisplayName,
                output.VolumePercent,
                output.IsMuted,
                audioOutputCoordinator.SetEndpointVolume,
                (id, muted) => _ = audioOutputCoordinator.SetEndpointMuteAsync(id, muted));
            foreach (LocalAudioRouteSnapshot route in output.Routes)
            {
                target.Routes.Add(new AdditionalOutputRouteItemViewModel(
                    route.ChannelId,
                    route.DeviceId,
                    route.SourceName,
                    route.IsActive,
                    RemoveSecondaryOutputRouteAsync));
            }
            target.RefreshSummary();
            AdditionalOutputs.Add(target);
        }
        if (!string.IsNullOrWhiteSpace(snapshot.Status))
        {
            AudioStatus = snapshot.Status;
        }
        if (!string.IsNullOrWhiteSpace(snapshot.ErrorText))
        {
            AudioErrorText = snapshot.ErrorText;
        }
    }

    private void OnLocalAudioRoutesChanged(object? sender, EventArgs args)
    {
        void Notify() => AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
        if (Application.Current.Dispatcher.CheckAccess())
        {
            Notify();
        }
        else
        {
            _ = Application.Current.Dispatcher.BeginInvoke(Notify);
        }
    }

    private void OnMicrophoneHubSnapshotChanged(
        object? sender,
        MicrophoneHubSnapshot snapshot)
    {
        void ApplySnapshot() => ApplyMicrophoneHubSnapshot(microphoneHubCoordinator.Snapshot);
        if (Application.Current.Dispatcher.CheckAccess())
        {
            ApplySnapshot();
        }
        else
        {
            _ = Application.Current.Dispatcher.BeginInvoke(ApplySnapshot);
        }
    }

    private void ApplyMicrophoneHubSnapshot(MicrophoneHubSnapshot snapshot)
    {
        MicrophoneHubSnapshot previous = projectedMicrophoneHubSnapshot;
        if (snapshot.Revision <= previous.Revision)
        {
            return;
        }
        projectedMicrophoneHubSnapshot = snapshot;
        if (!ReferenceEquals(previous.RecordingDevices, snapshot.RecordingDevices))
        {
            RecordingDevices.Clear();
            foreach (IAudioDevice device in snapshot.RecordingDevices)
            {
                RecordingDevices.Add(device);
            }
        }
        if (!ReferenceEquals(previous.VirtualOutputDevices, snapshot.VirtualOutputDevices))
        {
            MicrophoneOutputDevices.Clear();
            foreach (IAudioDevice device in snapshot.VirtualOutputDevices)
            {
                MicrophoneOutputDevices.Add(device);
            }
        }
        if (!string.Equals(
                previous.SelectedComputerMicrophone?.Id,
                snapshot.SelectedComputerMicrophone?.Id,
                StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(SelectedComputerMicrophoneDevice));
            OnPropertyChanged(nameof(SelectedComputerMicrophoneDeviceId));
            OnPropertyChanged(nameof(SelectedComputerMicrophoneDeviceDisplayName));
        }
        if (!string.Equals(
                previous.SelectedVirtualOutput?.Id,
                snapshot.SelectedVirtualOutput?.Id,
                StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(SelectedMicrophoneOutputDevice));
            OnPropertyChanged(nameof(SelectedMicrophoneOutputDeviceId));
            OnPropertyChanged(nameof(SelectedMicrophoneOutputDeviceDisplayName));
        }
        if (!string.Equals(
                previous.SelectedMonitoringDevice?.Id,
                snapshot.SelectedMonitoringDevice?.Id,
                StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(SelectedMicrophoneMonitoringDevice));
            OnPropertyChanged(nameof(SelectedMicrophoneMonitoringDeviceId));
            OnPropertyChanged(nameof(SelectedMicrophoneMonitoringDeviceDisplayName));
        }
        if (previous.IsEnabled != snapshot.IsEnabled)
        {
            OnPropertyChanged(nameof(MicrophoneOutputEnabled));
        }
        if (previous.IsMonitoringEnabled != snapshot.IsMonitoringEnabled)
        {
            OnPropertyChanged(nameof(MicrophoneMonitoringEnabled));
        }
        if (Math.Abs(previous.VolumePercent - snapshot.VolumePercent) >= 0.01f)
        {
            OnPropertyChanged(nameof(MicrophoneOutputVolumePercent));
        }
        if (previous.IsMuted != snapshot.IsMuted)
        {
            OnPropertyChanged(nameof(MicrophoneOutputMuted));
        }
        if (Math.Abs(previous.AggregatePeakPercent - snapshot.AggregatePeakPercent) >= 0.01f)
        {
            OnPropertyChanged(nameof(MicrophoneHubPeakPercent));
        }
        if (Math.Abs(previous.ComputerPeakPercent - snapshot.ComputerPeakPercent) >= 0.01f)
        {
            OnPropertyChanged(nameof(ComputerMicrophonePeakPercent));
        }
        OnPropertyChanged(nameof(MicrophoneOutputGlowLevel));
        if (!string.Equals(previous.OutputStatus, snapshot.OutputStatus, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(MicrophoneOutputStatus));
        }
        if (!string.IsNullOrWhiteSpace(snapshot.ActivityStatus) && !string.Equals(
                projectedMicrophoneActivityStatus,
                snapshot.ActivityStatus,
                StringComparison.Ordinal))
        {
            projectedMicrophoneActivityStatus = snapshot.ActivityStatus;
            AudioStatus = snapshot.ActivityStatus;
        }
        if (!string.IsNullOrWhiteSpace(snapshot.ErrorText) && !string.Equals(
                projectedMicrophoneError,
                snapshot.ErrorText,
                StringComparison.Ordinal))
        {
            projectedMicrophoneError = snapshot.ErrorText;
            AudioErrorText = snapshot.ErrorText;
        }
    }

    private void OnMicrophoneHubSettingsChanged(object? sender, EventArgs args)
    {
        void Notify() => AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
        if (Application.Current.Dispatcher.CheckAccess())
        {
            Notify();
        }
        else
        {
            _ = Application.Current.Dispatcher.BeginInvoke(Notify);
        }
    }

    private void OnAudioOutputEvent(object? sender, AudioOutputEvent outputEvent)
    {
        switch (outputEvent.Kind)
        {
            case AudioOutputEventKind.DeviceChanged:
                diagnostics.Record(
                    DiagnosticSeverity.Information,
                    "windows.audio-device.changed",
                    new Dictionary<string, object?>
                    {
                        ["change"] = outputEvent.DeviceChange?.ToString()
                    });
                break;
            case AudioOutputEventKind.EndpointReady:
                diagnostics.Record(
                    DiagnosticSeverity.Information,
                    "playback.endpoint.ready",
                    new Dictionary<string, object?>
                    {
                        ["isDefault"] = outputEvent.IsDefault
                    });
                break;
            case AudioOutputEventKind.EndpointFailed:
                RecordAudioOutputFailure("playback.endpoint.failed", outputEvent.Exception);
                break;
            case AudioOutputEventKind.WriteFailed:
                RecordAudioOutputFailure("playback.write.failed", outputEvent.Exception);
                break;
            case AudioOutputEventKind.UnexpectedStop:
                diagnostics.Record(
                    outputEvent.Exception is null
                        ? DiagnosticSeverity.Warning
                        : DiagnosticSeverity.Error,
                    "playback.unexpected-stop",
                    outputEvent.Exception is null
                        ? null
                        : new Dictionary<string, object?>
                        {
                            ["exceptionType"] = outputEvent.Exception.GetType().Name
                        });
                break;
        }
    }

    private void RecordAudioOutputFailure(string eventName, Exception? exception) =>
        diagnostics.Record(
            DiagnosticSeverity.Error,
            eventName,
            exception is null
                ? null
                : new Dictionary<string, object?>
                {
                    ["exceptionType"] = exception.GetType().Name
                });

    private async Task RefreshMicrophoneDevicesAsync(
        string? preferredMicrophoneDeviceId = null,
        string? preferredMicrophoneMonitoringDeviceId = null,
        string? preferredComputerMicrophoneDeviceId = null)
    {
        AudioOutputSnapshot output = audioOutputCoordinator.Snapshot;
        await microphoneHubCoordinator.RefreshDevicesAsync(
            output.Devices,
            output.SelectedDevice?.Id,
            preferredMicrophoneDeviceId,
            preferredMicrophoneMonitoringDeviceId,
            preferredComputerMicrophoneDeviceId);
        await UpdateLocalAudioRoutingAsync();
    }

    async ValueTask<RemoteAudioFrameResult> IRemoteAudioFrameProcessor.ProcessAsync(
        Guid sessionId,
        byte[] pcm,
        ulong timestamp,
        CancellationToken cancellationToken)
    {
        channelsBySession.TryGetValue(sessionId, out RemoteChannelItemViewModel? channel);
        GroupMixerProcessResult processed = groupMixerCoordinator.Process(sessionId, pcm);
        float? peak = processed.HasChannel ? processed.Peak : null;
        if (channel is not null && peak is float observedPeak)
        {
            channel.ObservePostProcessingPeak(observedPeak);
            channel.ObserveLimiterActivity(processed.Dynamics.Limited);
        }

        await WriteSecondaryOutputRoutesAsync(
            channel,
            pcm,
            timestamp,
            cancellationToken).ConfigureAwait(false);
        await WriteMicrophoneOutputAsync(
            channel,
            pcm,
            timestamp,
            cancellationToken).ConfigureAwait(false);
        bool isMicrophone = channel?.IsMicrophoneSource == true;
        if (isMicrophone)
        {
            await WriteMicrophoneMonitoringAsync(
                channel!,
                pcm,
                timestamp,
                cancellationToken).ConfigureAwait(false);
        }

        return new RemoteAudioFrameResult(
            RouteToMainMixer: !isMicrophone,
            Peak: peak,
            Dynamics: processed.Dynamics,
            DuckingActive: processed.DuckingActive);
    }

    bool IGroupMixerSettingsProvider.TryGetChannelSettings(
        Guid sessionId,
        out GroupMixerChannelSettings settings)
    {
        if (!channelsBySession.TryGetValue(
                sessionId,
                out RemoteChannelItemViewModel? channel))
        {
            settings = default;
            return false;
        }

        settings = new GroupMixerChannelSettings(
            channel.EffectiveGain,
            channel.IsEqualizerEnabled,
            channel.EqualizerGains,
            channel.DynamicsSettings,
            channel.SelectedChannelGroup,
            channel.IsVoiceDuckingTrigger,
            channel.IsVoiceDuckingTarget,
            channel.VoiceDuckingReductionDb);
        return true;
    }

    private void OnRemoteAudioMeterChanged(
        object? sender,
        RemoteAudioMeterUpdate update)
    {
        _ = Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (channelsBySession.TryGetValue(
                    update.SessionId,
                    out RemoteChannelItemViewModel? channel))
            {
                channel.PeakPercent = update.Peak * 100;
                channel.UpdateDynamicsStatus(update.Dynamics, update.DuckingActive);
            }
        });
    }

    private void OnRemoteAudioSnapshotChanged(
        object? sender,
        RemoteAudioSnapshot snapshot)
    {
        _ = Application.Current.Dispatcher.BeginInvoke(() =>
        {
            foreach (UdpAudioSessionStatistics session in snapshot.NetworkSessions)
            {
                if (channelsBySession.TryGetValue(
                        session.SessionId,
                        out RemoteChannelItemViewModel? channel) &&
                    channel.UpdateNetworkQuality(session))
                {
                    diagnostics.Record(
                        DiagnosticSeverity.Warning,
                        "audio.channel.quality-warning",
                        new Dictionary<string, object?>
                        {
                            ["targetBufferMs"] = session.TargetBufferMilliseconds,
                            ["jitterMs"] = Math.Round(
                                session.EstimatedJitterMilliseconds,
                                1)
                        });
                }
            }

            foreach (BluetoothAudioSessionStatistics session in snapshot.BluetoothSessions)
            {
                if (channelsBySession.TryGetValue(
                        session.SessionId,
                        out RemoteChannelItemViewModel? channel) &&
                    channel.UpdateBluetoothQuality(session))
                {
                    diagnostics.Record(
                        DiagnosticSeverity.Warning,
                        "audio.bluetooth.quality-warning",
                        new Dictionary<string, object?>
                        {
                            ["codec"] = session.Codec.ToString(),
                            ["channels"] = session.ChannelCount,
                            ["queueDepth"] = session.QueueDepth,
                            ["jitterMs"] = Math.Round(
                                session.EstimatedJitterMilliseconds,
                                1)
                        });
                }
            }

            UpdateBluetoothDuplexStatus(snapshot.BluetoothSessions);
            AudioStatus = snapshot.Status;
            if (!string.IsNullOrWhiteSpace(snapshot.ErrorText))
            {
                AudioErrorText = snapshot.ErrorText;
            }
        });
    }
    public async Task AddSecondaryOutputRouteAsync(Guid channelId, string deviceId)
    {
        RemoteChannelItemViewModel? channel = RemoteChannels.FirstOrDefault(
            item => item.ChannelId == channelId && item.IsOnline);
        bool isLocalSound = channelId == LocalSoundChannelId;
        string? localApplicationName =
            localAudioRoutingCoordinator.GetApplicationSourceName(channelId);
        if (!isLocalSound && localApplicationName is null && channel is null)
        {
            return;
        }
        string sourceName = isLocalSound
            ? "本地声音"
            : localApplicationName is not null
                ? localApplicationName
                : channel!.DisplayName;
        await localAudioRoutingCoordinator.AddRouteAsync(
            channelId,
            deviceId,
            sourceName);
    }

    public async Task RemoveSecondaryOutputRouteAsync(Guid channelId, string deviceId)
    {
        LocalAudioRouteSnapshot? route = localAudioRoutingCoordinator
            .GetRoutes(channelId)
            .FirstOrDefault(candidate => string.Equals(
                candidate.DeviceId,
                deviceId,
                StringComparison.Ordinal));
        Task cleanup = localAudioRoutingCoordinator.RemoveRouteAsync(channelId, deviceId);
        ApplyLocalAudioRoutingSnapshot(localAudioRoutingCoordinator.Snapshot);
        AudioStatus = route is null
            ? "附加输出路由已移除。"
            : $"已从 {route.DeviceName} 移除“{route.SourceName}”。";
        await cleanup;
    }

    public IReadOnlyList<ApplicationOutputRouteInfo> GetApplicationOutputRoutes(Guid channelId)
    {
        return localAudioRoutingCoordinator.GetRoutes(channelId)
            .Select(route => new ApplicationOutputRouteInfo(
                route.DeviceId,
                route.DeviceName,
                route.IsActive))
            .OrderBy(route => route.DeviceName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private async ValueTask WriteSecondaryOutputRoutesAsync(
        RemoteChannelItemViewModel? channel,
        byte[] pcm,
        ulong timestamp,
        CancellationToken cancellationToken)
    {
        if (channel is null)
        {
            return;
        }

        await localAudioRoutingCoordinator.WriteAsync(
            channel.ChannelId,
            pcm,
            timestamp,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteMicrophoneOutputAsync(
        RemoteChannelItemViewModel? channel,
        byte[] pcm,
        ulong timestamp,
        CancellationToken cancellationToken)
    {
        if (channel?.IsMicrophoneSource != true)
        {
            return;
        }
        await microphoneHubCoordinator.WriteOutputAsync(
            channel.ChannelId,
            pcm,
            timestamp,
            cancellationToken).ConfigureAwait(false);
    }

    private ValueTask WriteMicrophoneMonitoringAsync(
        RemoteChannelItemViewModel channel,
        byte[] pcm,
        ulong timestamp,
        CancellationToken cancellationToken) =>
        microphoneHubCoordinator.WriteMonitoringAsync(
            channel.ChannelId,
            pcm,
            timestamp,
            cancellationToken);

    private void UpdateBluetoothDuplexStatus(
        IReadOnlyList<BluetoothAudioSessionStatistics> sessions)
    {
        if (sessions.Count == 0 ||
            SelectedPlaybackDevice is not WindowsAudioDevice { IsBluetooth: true })
        {
            return;
        }

        bool highBandwidth = sessions.Any(session =>
            session.Codec == AudioCodec.PcmInt16 && session.ChannelCount == 2);
        BluetoothStatus = highBandwidth
            ? "双蓝牙链路已启用抗掉帧缓冲；PCM16 双声道占用较高，建议改用 IMA ADPCM 或 AAC-LC。"
            : "双蓝牙链路已启用抗掉帧缓冲。";
    }

    private async Task RevokeDeviceAsync(Guid deviceId)
    {
        RemoteChannelItemViewModel? channel = RemoteChannels.FirstOrDefault(
            item => item.ParentDeviceId == deviceId && !item.IsApplicationSource);
        RemoteDeviceMutation? mutation = await remoteDeviceCoordinator.RevokeAsync(
            deviceId,
            channel?.DisplayName,
            CancellationToken.None);
        if (mutation is null)
        {
            return;
        }

        NetworkStatus = $"已删除设备：{mutation.DisplayName}；再次连接需要重新配对。";
        RemoteChannelItemViewModel[] removingChannels = RemoteChannels
            .Where(item => item.ParentDeviceId == deviceId).ToArray();
        await localAudioRoutingCoordinator.RemoveChannelsAsync(
            removingChannels.Select(item => item.ChannelId));
        foreach (RemoteChannelItemViewModel removing in removingChannels)
        {
            if (removing.SessionId is Guid sessionId)
            {
                RemoveRemoteAudioSession(sessionId);
            }

            RemoteChannels.Remove(removing);
            VisibleRemoteChannels.Remove(removing);
            ActiveRemoteChannels.Remove(removing);
        }

    }

    private RemoteChannelItemViewModel EnsureRemoteChannel(
        Guid deviceId,
        string displayName,
        RemoteTransportMode transport = RemoteTransportMode.Wireless,
        string? sourceKind = null)
    {
        RemoteChannelItemViewModel? existing =
            RemoteChannels.FirstOrDefault(item => item.ChannelId == deviceId);
        if (existing is not null)
        {
            existing.DisplayName = displayName;
            existing.UpdateSourceKind(sourceKind);
            // The trust store records how the device was originally paired.  Do not let
            // a trust-list refresh overwrite the transport of a live session (for example,
            // a phone first paired over Wi-Fi but currently streaming over Bluetooth).
            if (!existing.IsOnline)
            {
                existing.Transport = transport;
            }
            RebuildVisibleChannels();
            return existing;
        }

        var channel = new RemoteChannelItemViewModel(
            deviceId,
            displayName,
            transport,
            OnChannelSettingsChanged,
            DisconnectDeviceAsync,
            RevokeDeviceAsync,
            layoutChanged: OnChannelLayoutChanged,
            moveChannel: MoveChannel);
        channel.UpdateSourceKind(sourceKind);
        ApplyRememberedSettings(channel);
        ApplyRememberedLayout(channel);
        RemoteChannels.Add(channel);
        RebuildVisibleChannels();
        return channel;
    }

    private RemoteChannelItemViewModel EnsureApplicationChannel(
        Guid channelId,
        Guid deviceId,
        string deviceName,
        string sourceName,
        string? sourceKind,
        RemoteTransportMode transport)
    {
        RemoteChannelItemViewModel? existing =
            RemoteChannels.FirstOrDefault(item => item.ChannelId == channelId);
        if (existing is not null)
        {
            existing.DisplayName = sourceName;
            existing.ParentDeviceName = deviceName;
            existing.Transport = transport;
            return existing;
        }

        var channel = new RemoteChannelItemViewModel(
            channelId,
            sourceName,
            transport,
            OnChannelSettingsChanged,
            DisconnectDeviceAsync,
            RevokeDeviceAsync,
            deviceId,
            deviceName,
            isApplicationSource: true,
            sourceKind: sourceKind,
            channelGroupChanged: OnChannelGroupChanged,
            layoutChanged: OnChannelLayoutChanged,
            moveChannel: MoveChannel);
        channel.Apply(
            1f,
            false,
            channelGroup: SuggestChannelGroup(sourceName, sourceKind));
        if (!ApplyRememberedSettings(channel))
        {
            ApplyAutomaticRouting(channel);
        }
        ApplyRememberedLayout(channel);
        RemoteChannels.Add(channel);
        return channel;
    }

    private static string SuggestChannelGroup(string sourceName, string? sourceKind)
    {
        if (string.Equals(sourceKind, "system", StringComparison.OrdinalIgnoreCase))
        {
            return "系统";
        }

        string name = sourceName.ToLowerInvariant();
        if (new[] { "discord", "teams", "zoom", "wechat", "微信", "qq", "telegram", "语音" }
            .Any(name.Contains))
        {
            return "语音";
        }
        if (new[] { "game", "steam", "epic", "xbox", "游戏", "战", "原神" }
            .Any(name.Contains))
        {
            return "游戏";
        }
        if (new[] { "chrome", "edge", "firefox", "spotify", "music", "音乐", "video", "player", "bilibili", "哔哩" }
            .Any(name.Contains))
        {
            return "媒体";
        }
        return "未分组";
    }

    private bool ApplyRememberedSettings(RemoteChannelItemViewModel channel)
    {
        if (!rememberedChannelSettings.TryGetValue(
            channel.ChannelId,
            out ListenSphere.Configuration.ChannelSettings? settings))
        {
            return false;
        }

        channel.Apply(
            settings.Volume,
            settings.IsMuted,
            settings.EqualizerPreset,
            settings.EqualizerGains,
            settings.EqualizerEnabled,
            settings.ChannelGroup,
            settings.PreampDb,
            settings.NoiseGateEnabled,
            settings.NoiseGateThresholdDb,
            settings.CompressorEnabled,
            settings.CompressorThresholdDb,
            settings.CompressorRatio,
            settings.LimiterEnabled,
            settings.LimiterCeilingDb,
            settings.IsVoiceDuckingTrigger,
            settings.IsVoiceDuckingTarget,
            settings.VoiceDuckingReductionDb);
        channel.RoutingStatus = "已恢复此声道的场景设置";
        return true;
    }

    private void ApplyAutomaticRouting(RemoteChannelItemViewModel channel)
    {
        if (!AutomaticRoutingEnabled || !channel.IsApplicationSource)
        {
            channel.RoutingStatus = "使用默认分组";
            return;
        }

        ListenSphere.Configuration.AudioRoutingRuleSettings? rule =
            ListenSphere.Configuration.AudioRoutingRuleEvaluator.Match(
                RoutingRules.Select(item => item.ToSettings()),
                new ListenSphere.Configuration.AudioRoutingContext(
                    channel.DisplayName,
                    channel.SourceKind,
                    channel.Transport.ToString()));
        if (rule is null || !GroupBusNames.Contains(rule.TargetGroup, StringComparer.Ordinal))
        {
            channel.RoutingStatus = "已按来源类型自动识别";
            return;
        }

        channel.Apply(
            channel.VolumePercent / 100,
            channel.IsMuted,
            channel.SelectedEqualizerPreset.Name,
            channel.EqualizerGains,
            channel.IsEqualizerEnabled,
            rule.TargetGroup);
        channel.RoutingStatus = $"自动路由 · {rule.Name}";
    }

    private void OnChannelGroupChanged(RemoteChannelItemViewModel channel)
    {
        if (!channel.IsApplicationSource)
        {
            OnChannelSettingsChanged();
            return;
        }

        RoutingRuleItemViewModel? existing = RoutingRules.FirstOrDefault(item =>
        {
            ListenSphere.Configuration.AudioRoutingRuleSettings rule = item.ToSettings();
            return rule.MatchMode == ListenSphere.Configuration.AudioRouteMatchMode.Exact &&
                string.Equals(rule.SourcePattern, channel.DisplayName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(rule.SourceKind, channel.SourceKind, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(rule.Transport);
        });
        var learned = new ListenSphere.Configuration.AudioRoutingRuleSettings(
            existing?.RuleId ?? Guid.NewGuid(),
            $"{channel.DisplayName} → {channel.SelectedChannelGroup}",
            channel.DisplayName,
            channel.SelectedChannelGroup,
            ListenSphere.Configuration.AudioRouteMatchMode.Exact,
            channel.SourceKind,
            Priority: 1000);
        if (existing is null)
        {
            RoutingRules.Add(CreateRoutingRuleItem(learned));
        }
        else
        {
            existing.UpdateTargetGroup(channel.SelectedChannelGroup);
        }

        channel.RoutingStatus = "已记住此来源的分组";
        OnPropertyChanged(nameof(AutomaticRoutingStatus));
        OnChannelSettingsChanged();
    }

    private RoutingRuleItemViewModel CreateRoutingRuleItem(
        ListenSphere.Configuration.AudioRoutingRuleSettings rule) =>
        new(rule, OnRoutingRuleChanged, DeleteRoutingRule);

    private void OnRoutingRuleChanged()
    {
        OnPropertyChanged(nameof(AutomaticRoutingStatus));
        AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DeleteRoutingRule(Guid ruleId)
    {
        RoutingRuleItemViewModel? item = RoutingRules.FirstOrDefault(rule => rule.RuleId == ruleId);
        if (item is null)
        {
            return;
        }

        RoutingRules.Remove(item);
        OnRoutingRuleChanged();
    }

    private void ApplyRememberedLayout(RemoteChannelItemViewModel channel)
    {
        if (rememberedChannelLayouts.TryGetValue(
            channel.ChannelId,
            out ListenSphere.Configuration.ChannelLayoutSettings? layout))
        {
            channel.ApplyLayout(layout.IsPinned, layout.SortOrder);
            return;
        }

        int nextOrder = rememberedChannelLayouts.Count == 0
            ? 0
            : rememberedChannelLayouts.Values.Max(item => item.SortOrder) + 1;
        channel.ApplyLayout(false, nextOrder);
        rememberedChannelLayouts[channel.ChannelId] =
            new ListenSphere.Configuration.ChannelLayoutSettings(channel.ChannelId, false, nextOrder);
    }

    private void OnChannelLayoutChanged(RemoteChannelItemViewModel channel)
    {
        rememberedChannelLayouts[channel.ChannelId] =
            new ListenSphere.Configuration.ChannelLayoutSettings(
                channel.ChannelId,
                channel.IsPinned,
                channel.SortOrder);
        RebuildVisibleChannels();
        AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MoveChannel(RemoteChannelItemViewModel channel, int direction)
    {
        RemoteChannelItemViewModel[] ordered = RemoteChannels
            .Where(item => item.IsOnline && item.SessionId is Guid && item.IsPinned == channel.IsPinned)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        int index = Array.IndexOf(ordered, channel);
        if (index < 0 || ordered.Length < 2)
        {
            return;
        }
        int target = Math.Clamp(index + Math.Sign(direction), 0, ordered.Length - 1);
        if (target == index)
        {
            return;
        }

        int oldOrder = channel.SortOrder;
        channel.ApplyLayout(channel.IsPinned, ordered[target].SortOrder);
        ordered[target].ApplyLayout(ordered[target].IsPinned, oldOrder);
        OnChannelLayoutChanged(channel);
        OnChannelLayoutChanged(ordered[target]);
    }

    private static RemoteTransportMode ParseTransport(string transport) =>
        Enum.TryParse(transport, true, out RemoteTransportMode parsed)
            ? parsed
            : RemoteTransportMode.Wireless;

    private void RebuildVisibleChannels()
    {
        VisibleRemoteChannels.Clear();
        foreach (RemoteChannelItemViewModel channel in RemoteChannels.Where(
                     channel => !channel.IsApplicationSource &&
                         channel.SupportsTransport(SelectedTransport)))
        {
            channel.SetListTransport(SelectedTransport);
            VisibleRemoteChannels.Add(channel);
        }

        ActiveRemoteChannels.Clear();
        foreach (RemoteChannelItemViewModel channel in RemoteChannels.Where(
                     channel => channel.IsOnline && channel.SessionId is Guid)
                     .OrderByDescending(channel => channel.IsPinned)
                     .ThenBy(channel => channel.SortOrder)
                     .ThenBy(channel => channel.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            ActiveRemoteChannels.Add(channel);
        }

        RebuildActiveGroupBuses();
    }

    private void OnChannelSettingsChanged()
    {
        RebuildActiveGroupBuses();
        AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnGroupBusSettingsChanged(GroupBusItemViewModel group)
    {
        UpdateGroupMixerBus(group);
        AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateGroupMixerBus(GroupBusItemViewModel group) =>
        groupMixerCoordinator.UpdateBus(new GroupMixerBusSettings(
            group.Name,
            group.EffectiveGain,
            group.EqualizerGains));

    private void RebuildActiveGroupBuses()
    {
        groupMixerCoordinator.UpdateActiveGroups(
            ActiveRemoteChannels.Select(channel => channel.SelectedChannelGroup));
    }

    private void OnGroupMixerSnapshotChanged(
        object? sender,
        GroupMixerSnapshot snapshot)
    {
        if (Application.Current.Dispatcher.CheckAccess())
        {
            ApplyGroupMixerSnapshot(snapshot);
            return;
        }
        _ = Application.Current.Dispatcher.BeginInvoke(
            () => ApplyGroupMixerSnapshot(snapshot));
    }

    private void ApplyGroupMixerSnapshot(GroupMixerSnapshot snapshot)
    {
        Dictionary<string, int> counts = snapshot.Buses.ToDictionary(
            bus => bus.Name,
            bus => bus.ChannelCount,
            StringComparer.Ordinal);
        foreach (GroupBusItemViewModel group in GroupBuses)
        {
            group.ChannelCount = counts.TryGetValue(group.Name, out int count) ? count : 0;
        }

        ActiveGroupBuses.Clear();
        foreach (GroupBusItemViewModel group in GroupBuses.Where(group => group.ChannelCount > 0))
        {
            ActiveGroupBuses.Add(group);
        }
    }

    private async Task DisconnectDeviceAsync(Guid deviceId)
    {
        RemoteChannelItemViewModel? channel =
            RemoteChannels.FirstOrDefault(item =>
                item.ParentDeviceId == deviceId && !item.IsApplicationSource);
        await remoteDeviceCoordinator.DisconnectAsync(deviceId, CancellationToken.None);
        foreach (RemoteChannelItemViewModel affected in RemoteChannels.Where(
                     item => item.ParentDeviceId == deviceId).ToArray())
        {
            if (affected.SessionId is Guid sessionId)
            {
                RemoveRemoteAudioSession(sessionId);
            }
            affected.ConnectionState = DeviceConnectionState.Offline;
            affected.SessionId = null;
            affected.PeakPercent = 0;
        }
        PruneOfflineApplicationChannels(deviceId);
        RebuildVisibleChannels();
        NetworkStatus = channel is null
            ? "已断开发送端。"
            : $"已断开 {channel.DisplayName}；设备信任仍保留，可重新连接。";
    }

    private void OnPeerChanged(object? sender, ControlPeerEvent args)
    {
        diagnostics.Record(
            args.State == DeviceConnectionState.Faulted
                ? DiagnosticSeverity.Warning
                : DiagnosticSeverity.Information,
            "network.peer.state",
            new Dictionary<string, object?> { ["state"] = args.State.ToString() });
        _ = Application.Current.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                NetworkStatus = args.Device is null
                    ? args.Message
                    : $"{args.Device.DisplayName} · {args.State} · {args.Message}";
                if (args.Device is null)
                {
                    return;
                }

                bool isApplicationStream = args.ChannelId is Guid;
                RemoteChannelItemViewModel? deviceChannel = RemoteChannels.FirstOrDefault(
                    item => item.ParentDeviceId == args.Device.DeviceId &&
                        !item.IsApplicationSource);
                RemoteChannelItemViewModel? channel = isApplicationStream
                    ? RemoteChannels.FirstOrDefault(item => item.ChannelId == args.ChannelId)
                    : deviceChannel;
                if (channel is null && args.State is
                    DeviceConnectionState.Offline or DeviceConnectionState.Faulted)
                {
                    return;
                }

                RemoteTransportMode eventTransport = ParseTransport(args.Transport);
                deviceChannel ??= EnsureRemoteChannel(
                    args.Device.DeviceId, args.Device.DisplayName, eventTransport);
                if (isApplicationStream)
                {
                    // The legacy/default stream remains available for older Senders, but
                    // once named application streams exist it is only a device placeholder.
                    if (deviceChannel.SessionId is Guid legacySession)
                    {
                        RemoveRemoteAudioSession(legacySession);
                        deviceChannel.SessionId = null;
                    }
                    deviceChannel.ConnectionState = DeviceConnectionState.Connected;
                    channel ??= EnsureApplicationChannel(
                        args.ChannelId!.Value,
                        args.Device.DeviceId,
                        args.Device.DisplayName,
                        args.SourceName ?? "应用声音",
                        args.SourceKind,
                        eventTransport);
                }
                else
                {
                    channel ??= deviceChannel;
                    channel.UpdateSourceKind(args.SourceKind);
                }
                if (args.State is DeviceConnectionState.Connected or DeviceConnectionState.Streaming)
                {
                    channel.Transport = eventTransport;
                }
                else if (channel.IsOnline && channel.Transport != eventTransport)
                {
                    // An old Wi-Fi session may report Offline after the same device has
                    // already switched to Bluetooth.  Keep the active transport visible.
                    return;
                }
                channel.ConnectionState = args.State;
                if (args.State == DeviceConnectionState.Streaming && args.AudioSessionId is Guid sessionId)
                {
                    channel.SessionId = sessionId;
                    RegisterRemoteAudioSession(sessionId, channel);
                }
                else if (args.State == DeviceConnectionState.Offline)
                {
                    if (args.AudioSessionId is Guid endedSession)
                    {
                        RemoveRemoteAudioSession(endedSession);
                    }

                    channel.SessionId = null;
                    channel.PeakPercent = 0;
                    await microphoneHubCoordinator.RemoveSourceAsync(channel.ChannelId);
                    if (channel.IsApplicationSource)
                    {
                        RemoveApplicationChannel(channel);
                    }
                }

                if (!isApplicationStream && args.State is
                    DeviceConnectionState.Offline or DeviceConnectionState.Faulted)
                {
                    foreach (RemoteChannelItemViewModel child in RemoteChannels.Where(
                                 item => item.ParentDeviceId == args.Device.DeviceId &&
                                     item.IsApplicationSource).ToArray())
                    {
                        if (child.SessionId is Guid childSession)
                        {
                            RemoveRemoteAudioSession(childSession);
                        }
                        child.SessionId = null;
                        child.PeakPercent = 0;
                        child.ConnectionState = args.State;
                    }
                    PruneOfflineApplicationChannels(args.Device.DeviceId);
                }

                RebuildVisibleChannels();

                if (args.State is DeviceConnectionState.Connected or DeviceConnectionState.Streaming)
                {
                    remoteDeviceCoordinator.CompletePairingCodeIfConsumed(
                        "设备已建立证书固定信任；后续将自动重连。");
                    await remoteDeviceCoordinator.RefreshTrustedDevicesAsync();
                }
            }
            catch (Exception exception)
            {
                Log.Error(exception, "Failed to apply Controller peer state");
                NetworkStatus = $"更新设备状态失败：{exception.Message}";
            }
        });
    }

    private void PruneOfflineApplicationChannels(Guid deviceId)
    {
        foreach (RemoteChannelItemViewModel channel in RemoteChannels.Where(item =>
                     item.ParentDeviceId == deviceId &&
                     item.IsApplicationSource &&
                     !item.IsOnline).ToArray())
        {
            RemoveApplicationChannel(channel);
        }
    }

    private void RegisterRemoteAudioSession(
        Guid sessionId,
        RemoteChannelItemViewModel channel,
        int preferredStartupFrames = 6)
    {
        channelsBySession[sessionId] = channel;
        remoteAudioCoordinator.RegisterSession(sessionId, preferredStartupFrames);
    }

    private void RemoveRemoteAudioSession(Guid sessionId)
    {
        channelsBySession.TryRemove(sessionId, out _);
        remoteAudioCoordinator.RemoveSession(sessionId);
        groupMixerCoordinator.RemoveSession(sessionId);
    }

    private void RemoveApplicationChannel(RemoteChannelItemViewModel channel)
    {
        if (channel.SessionId is Guid sessionId)
        {
            RemoveRemoteAudioSession(sessionId);
        }
        RemoteChannels.Remove(channel);
        ActiveRemoteChannels.Remove(channel);
        VisibleRemoteChannels.Remove(channel);
    }

    private void OnBluetoothProbeReceived(object? sender, BluetoothProbeEvent args)
    {
        _ = Application.Current.Dispatcher.BeginInvoke(() =>
        {
            BluetoothStatus = $"已验证：{args.RemoteName}";
            NetworkStatus = $"蓝牙设备 {args.RemoteName} 已完成 RFCOMM 握手。";
        });
    }

    private void OnBluetoothSessionChanged(object? sender, BluetoothSessionEvent args)
    {
        _ = Application.Current.Dispatcher.BeginInvoke(async () =>
        {
            BluetoothStatus = args.State == DeviceConnectionState.Streaming
                ? $"正在接收：{args.Device.DisplayName}"
                : "RFCOMM 音频服务已开启";
            RemoteChannelItemViewModel? channel = RemoteChannels.FirstOrDefault(
                item => item.ChannelId == args.Device.DeviceId);
            if (channel is null && args.State == DeviceConnectionState.Offline)
            {
                return;
            }

            channel ??= EnsureRemoteChannel(
                args.Device.DeviceId,
                args.Device.DisplayName,
                RemoteTransportMode.Bluetooth,
                args.SourceKind);
            channel.UpdateSourceKind(args.SourceKind);
            channel.Transport = RemoteTransportMode.Bluetooth;
            RebuildVisibleChannels();
            channel.ConnectionState = args.State;
            if (args.State == DeviceConnectionState.Streaming)
            {
                channel.SessionId = args.SessionId;
                RegisterRemoteAudioSession(args.SessionId, channel, preferredStartupFrames: 18);
                remoteDeviceCoordinator.CompletePairingCodeIfConsumed(
                    "蓝牙设备已建立身份信任；后续可直接重连。");
                await remoteDeviceCoordinator.RefreshTrustedDevicesAsync();
            }
            else
            {
                RemoveRemoteAudioSession(args.SessionId);
                channel.SessionId = null;
                channel.PeakPercent = 0;
                await microphoneHubCoordinator.RemoveSourceAsync(channel.ChannelId);
            }
            RebuildVisibleChannels();
        });
    }

    private void OnBluetoothHostFaulted(object? sender, BluetoothHostFaultEvent args)
    {
        Log.Error(
            "Bluetooth RFCOMM session failed for {RemoteName}: {ExceptionType} {Message}",
            args.RemoteName,
            args.ExceptionType,
            args.Message);
        diagnostics.Record(
            DiagnosticSeverity.Error,
            "bluetooth.session.failed",
            new Dictionary<string, object?>
            {
                ["remoteName"] = args.RemoteName,
                ["exceptionType"] = args.ExceptionType,
                ["message"] = args.Message
            });
        _ = Application.Current.Dispatcher.BeginInvoke(() =>
        {
            BluetoothStatus = $"会话失败：{args.ExceptionType}";
            NetworkStatus = $"蓝牙会话失败：{args.Message}";
        });
    }

    private void OnUsbStatusChanged(object? sender, UsbAccessoryStatusEvent args)
    {
        _ = Application.Current.Dispatcher.BeginInvoke(() => UsbStatus = args.Message);
    }

    private void OnUsbSessionChanged(object? sender, UsbAccessorySessionEvent args)
    {
        _ = Application.Current.Dispatcher.BeginInvoke(async () =>
        {
            UsbStatus = args.State == DeviceConnectionState.Streaming
                ? $"正在接收：{args.Device.DisplayName}"
                : "正在等待原生 USB 设备";
            RemoteChannelItemViewModel? channel = RemoteChannels.FirstOrDefault(
                item => item.ChannelId == args.Device.DeviceId);
            if (channel is null && args.State == DeviceConnectionState.Offline) return;
            channel ??= EnsureRemoteChannel(
                args.Device.DeviceId, args.Device.DisplayName, RemoteTransportMode.Wired,
                args.SourceKind);
            channel.UpdateSourceKind(args.SourceKind);
            channel.Transport = RemoteTransportMode.Wired;
            channel.ConnectionState = args.State;
            if (args.State == DeviceConnectionState.Streaming)
            {
                channel.SessionId = args.SessionId;
                RegisterRemoteAudioSession(args.SessionId, channel, preferredStartupFrames: 6);
                remoteDeviceCoordinator.CompletePairingCodeIfConsumed(
                    "有线设备已建立身份信任；后续插线可直接重连。");
                await remoteDeviceCoordinator.RefreshTrustedDevicesAsync();
            }
            else
            {
                RemoveRemoteAudioSession(args.SessionId);
                channel.SessionId = null;
                channel.PeakPercent = 0;
                await microphoneHubCoordinator.RemoveSourceAsync(channel.ChannelId);
            }
            RebuildVisibleChannels();
        });
    }

    private void OnUsbHostFaulted(object? sender, UsbAccessoryFaultEvent args)
    {
        Log.Warning(
            "Native USB session failed: {ExceptionType} {Message}",
            args.ExceptionType,
            args.Message);
        diagnostics.Record(
            DiagnosticSeverity.Warning,
            "usb.session.failed",
            new Dictionary<string, object?>
            {
                ["exceptionType"] = args.ExceptionType,
                ["message"] = args.Message
            });
        _ = Application.Current.Dispatcher.BeginInvoke(() =>
        {
            UsbStatus = $"USB 会话失败：{args.Message}";
            NetworkStatus = "原生 USB 暂不可用，可在手机端启用 USB 网络兼容模式。";
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        server.PeerChanged -= OnPeerChanged;
        bluetoothHost.ProbeReceived -= OnBluetoothProbeReceived;
        bluetoothHost.SessionChanged -= OnBluetoothSessionChanged;
        bluetoothHost.Faulted -= OnBluetoothHostFaulted;
        usbHost.StatusChanged -= OnUsbStatusChanged;
        usbHost.SessionChanged -= OnUsbSessionChanged;
        usbHost.Faulted -= OnUsbHostFaulted;
        transportCoordinator.SnapshotChanged -= OnTransportSnapshotChanged;
        remoteDeviceCoordinator.SnapshotChanged -= OnRemoteDeviceSnapshotChanged;
        audioOutputCoordinator.SnapshotChanged -= OnAudioOutputSnapshotChanged;
        audioOutputCoordinator.OutputEvent -= OnAudioOutputEvent;
        localAudioRoutingCoordinator.SnapshotChanged -= OnLocalAudioRoutingSnapshotChanged;
        localAudioRoutingCoordinator.RoutesChanged -= OnLocalAudioRoutesChanged;
        microphoneHubCoordinator.SnapshotChanged -= OnMicrophoneHubSnapshotChanged;
        microphoneHubCoordinator.SettingsChanged -= OnMicrophoneHubSettingsChanged;
        remoteAudioCoordinator.SnapshotChanged -= OnRemoteAudioSnapshotChanged;
        remoteAudioCoordinator.MeterChanged -= OnRemoteAudioMeterChanged;
        groupMixerCoordinator.SnapshotChanged -= OnGroupMixerSnapshotChanged;
        await remoteAudioCoordinator.DisposeAsync();
        await groupMixerCoordinator.DisposeAsync();
        await audioOutputCoordinator.DisposeAsync();
        await remoteDeviceCoordinator.DisposeAsync();
        await transportCoordinator.DisposeAsync();
        await server.DisposeAsync();
        await localAudioRoutingCoordinator.DisposeAsync();
        await microphoneHubCoordinator.DisposeAsync();
        AdditionalOutputs.Clear();

        await bluetoothHost.DisposeAsync();
        await usbHost.DisposeAsync();
    }

    public sealed record ApplicationOutputRouteInfo(
        string DeviceId,
        string DeviceName,
        bool IsActive);

    private bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class AdditionalOutputDeviceItemViewModel : INotifyPropertyChanged
{
    private string summary = "拖动音源卡片到这里";
    private readonly Action<string, float> volumeChanged;
    private readonly Action<string, bool> muteChanged;
    private float volumePercent;
    private bool isMuted;

    public AdditionalOutputDeviceItemViewModel(
        string deviceId,
        string displayName,
        float volumePercent,
        bool isMuted,
        Action<string, float> volumeChanged,
        Action<string, bool> muteChanged)
    {
        DeviceId = deviceId;
        DisplayName = displayName;
        this.volumePercent = Math.Clamp(volumePercent, 0, 100);
        this.isMuted = isMuted;
        this.volumeChanged = volumeChanged;
        this.muteChanged = muteChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string DeviceId { get; }
    public string DisplayName { get; }
    public ObservableCollection<AdditionalOutputRouteItemViewModel> Routes { get; } = [];
    public float VolumePercent
    {
        get => volumePercent;
        set
        {
            float normalized = Math.Clamp(value, 0, 100);
            if (Math.Abs(volumePercent - normalized) < 0.01f) return;
            volumePercent = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VolumePercent)));
            volumeChanged(DeviceId, normalized / 100);
        }
    }

    public bool IsMuted
    {
        get => isMuted;
        set
        {
            if (isMuted == value) return;
            isMuted = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMuted)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MuteButtonText)));
            muteChanged(DeviceId, value);
        }
    }

    public string MuteButtonText => IsMuted ? "取消此输出设备静音" : "静音此输出设备";
    public string Summary
    {
        get => summary;
        private set
        {
            if (string.Equals(summary, value, StringComparison.Ordinal)) return;
            summary = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
        }
    }

    public void RefreshSummary() => Summary = Routes.Count == 0
        ? "拖动音源卡片到这里"
        : $"已接收 {Routes.Count} 个音源";
}

public sealed class AdditionalOutputRouteItemViewModel
{
    public AdditionalOutputRouteItemViewModel(
        Guid channelId,
        string deviceId,
        string sourceName,
        bool isActive,
        Func<Guid, string, Task> remove)
    {
        ChannelId = channelId;
        DeviceId = deviceId;
        SourceName = sourceName;
        IsActive = isActive;
        RemoveCommand = new AsyncRelayCommand(() => remove(ChannelId, DeviceId));
    }

    public Guid ChannelId { get; }
    public string DeviceId { get; }
    public string SourceName { get; }
    public bool IsActive { get; }
    public string StatusText => IsActive ? "正在输出" : "等待设备恢复";
    public AsyncRelayCommand RemoveCommand { get; }
}

public sealed class RemoteChannelItemViewModel : INotifyPropertyChanged
{
    private static readonly EqualizerPresetOption CustomPreset =
        new("自定义", null);
    private static readonly IReadOnlyList<EqualizerPresetOption> BuiltInPresets =
    [
        new("原声", [0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
        new("语音清晰", [-6, -4, -2, -1, 0, 2, 4, 3, 1, -1]),
        new("游戏脚步", [-6, -4, -2, 0, 2, 4, 5, 3, 1, -2]),
        new("音乐均衡", [1, 2, 1, 0, -1, 0, 1, 2, 2, 1]),
        new("低音增强", [5, 4, 3, 2, 0, -1, -1, 0, 1, 1]),
        new("柔和聆听", [-2, -1, 0, 1, 2, 3, 2, 0, -1, -2])
    ];
    private static readonly IReadOnlyList<EqualizerPresetOption> AllPresets =
        [.. BuiltInPresets, CustomPreset];
    private static readonly IReadOnlyList<string> ChannelGroups =
        ["未分组", "游戏", "语音", "媒体", "系统", "自定义"];
    private readonly Action settingsChanged;
    private readonly Action<RemoteChannelItemViewModel>? channelGroupChanged;
    private readonly Action<RemoteChannelItemViewModel>? layoutChanged;
    private string displayName;
    private string parentDeviceName;
    private DeviceConnectionState connectionState;
    private float volumePercent = 100;
    private bool isMuted;
    private float peakPercent;
    private bool applying;
    private RemoteTransportMode transport;
    private RemoteTransportMode listTransport;
    private readonly HashSet<RemoteTransportMode> observedTransports = [];
    private bool isEditorOpen;
    private bool isEqualizerEnabled = true;
    private EqualizerPresetOption selectedEqualizerPreset = BuiltInPresets[0];
    private string selectedChannelGroup = ChannelGroups[0];
    private string routingStatus = "使用默认分组";
    private string networkQualityText = string.Empty;
    private string networkQualityColor = "#8C98AA";
    private string networkQualityToolTip = "拖动卡片可将此音源同时输出到其他播放设备";
    private readonly Queue<ChannelQualitySample> qualityTrend = [];
    private long previousQualityFrames;
    private long previousQualityConcealments;
    private long previousQualityLostDatagrams;
    private long previousBluetoothDecodedFrames;
    private long previousBluetoothTimestampGaps;
    private long previousBluetoothQueueDrops;
    private long previousBluetoothDecodeFailures;
    private long clippedFramesSinceQualityUpdate;
    private int networkQualityLevel;
    private bool hasQualityBaseline;
    private bool isPinned;
    private int sortOrder;
    private readonly float[] equalizerGains = new float[10];
    private float preampDb;
    private bool noiseGateEnabled;
    private float noiseGateThresholdDb = -48;
    private bool compressorEnabled;
    private float compressorThresholdDb = -18;
    private float compressorRatio = 4;
    private bool limiterEnabled = true;
    private float limiterCeilingDb = -1;
    private bool isVoiceDuckingTrigger;
    private bool isVoiceDuckingTarget;
    private float voiceDuckingReductionDb = 12;
    private string? sourceKind;
    private string dynamicsStatusText = "动态处理待机";
    private string dynamicsStatusColor = "#8C98AA";

    public RemoteChannelItemViewModel(
        Guid channelId,
        string displayName,
        RemoteTransportMode transport,
        Action settingsChanged,
        Func<Guid, Task> disconnect,
        Func<Guid, Task> revoke,
        Guid? parentDeviceId = null,
        string? parentDeviceName = null,
        bool isApplicationSource = false,
        string? sourceKind = null,
        Action<RemoteChannelItemViewModel>? channelGroupChanged = null,
        Action<RemoteChannelItemViewModel>? layoutChanged = null,
        Action<RemoteChannelItemViewModel, int>? moveChannel = null)
    {
        ChannelId = channelId;
        ParentDeviceId = parentDeviceId ?? channelId;
        this.parentDeviceName = parentDeviceName ?? displayName;
        IsApplicationSource = isApplicationSource;
        this.sourceKind = sourceKind;
        this.displayName = displayName;
        this.transport = transport;
        listTransport = transport;
        observedTransports.Add(transport);
        this.settingsChanged = settingsChanged;
        this.channelGroupChanged = channelGroupChanged;
        this.layoutChanged = layoutChanged;
        DisconnectCommand = new AsyncRelayCommand(
            () => disconnect(ParentDeviceId),
            () => IsOnline);
        DeleteCommand = new AsyncRelayCommand(() => revoke(ParentDeviceId));
        ResetEqualizerCommand = new AsyncRelayCommand(ResetEqualizerAsync);
        MoveUpCommand = new AsyncRelayCommand(() =>
        {
            moveChannel?.Invoke(this, -1);
            return Task.CompletedTask;
        });
        MoveDownCommand = new AsyncRelayCommand(() =>
        {
            moveChannel?.Invoke(this, 1);
            return Task.CompletedTask;
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid ChannelId { get; }
    public Guid ParentDeviceId { get; }
    public bool IsApplicationSource { get; }
    public string? SourceKind => sourceKind;
    public bool IsMicrophoneSource =>
        string.Equals(SourceKind, "microphone", StringComparison.OrdinalIgnoreCase);
    public bool IsPinned
    {
        get => isPinned;
        set
        {
            if (SetField(ref isPinned, value) && !applying)
            {
                layoutChanged?.Invoke(this);
            }
        }
    }
    public int SortOrder => sortOrder;
    public string RoutingStatus
    {
        get => routingStatus;
        set => SetField(ref routingStatus, value);
    }
    public string NetworkQualityText
    {
        get => networkQualityText;
        private set => SetField(ref networkQualityText, value);
    }
    public string NetworkQualityColor
    {
        get => networkQualityColor;
        private set => SetField(ref networkQualityColor, value);
    }
    public string NetworkQualityToolTip
    {
        get => networkQualityToolTip;
        private set => SetField(ref networkQualityToolTip, value);
    }
    public string ParentDeviceName
    {
        get => parentDeviceName;
        set
        {
            if (SetField(ref parentDeviceName, value))
            {
                OnPropertyChanged(nameof(SourceSubtitle));
            }
        }
    }
    public Guid? SessionId { get; set; }
    public bool IsEditorOpen
    {
        get => isEditorOpen;
        set => SetField(ref isEditorOpen, value);
    }
    public IReadOnlyList<float> EqualizerGains => equalizerGains;
    public IReadOnlyList<float> EqualizerCurveGains => equalizerGains.ToArray();
    public IReadOnlyList<EqualizerPresetOption> EqualizerPresets => AllPresets;
    public IReadOnlyList<string> AvailableChannelGroups => ChannelGroups;
    public string SelectedChannelGroup
    {
        get => selectedChannelGroup;
        set
        {
            string normalized = ChannelGroups.Contains(value, StringComparer.Ordinal)
                ? value
                : ChannelGroups[0];
            if (SetField(ref selectedChannelGroup, normalized))
            {
                OnPropertyChanged(nameof(SourceGlyph));
                if (!applying)
                {
                    if (channelGroupChanged is null)
                    {
                        settingsChanged();
                    }
                    else
                    {
                        channelGroupChanged(this);
                    }
                }
            }
        }
    }
    public EqualizerPresetOption SelectedEqualizerPreset
    {
        get => selectedEqualizerPreset;
        set
        {
            if (value is null || ReferenceEquals(selectedEqualizerPreset, value))
            {
                return;
            }

            ApplyEqualizerPreset(value);
        }
    }
    public bool IsEqualizerEnabled
    {
        get => isEqualizerEnabled;
        set
        {
            if (SetField(ref isEqualizerEnabled, value) && !applying)
            {
                settingsChanged();
            }
        }
    }
    public float Eq20Db { get => equalizerGains[0]; set => SetEqualizerBand(0, value); }
    public float Eq60Db { get => equalizerGains[1]; set => SetEqualizerBand(1, value); }
    public float Eq100Db { get => equalizerGains[2]; set => SetEqualizerBand(2, value); }
    public float Eq250Db { get => equalizerGains[3]; set => SetEqualizerBand(3, value); }
    public float Eq500Db { get => equalizerGains[4]; set => SetEqualizerBand(4, value); }
    public float Eq1KDb { get => equalizerGains[5]; set => SetEqualizerBand(5, value); }
    public float Eq2KDb { get => equalizerGains[6]; set => SetEqualizerBand(6, value); }
    public float Eq4KDb { get => equalizerGains[7]; set => SetEqualizerBand(7, value); }
    public float Eq8KDb { get => equalizerGains[8]; set => SetEqualizerBand(8, value); }
    public float Eq20KDb { get => equalizerGains[9]; set => SetEqualizerBand(9, value); }
    public float PreampDb
    {
        get => preampDb;
        set => SetProcessingField(ref preampDb, Math.Clamp(value, -24f, 12f));
    }
    public bool NoiseGateEnabled
    {
        get => noiseGateEnabled;
        set => SetProcessingField(ref noiseGateEnabled, value);
    }
    public float NoiseGateThresholdDb
    {
        get => noiseGateThresholdDb;
        set => SetProcessingField(ref noiseGateThresholdDb, Math.Clamp(value, -80f, -10f));
    }
    public bool CompressorEnabled
    {
        get => compressorEnabled;
        set => SetProcessingField(ref compressorEnabled, value);
    }
    public float CompressorThresholdDb
    {
        get => compressorThresholdDb;
        set => SetProcessingField(ref compressorThresholdDb, Math.Clamp(value, -40f, 0f));
    }
    public float CompressorRatio
    {
        get => compressorRatio;
        set => SetProcessingField(ref compressorRatio, Math.Clamp(value, 1f, 20f));
    }
    public bool LimiterEnabled
    {
        get => limiterEnabled;
        set => SetProcessingField(ref limiterEnabled, value);
    }
    public float LimiterCeilingDb
    {
        get => limiterCeilingDb;
        set => SetProcessingField(ref limiterCeilingDb, Math.Clamp(value, -12f, -0.1f));
    }
    public bool IsVoiceDuckingTrigger
    {
        get => isVoiceDuckingTrigger;
        set => SetProcessingField(ref isVoiceDuckingTrigger, value);
    }
    public bool IsVoiceDuckingTarget
    {
        get => isVoiceDuckingTarget;
        set => SetProcessingField(ref isVoiceDuckingTarget, value);
    }
    public float VoiceDuckingReductionDb
    {
        get => voiceDuckingReductionDb;
        set => SetProcessingField(ref voiceDuckingReductionDb, Math.Clamp(value, 0f, 30f));
    }
    public string DynamicsStatusText
    {
        get => dynamicsStatusText;
        private set => SetField(ref dynamicsStatusText, value);
    }
    public string DynamicsStatusColor
    {
        get => dynamicsStatusColor;
        private set => SetField(ref dynamicsStatusColor, value);
    }
    public ChannelDynamicsSettings DynamicsSettings => new(
        PreampDb,
        NoiseGateEnabled,
        NoiseGateThresholdDb,
        CompressorEnabled,
        CompressorThresholdDb,
        CompressorRatio,
        LimiterEnabled,
        LimiterCeilingDb);
    public RemoteTransportMode Transport
    {
        get => transport;
        set
        {
            if (SetField(ref transport, value))
            {
                observedTransports.Add(value);
                OnPropertyChanged(nameof(TransportLabel));
                OnPropertyChanged(nameof(SourceSubtitle));
            }
        }
    }

    public void RememberTransport(RemoteTransportMode value) =>
        observedTransports.Add(value);

    public bool SupportsTransport(RemoteTransportMode value) =>
        observedTransports.Contains(value);

    public void SetListTransport(RemoteTransportMode value)
    {
        SetField(ref listTransport, value, nameof(ListTransportLabel));
    }

    public string ListTransportLabel => listTransport switch
    {
        RemoteTransportMode.Bluetooth => "蓝牙设备",
        RemoteTransportMode.Wired => "有线设备",
        _ => "无线网络设备"
    };

    public string TransportLabel => Transport switch
    {
        RemoteTransportMode.Bluetooth => "蓝牙设备",
        RemoteTransportMode.Wired => "有线设备",
        _ => "无线网络设备"
    };

    public string SourceSubtitle => IsMicrophoneSource
        ? $"{TransportLabel} · 手机麦克风"
        : IsApplicationSource
        ? $"{ParentDeviceName} · {TransportLabel}"
        : TransportLabel;

    public void UpdateSourceKind(string? value)
    {
        string? normalized = string.IsNullOrWhiteSpace(value)
            ? SourceKind
            : value.Trim().ToLowerInvariant();
        if (!SetField(ref sourceKind, normalized, nameof(SourceKind)))
        {
            return;
        }
        OnPropertyChanged(nameof(IsMicrophoneSource));
        OnPropertyChanged(nameof(SourceSubtitle));
    }

    public string SourceGlyph => SelectedChannelGroup switch
    {
        "游戏" => "\uE7FC",
        "语音" => "\uE720",
        "媒体" => "\uE8D6",
        "系统" => "\uE770",
        _ => "\uE71D"
    };

    private void SetEqualizerBand(int index, float value)
    {
        float normalized = Math.Clamp(value, -20f, 20f);
        if (equalizerGains[index] == normalized) return;
        equalizerGains[index] = normalized;
        OnPropertyChanged(index switch
        {
            0 => nameof(Eq20Db), 1 => nameof(Eq60Db), 2 => nameof(Eq100Db),
            3 => nameof(Eq250Db), 4 => nameof(Eq500Db), 5 => nameof(Eq1KDb),
            6 => nameof(Eq2KDb), 7 => nameof(Eq4KDb), 8 => nameof(Eq8KDb),
            _ => nameof(Eq20KDb)
        });
        OnPropertyChanged(nameof(EqualizerCurveGains));
        if (!applying)
        {
            selectedEqualizerPreset = CustomPreset;
            OnPropertyChanged(nameof(SelectedEqualizerPreset));
            settingsChanged();
        }
    }
    public AsyncRelayCommand DisconnectCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand ResetEqualizerCommand { get; }
    public AsyncRelayCommand MoveUpCommand { get; }
    public AsyncRelayCommand MoveDownCommand { get; }

    public void ApplyLayout(bool pinned, int order)
    {
        applying = true;
        try
        {
            IsPinned = pinned;
            SetField(ref sortOrder, Math.Max(0, order), nameof(SortOrder));
        }
        finally
        {
            applying = false;
        }
    }

    private Task ResetEqualizerAsync()
    {
        ApplyEqualizerPreset(BuiltInPresets[0]);
        return Task.CompletedTask;
    }

    private void ApplyEqualizerPreset(EqualizerPresetOption preset)
    {
        if (preset.Gains is not { Length: 10 })
        {
            selectedEqualizerPreset = CustomPreset;
            OnPropertyChanged(nameof(SelectedEqualizerPreset));
            settingsChanged();
            return;
        }

        applying = true;
        try
        {
            for (int index = 0; index < equalizerGains.Length; index++)
            {
                SetEqualizerBand(index, preset.Gains[index]);
            }
            selectedEqualizerPreset = preset;
            OnPropertyChanged(nameof(SelectedEqualizerPreset));
        }
        finally
        {
            applying = false;
        }

        settingsChanged();
    }

    public string DisplayName
    {
        get => displayName;
        set => SetField(ref displayName, value);
    }

    public DeviceConnectionState ConnectionState
    {
        get => connectionState;
        set
        {
            if (SetField(ref connectionState, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsOnline));
                OnPropertyChanged(nameof(AudioGlowLevel));
                DisconnectCommand.RaiseCanExecuteChanged();
                if (!IsOnline)
                {
                    NetworkQualityText = string.Empty;
                    NetworkQualityToolTip = "拖动卡片可将此音源同时输出到其他播放设备";
                    qualityTrend.Clear();
                    networkQualityLevel = 0;
                    hasQualityBaseline = false;
                }
            }
        }
    }

    public string StatusText => ConnectionState switch
    {
        DeviceConnectionState.Streaming => "正在传输",
        DeviceConnectionState.Connected => "已连接",
        DeviceConnectionState.Faulted => "连接异常",
        _ => "离线"
    };

    public bool IsOnline => ConnectionState is
        DeviceConnectionState.Connected or DeviceConnectionState.Streaming;

    public float VolumePercent
    {
        get => volumePercent;
        set
        {
            if (SetField(ref volumePercent, Math.Clamp(value, 0, 100)) && !applying)
            {
                settingsChanged();
            }
        }
    }

    public bool IsMuted
    {
        get => isMuted;
        set
        {
            if (SetField(ref isMuted, value))
            {
                OnPropertyChanged(nameof(MuteButtonText));
                OnPropertyChanged(nameof(AudioGlowLevel));
                if (!applying)
                {
                    settingsChanged();
                }
            }
        }
    }

    public string MuteButtonText => IsMuted ? "取消静音" : "静音";

    public float PeakPercent
    {
        get => peakPercent;
        set
        {
            if (SetField(ref peakPercent, Math.Clamp(value, 0, 100)))
            {
                OnPropertyChanged(nameof(AudioGlowLevel));
            }
        }
    }

    public float AudioGlowLevel => IsOnline && !IsMuted ? PeakPercent : 0;

    public float EffectiveGain => IsMuted ? 0f : VolumePercent / 100;

    public void ObservePostProcessingPeak(float peak)
    {
        if (peak > 1f)
        {
            Interlocked.Increment(ref clippedFramesSinceQualityUpdate);
        }
    }

    public void ObserveLimiterActivity(bool limited)
    {
        if (limited)
        {
            Interlocked.Increment(ref clippedFramesSinceQualityUpdate);
        }
    }

    public void UpdateDynamicsStatus(ChannelDynamicsResult result, bool duckingActive)
    {
        var states = new List<string>(4);
        if (result.GateClosed) states.Add("门限关闭");
        if (result.GainReductionDb >= 0.5f) states.Add($"压缩 -{result.GainReductionDb:F1} dB");
        if (duckingActive) states.Add($"闪避 -{VoiceDuckingReductionDb:F0} dB");
        if (result.Limited) states.Add("限幅保护");
        DynamicsStatusText = states.Count == 0 ? "动态处理待机" : string.Join(" · ", states);
        DynamicsStatusColor = result.Limited
            ? "#F06C75"
            : states.Count > 0 ? "#E6B94C" : "#49C99A";
    }

    public bool UpdateNetworkQuality(UdpAudioSessionStatistics statistics)
    {
        long frameDelta = hasQualityBaseline
            ? Math.Max(0, statistics.FramesCompleted - previousQualityFrames)
            : 0;
        long concealmentDelta = hasQualityBaseline
            ? Math.Max(0, statistics.ConcealmentFrames - previousQualityConcealments)
            : 0;
        long lostDelta = hasQualityBaseline
            ? Math.Max(0, statistics.EstimatedLostDatagrams - previousQualityLostDatagrams)
            : 0;
        previousQualityFrames = statistics.FramesCompleted;
        previousQualityConcealments = statistics.ConcealmentFrames;
        previousQualityLostDatagrams = statistics.EstimatedLostDatagrams;
        hasQualityBaseline = true;
        long clippedDelta = Interlocked.Exchange(ref clippedFramesSinceQualityUpdate, 0);

        qualityTrend.Enqueue(new ChannelQualitySample(
            concealmentDelta,
            lostDelta,
            clippedDelta,
            statistics.EstimatedJitterMilliseconds));
        while (qualityTrend.Count > 60)
        {
            qualityTrend.Dequeue();
        }

        long trendConcealments = qualityTrend.Sum(sample => sample.ConcealmentFrames);
        long trendLost = qualityTrend.Sum(sample => sample.LostDatagrams);
        long trendClipped = qualityTrend.Sum(sample => sample.ClippedFrames);
        double trendJitter = qualityTrend.Count == 0
            ? 0
            : qualityTrend.Average(sample => sample.JitterMilliseconds);

        double concealmentRate = frameDelta == 0
            ? 0
            : concealmentDelta / (double)frameDelta;
        int newQualityLevel;
        if (concealmentRate >= 0.02 ||
            concealmentDelta >= 3 ||
            clippedDelta >= 3 ||
            statistics.EstimatedJitterMilliseconds >= 30 ||
            statistics.TargetBufferMilliseconds >= 100)
        {
            newQualityLevel = 2;
            NetworkQualityText =
                $"质量较差 · 缓冲 {statistics.TargetBufferMilliseconds} ms" +
                (clippedDelta > 0 ? " · 削波" : string.Empty);
            NetworkQualityColor = "#F06C75";
        }
        else if (concealmentDelta > 0 ||
                 lostDelta > 0 ||
                 clippedDelta > 0 ||
                 statistics.EstimatedJitterMilliseconds >= 12 ||
                 statistics.TargetBufferMilliseconds >= 60)
        {
            newQualityLevel = 1;
            NetworkQualityText =
                $"质量波动 · 缓冲 {statistics.TargetBufferMilliseconds} ms" +
                (clippedDelta > 0 ? " · 削波" : string.Empty);
            NetworkQualityColor = "#E6B94C";
        }
        else
        {
            newQualityLevel = 0;
            NetworkQualityText =
                $"网络良好 · 缓冲 {statistics.TargetBufferMilliseconds} ms";
            NetworkQualityColor = "#49C99A";
        }

        NetworkQualityToolTip =
            "拖动卡片可将此音源同时输出到其他播放设备\n" +
            $"最近 {qualityTrend.Count} 秒：补偿 {trendConcealments} 帧 · " +
            $"丢包 {trendLost} · 削波 {trendClipped} 帧 · 平均抖动 {trendJitter:F1} ms";
        bool alertStarted = newQualityLevel > networkQualityLevel && newQualityLevel > 0;
        networkQualityLevel = newQualityLevel;
        return alertStarted;
    }

    public bool UpdateBluetoothQuality(BluetoothAudioSessionStatistics statistics)
    {
        long frameDelta = hasQualityBaseline
            ? Math.Max(0, statistics.DecodedFrames - previousBluetoothDecodedFrames)
            : 0;
        long gapDelta = hasQualityBaseline
            ? Math.Max(0, statistics.TimestampGaps - previousBluetoothTimestampGaps)
            : 0;
        long queueDropDelta = hasQualityBaseline
            ? Math.Max(0, statistics.QueueDrops - previousBluetoothQueueDrops)
            : 0;
        long decodeFailureDelta = hasQualityBaseline
            ? Math.Max(0, statistics.DecodeFailures - previousBluetoothDecodeFailures)
            : 0;
        long clippedDelta = Interlocked.Exchange(ref clippedFramesSinceQualityUpdate, 0);
        previousBluetoothDecodedFrames = statistics.DecodedFrames;
        previousBluetoothTimestampGaps = statistics.TimestampGaps;
        previousBluetoothQueueDrops = statistics.QueueDrops;
        previousBluetoothDecodeFailures = statistics.DecodeFailures;
        hasQualityBaseline = true;

        long droppedDelta = queueDropDelta + decodeFailureDelta;
        qualityTrend.Enqueue(new ChannelQualitySample(
            droppedDelta,
            gapDelta,
            clippedDelta,
            statistics.EstimatedJitterMilliseconds));
        while (qualityTrend.Count > 60)
        {
            qualityTrend.Dequeue();
        }

        long trendDropped = qualityTrend.Sum(sample => sample.ConcealmentFrames);
        long trendGaps = qualityTrend.Sum(sample => sample.LostDatagrams);
        long trendClipped = qualityTrend.Sum(sample => sample.ClippedFrames);
        double trendJitter = qualityTrend.Count == 0
            ? 0
            : qualityTrend.Average(sample => sample.JitterMilliseconds);
        double dropRate = frameDelta == 0 ? 0 : droppedDelta / (double)frameDelta;
        int newQualityLevel;
        if (dropRate >= 0.02 ||
            droppedDelta >= 3 ||
            gapDelta >= 3 ||
            clippedDelta >= 3 ||
            statistics.QueueDepth >= 48 ||
            statistics.EstimatedJitterMilliseconds >= 40)
        {
            newQualityLevel = 2;
            NetworkQualityText =
                $"蓝牙质量较差 · {BluetoothCodecLabel(statistics.Codec)}" +
                (clippedDelta > 0 ? " · 削波" : string.Empty);
            NetworkQualityColor = "#F06C75";
        }
        else if (droppedDelta > 0 ||
                 gapDelta > 0 ||
                 clippedDelta > 0 ||
                 statistics.QueueDepth >= 32 ||
                 statistics.EstimatedJitterMilliseconds >= 20)
        {
            newQualityLevel = 1;
            NetworkQualityText =
                $"蓝牙质量波动 · {BluetoothCodecLabel(statistics.Codec)}" +
                (clippedDelta > 0 ? " · 削波" : string.Empty);
            NetworkQualityColor = "#E6B94C";
        }
        else
        {
            newQualityLevel = 0;
            NetworkQualityText =
                $"蓝牙良好 · {BluetoothCodecLabel(statistics.Codec)} · " +
                $"{(statistics.ChannelCount == 2 ? "双声道" : "单声道")}";
            NetworkQualityColor = "#49C99A";
        }

        NetworkQualityToolTip =
            "拖动卡片可将此音源同时输出到其他播放设备\n" +
            $"最近 {qualityTrend.Count} 秒：丢帧 {trendDropped} · " +
            $"时间戳缺口 {trendGaps} · 削波 {trendClipped} 帧 · " +
            $"平均抖动 {trendJitter:F1} ms · 当前队列 {statistics.QueueDepth}/64";
        bool alertStarted = newQualityLevel > networkQualityLevel && newQualityLevel > 0;
        networkQualityLevel = newQualityLevel;
        return alertStarted;
    }

    private static string BluetoothCodecLabel(AudioCodec codec) => codec switch
    {
        AudioCodec.PcmInt16 => "PCM16",
        AudioCodec.ImaAdpcm => "IMA ADPCM",
        AudioCodec.AacLc => "AAC-LC",
        _ => codec.ToString()
    };

    private readonly record struct ChannelQualitySample(
        long ConcealmentFrames,
        long LostDatagrams,
        long ClippedFrames,
        double JitterMilliseconds);

    public void Apply(
        float volume,
        bool isMuted,
        string? equalizerPreset = null,
        IReadOnlyList<float>? gains = null,
        bool equalizerEnabled = true,
        string channelGroup = "未分组",
        float preamp = 0,
        bool gateEnabled = false,
        float gateThreshold = -48,
        bool compressorIsEnabled = false,
        float compressorThreshold = -18,
        float ratio = 4,
        bool limiterIsEnabled = true,
        float limiterCeiling = -1,
        bool? duckingTrigger = null,
        bool? duckingTarget = null,
        float duckingReduction = 12)
    {
        applying = true;
        try
        {
            VolumePercent = Math.Clamp(volume, 0f, 1f) * 100;
            IsMuted = isMuted;
            IsEqualizerEnabled = equalizerEnabled;
            SelectedChannelGroup = channelGroup;
            PreampDb = preamp;
            NoiseGateEnabled = gateEnabled;
            NoiseGateThresholdDb = gateThreshold;
            CompressorEnabled = compressorIsEnabled;
            CompressorThresholdDb = compressorThreshold;
            CompressorRatio = ratio;
            LimiterEnabled = limiterIsEnabled;
            LimiterCeilingDb = limiterCeiling;
            IsVoiceDuckingTrigger = duckingTrigger ??
                string.Equals(channelGroup, "语音", StringComparison.Ordinal);
            IsVoiceDuckingTarget = duckingTarget ?? channelGroup is "游戏" or "媒体";
            VoiceDuckingReductionDb = duckingReduction;
            EqualizerPresetOption restoredPreset = BuiltInPresets.FirstOrDefault(preset =>
                string.Equals(preset.Name, equalizerPreset, StringComparison.Ordinal)) ??
                CustomPreset;
            IReadOnlyList<float>? restoredGains = gains is { Count: 10 }
                ? gains
                : restoredPreset.Gains;
            if (restoredGains is { Count: 10 })
            {
                for (int index = 0; index < equalizerGains.Length; index++)
                {
                    SetEqualizerBand(index, restoredGains[index]);
                }
            }
            selectedEqualizerPreset = restoredPreset;
            OnPropertyChanged(nameof(SelectedEqualizerPreset));
        }
        finally
        {
            applying = false;
        }
    }

    private void SetProcessingField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (SetField(ref field, value, name) && !applying)
        {
            settingsChanged();
        }
    }

    private bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class RoutingRuleItemViewModel : INotifyPropertyChanged
{
    private readonly Action changed;
    private readonly Action<Guid> delete;
    private bool isEnabled;
    private string targetGroup;

    public RoutingRuleItemViewModel(
        ListenSphere.Configuration.AudioRoutingRuleSettings settings,
        Action changed,
        Action<Guid> delete)
    {
        RuleId = settings.RuleId;
        Name = settings.Name;
        SourcePattern = settings.SourcePattern;
        MatchMode = settings.MatchMode;
        SourceKind = settings.SourceKind;
        Transport = settings.Transport;
        Priority = settings.Priority;
        isEnabled = settings.IsEnabled;
        targetGroup = settings.TargetGroup;
        this.changed = changed;
        this.delete = delete;
        DeleteCommand = new AsyncRelayCommand(() =>
        {
            this.delete(RuleId);
            return Task.CompletedTask;
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public Guid RuleId { get; }
    public string Name { get; private set; }
    public string SourcePattern { get; }
    public ListenSphere.Configuration.AudioRouteMatchMode MatchMode { get; }
    public string? SourceKind { get; }
    public string? Transport { get; }
    public int Priority { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public string MatchSummary => MatchMode == ListenSphere.Configuration.AudioRouteMatchMode.Exact
        ? $"来源等于 {SourcePattern}"
        : $"来源包含 {SourcePattern}";

    public bool IsEnabled
    {
        get => isEnabled;
        set
        {
            if (isEnabled == value) return;
            isEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
            changed();
        }
    }

    public string TargetGroup
    {
        get => targetGroup;
        private set
        {
            if (string.Equals(targetGroup, value, StringComparison.Ordinal)) return;
            targetGroup = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TargetGroup)));
        }
    }

    public void UpdateTargetGroup(string group)
    {
        TargetGroup = group;
        Name = $"{SourcePattern} → {group}";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        changed();
    }

    public ListenSphere.Configuration.AudioRoutingRuleSettings ToSettings() => new(
        RuleId,
        Name,
        SourcePattern,
        TargetGroup,
        MatchMode,
        SourceKind,
        Transport,
        Priority,
        IsEnabled);
}

public sealed class GroupBusItemViewModel : INotifyPropertyChanged
{
    private static readonly IReadOnlyList<EqualizerPresetOption> Presets =
    [
        new("原声", [0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
        new("语音清晰", [-6, -4, -2, -1, 0, 2, 4, 3, 1, -1]),
        new("游戏脚步", [-6, -4, -2, 0, 2, 4, 5, 3, 1, -2]),
        new("音乐均衡", [1, 2, 1, 0, -1, 0, 1, 2, 2, 1]),
        new("低音增强", [5, 4, 3, 2, 0, -1, -1, 0, 1, 1]),
        new("柔和聆听", [-2, -1, 0, 1, 2, 3, 2, 0, -1, -2])
    ];

    private readonly Action settingsChanged;
    private float volumePercent = 100;
    private bool isMuted;
    private int channelCount;
    private bool applying;
    private EqualizerPresetOption selectedEqualizerPreset = Presets[0];

    public GroupBusItemViewModel(string name, Action settingsChanged)
    {
        Name = name;
        this.settingsChanged = settingsChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Name { get; }
    public IReadOnlyList<EqualizerPresetOption> EqualizerPresets => Presets;
    public IReadOnlyList<float> EqualizerGains =>
        selectedEqualizerPreset.Gains ?? Presets[0].Gains!;
    public bool HasEqualization => selectedEqualizerPreset.Gains?.Any(gain => gain != 0) == true;
    public float EffectiveGain => IsMuted ? 0f : VolumePercent / 100;
    public string MuteButtonText => IsMuted ? "取消分组静音" : "静音整个分组";

    public int ChannelCount
    {
        get => channelCount;
        set
        {
            if (SetField(ref channelCount, Math.Max(0, value)))
            {
                OnPropertyChanged(nameof(ChannelCountText));
            }
        }
    }

    public string ChannelCountText => $"{ChannelCount} 个声道";

    public float VolumePercent
    {
        get => volumePercent;
        set
        {
            if (SetField(ref volumePercent, Math.Clamp(value, 0, 100)) && !applying)
            {
                settingsChanged();
            }
        }
    }

    public bool IsMuted
    {
        get => isMuted;
        set
        {
            if (SetField(ref isMuted, value))
            {
                OnPropertyChanged(nameof(MuteButtonText));
                if (!applying)
                {
                    settingsChanged();
                }
            }
        }
    }

    public EqualizerPresetOption SelectedEqualizerPreset
    {
        get => selectedEqualizerPreset;
        set
        {
            if (value is null || ReferenceEquals(selectedEqualizerPreset, value))
            {
                return;
            }
            selectedEqualizerPreset = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EqualizerGains));
            OnPropertyChanged(nameof(HasEqualization));
            if (!applying)
            {
                settingsChanged();
            }
        }
    }

    public void Apply(float volume, bool muted, string? presetName)
    {
        applying = true;
        try
        {
            VolumePercent = Math.Clamp(volume, 0f, 1f) * 100;
            IsMuted = muted;
            SelectedEqualizerPreset = Presets.FirstOrDefault(preset =>
                string.Equals(preset.Name, presetName, StringComparison.Ordinal)) ?? Presets[0];
        }
        finally
        {
            applying = false;
        }
    }

    private bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record EqualizerPresetOption(string Name, float[]? Gains);

public enum RemoteTransportMode
{
    Wireless,
    Bluetooth,
    Wired
}
