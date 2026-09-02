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

public sealed class ControllerNetworkRuntime :
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

    public ControllerNetworkRuntime(
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

    public int SelectedMicrophoneOutputDeviceIndex
    {
        get => FindDeviceIndex(
            MicrophoneOutputDevices,
            microphoneHubCoordinator.Snapshot.SelectedVirtualOutput?.Id);
        set
        {
            if (value >= 0 && value < MicrophoneOutputDevices.Count)
            {
                SelectedMicrophoneOutputDevice = MicrophoneOutputDevices[value];
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

    public int SelectedMicrophoneMonitoringDeviceIndex
    {
        get => FindDeviceIndex(
            PlaybackDevices,
            microphoneHubCoordinator.Snapshot.SelectedMonitoringDevice?.Id);
        set
        {
            if (value >= 0 && value < PlaybackDevices.Count)
            {
                SelectedMicrophoneMonitoringDevice = PlaybackDevices[value];
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

    public int SelectedComputerMicrophoneDeviceIndex
    {
        get => FindDeviceIndex(
            RecordingDevices,
            microphoneHubCoordinator.Snapshot.SelectedComputerMicrophone?.Id);
        set
        {
            if (value >= 0 && value < RecordingDevices.Count)
            {
                SelectedComputerMicrophoneDevice = RecordingDevices[value];
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
        OnPropertyChanged(nameof(SelectedMicrophoneMonitoringDeviceIndex));
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
        OnPropertyChanged(nameof(SelectedComputerMicrophoneDeviceIndex));
        OnPropertyChanged(nameof(SelectedMicrophoneOutputDeviceIndex));
        OnPropertyChanged(nameof(SelectedMicrophoneMonitoringDeviceIndex));
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

    private static int FindDeviceIndex(
        IReadOnlyList<IAudioDevice> devices,
        string? selectedId)
    {
        if (selectedId is null)
        {
            return -1;
        }
        for (int index = 0; index < devices.Count; index++)
        {
            if (string.Equals(devices[index].Id, selectedId, StringComparison.Ordinal))
            {
                return index;
            }
        }
        return -1;
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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
