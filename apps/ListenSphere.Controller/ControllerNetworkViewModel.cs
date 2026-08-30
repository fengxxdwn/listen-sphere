using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
using Serilog;

namespace ListenSphere.Controller;

public sealed class ControllerNetworkViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    public static readonly Guid LocalSoundChannelId = new("4c5f5426-fdc1-47b3-b7b4-60c31c6ea601");
    private static readonly Guid ComputerMicrophoneChannelId =
        new("603669e2-6a20-4d81-8bc0-24df59ed6d42");
    private static readonly string[] GroupBusNames =
        ["未分组", "游戏", "语音", "媒体", "系统", "自定义"];
    private readonly ListenSphereControlServer server;
    private readonly PairingCodeService pairingCodes;
    private readonly ITrustedDeviceStore trustStore;
    private readonly IAudioDeviceManager audioDeviceManager;
    private readonly IAudioOutputVolumeController outputVolume;
    private readonly IWasapiCaptureSourceFactory captureSourceFactory;
    private readonly IWasapiRecordingCaptureSourceFactory recordingCaptureSourceFactory;
    private readonly IProcessLoopbackCaptureSourceFactory processCaptureSourceFactory;
    private readonly IWindowsDeviceNotificationSource deviceNotifications;
    private readonly DiagnosticArchiveService diagnostics;
    private readonly BluetoothRfcommProbeHost bluetoothHost;
    private readonly UsbAccessoryHost usbHost;
    private readonly CancellationTokenSource audioLifetime = new();
    private readonly SemaphoreSlim playbackGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, RemoteChannelItemViewModel> channelsBySession = [];
    private readonly ConcurrentDictionary<Guid, GraphicEqualizer> equalizersBySession = [];
    private readonly ConcurrentDictionary<Guid, GraphicEqualizer> groupEqualizersBySession = [];
    private readonly ConcurrentDictionary<Guid, ChannelDynamicsProcessor> dynamicsBySession = [];
    private readonly ConcurrentDictionary<Guid, ChannelDynamicsResult> dynamicsResultsBySession = [];
    private readonly VoiceDuckingController voiceDucking = new();
    private readonly ConcurrentDictionary<Guid, long> meterUpdatesBySession = [];
    private readonly ConcurrentDictionary<OutputRouteKey, SecondaryPlaybackRoute>
        activeOutputRoutes = [];
    private readonly ConcurrentDictionary<Guid, WasapiProcessLoopbackCaptureSource>
        activeLocalApplicationCaptures = [];
    private readonly ConcurrentDictionary<Guid, LocalApplicationSource>
        localApplicationSources = [];
    private readonly ConcurrentDictionary<Guid, SecondaryPlaybackRoute>
        activeMicrophoneRoutes = [];
    private readonly ConcurrentDictionary<Guid, SecondaryPlaybackRoute>
        activeMicrophoneMonitoringRoutes = [];
    private readonly ConcurrentDictionary<Guid, MicrophonePeakState> microphonePeaks = [];
    private readonly ConcurrentDictionary<OutputRouteKey, ListenSphere.Configuration.AudioOutputRouteSettings>
        configuredOutputRoutes = [];
    private readonly Dictionary<string, GroupBusItemViewModel> groupBusesByName =
        new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, ListenSphere.Configuration.ChannelSettings>
        rememberedChannelSettings = [];
    private readonly Dictionary<Guid, ListenSphere.Configuration.ChannelLayoutSettings>
        rememberedChannelLayouts = [];
    private readonly RemotePcmMixer remoteMixer = new(
        3_840,
        startupFrames: 6,
        maximumFrames: 30,
        hardClipOutput: false);
    private readonly MasterSoftLimiter masterLimiter = new();
    private MdnsControllerPublisher? publisher;
    private IAudioPlaybackSink? playback;
    private WasapiLoopbackCaptureSource? localCaptureSource;
    private string? localCaptureDeviceId;
    private readonly SemaphoreSlim localCaptureGate = new(1, 1);
    private Task? audioLoop;
    private Task? bluetoothAudioLoop;
    private Task? usbAudioLoop;
    private Task? bluetoothRecoveryLoop;
    private Task? mixerLoop;
    private Task? pairingCodeCountdownLoop;
    private IAudioDevice? selectedPlaybackDevice;
    private IAudioDevice? selectedMicrophoneOutputDevice;
    private IAudioDevice? selectedMicrophoneMonitoringDevice;
    private IAudioDevice? selectedComputerMicrophoneDevice;
    private WasapiLoopbackCaptureSource? computerMicrophoneCapture;
    private readonly SemaphoreSlim computerMicrophoneGate = new(1, 1);
    private readonly SemaphoreSlim additionalOutputsGate = new(1, 1);
    private CancellationTokenSource? volumeDebounce;
    private readonly ConcurrentDictionary<string, CancellationTokenSource>
        outputVolumeDebounces = new(StringComparer.Ordinal);
    private CancellationTokenSource? deviceChangeDebounce;
    private CancellationTokenSource? networkChangeDebounce;
    private TaskCompletionSource<bool>? deleteConfirmation;
    private string pairingCode = "------";
    private string pairingHint = "点击“生成验证码”以允许新设备配对。";
    private string pairingCodeActionText = "生成配对码";
    private DateTimeOffset? pairingCodeExpiresAt;
    private string networkStatus = "控制服务尚未启动";
    private string wirelessIpAddressText = "IP 地址：正在检测…";
    private string wirelessPortText = "端口：--";
    private string audioStatus = "远程音频接收尚未启动";
    private string audioErrorText = string.Empty;
    private string bluetoothStatus = "正在初始化";
    private string usbStatus = "正在初始化";
    private string deleteConfirmationDeviceName = string.Empty;
    private float masterVolumePercent = 100;
    private float localSourceVolumePercent = 100;
    private bool isLocalSourceMuted;
    private float microphoneOutputVolumePercent = 100;
    private bool microphoneOutputMuted;
    private bool microphoneOutputEnabled;
    private float microphoneHubPeakPercent;
    private float computerMicrophonePeakPercent;
    private long lastMicrophonePeakPublishAt;
    private bool microphoneMonitoringEnabled;
    private bool followSystemDefaultPlayback = true;
    private bool initialized;
    private bool applyingSettings;
    private bool isDeleteConfirmationVisible;
    private bool isAudioDetailsOpen;
    private bool isGroupMixerOpen;
    private bool isSystemMuted;
    private bool applyingSystemMute;
    private bool automaticRoutingEnabled = true;
    private RemoteTransportMode selectedTransport = RemoteTransportMode.Wireless;
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
        this.pairingCodes = pairingCodes;
        this.trustStore = trustStore;
        this.audioDeviceManager = audioDeviceManager;
        this.outputVolume = outputVolume;
        this.captureSourceFactory = captureSourceFactory;
        this.recordingCaptureSourceFactory = recordingCaptureSourceFactory;
        this.processCaptureSourceFactory = processCaptureSourceFactory;
        this.deviceNotifications = deviceNotifications;
        this.diagnostics = diagnostics;
        this.bluetoothHost = bluetoothHost;
        this.usbHost = usbHost;
        Identity = identity;
        foreach (string groupName in GroupBusNames)
        {
            var group = new GroupBusItemViewModel(groupName, OnGroupBusSettingsChanged);
            groupBusesByName.Add(groupName, group);
            GroupBuses.Add(group);
        }
        GenerateCodeCommand = new AsyncRelayCommand(GenerateCodeAsync);
        RefreshOutputsCommand = new AsyncRelayCommand(RefreshOutputsAsync);
        SelectWirelessCommand = new AsyncRelayCommand(
            () => SelectTransportAsync(RemoteTransportMode.Wireless));
        SelectBluetoothCommand = new AsyncRelayCommand(
            () => SelectTransportAsync(RemoteTransportMode.Bluetooth));
        SelectWiredCommand = new AsyncRelayCommand(
            () => SelectTransportAsync(RemoteTransportMode.Wired));
        ConfirmDeleteCommand = new AsyncRelayCommand(
            () => CompleteDeleteConfirmationAsync(true),
            () => IsDeleteConfirmationVisible);
        CancelDeleteCommand = new AsyncRelayCommand(
            () => CompleteDeleteConfirmationAsync(false),
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

    public string PairingCode
    {
        get => pairingCode;
        private set => SetField(ref pairingCode, value);
    }

    public string PairingCodeActionText
    {
        get => pairingCodeActionText;
        private set => SetField(ref pairingCodeActionText, value);
    }

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
        get => isSystemMuted;
        set
        {
            if (!SetField(ref isSystemMuted, value)) return;
            OnPropertyChanged(nameof(SystemMuteButtonText));
            if (!applyingSystemMute) _ = SetSystemMuteAsync(value);
        }
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
            LocalSourceControlChanged?.Invoke(this, EventArgs.Empty);
            AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string LocalSourceMuteButtonText =>
        IsLocalSourceMuted ? "取消本机音源静音" : "静音本机音源";

    public RemoteTransportMode SelectedTransport
    {
        get => selectedTransport;
        private set
        {
            if (SetField(ref selectedTransport, value))
            {
                OnPropertyChanged(nameof(IsWirelessSelected));
                OnPropertyChanged(nameof(IsBluetoothSelected));
                OnPropertyChanged(nameof(IsWiredSelected));
                OnPropertyChanged(nameof(EmptyTransportText));
                RebuildVisibleChannels();
            }
        }
    }

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
        SelectedTransport = transport;
        if (transport == RemoteTransportMode.Bluetooth)
        {
            await EnsureBluetoothListeningAsync(CancellationToken.None);
        }
    }

    public string PairingHint
    {
        get => pairingHint;
        private set => SetField(ref pairingHint, value);
    }

    public string NetworkStatus
    {
        get => networkStatus;
        private set => SetField(ref networkStatus, value);
    }

    public string WirelessIpAddressText
    {
        get => wirelessIpAddressText;
        private set => SetField(ref wirelessIpAddressText, value);
    }

    public string WirelessPortText
    {
        get => wirelessPortText;
        private set => SetField(ref wirelessPortText, value);
    }

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
        get => bluetoothStatus;
        private set => SetField(ref bluetoothStatus, value);
    }

    public string UsbStatus
    {
        get => usbStatus;
        private set => SetField(ref usbStatus, value);
    }

    public string DeleteConfirmationDeviceName
    {
        get => deleteConfirmationDeviceName;
        private set => SetField(ref deleteConfirmationDeviceName, value);
    }

    public bool IsDeleteConfirmationVisible
    {
        get => isDeleteConfirmationVisible;
        private set
        {
            if (SetField(ref isDeleteConfirmationVisible, value))
            {
                ConfirmDeleteCommand.RaiseCanExecuteChanged();
                CancelDeleteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public float MasterVolumePercent
    {
        get => masterVolumePercent;
        set
        {
            float normalized = Math.Clamp(value, 0, 100);
            if (!SetField(ref masterVolumePercent, normalized) || applyingSettings)
            {
                return;
            }

            QueueMasterVolumeChange();
            AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IAudioDevice? SelectedPlaybackDevice
    {
        get => selectedPlaybackDevice;
        set
        {
            if (!SetField(ref selectedPlaybackDevice, value) || applyingSettings || value is null)
            {
                return;
            }

            if (followSystemDefaultPlayback)
            {
                followSystemDefaultPlayback = false;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(FollowSystemDefaultPlayback)));
            }

            _ = SwitchPlaybackAsync(value);
            AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IAudioDevice? SelectedMicrophoneOutputDevice
    {
        get => selectedMicrophoneOutputDevice;
        set
        {
            if (!SetField(ref selectedMicrophoneOutputDevice, value) || applyingSettings)
            {
                return;
            }

            OnPropertyChanged(nameof(MicrophoneOutputStatus));
            OnPropertyChanged(nameof(MicrophoneOutputGlowLevel));
            _ = ResetMicrophoneOutputRoutesAsync();
            AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IAudioDevice? SelectedMicrophoneMonitoringDevice
    {
        get => selectedMicrophoneMonitoringDevice;
        set
        {
            if (!SetField(ref selectedMicrophoneMonitoringDevice, value) || applyingSettings)
            {
                return;
            }

            _ = ResetMicrophoneMonitoringRoutesAsync();
            AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IAudioDevice? SelectedComputerMicrophoneDevice
    {
        get => selectedComputerMicrophoneDevice;
        set
        {
            IAudioDevice? previous = selectedComputerMicrophoneDevice;
            if (!SetField(ref selectedComputerMicrophoneDevice, value) || applyingSettings)
            {
                return;
            }

            _ = ChangeDefaultComputerMicrophoneAsync(value, previous);
        }
    }

    public bool MicrophoneOutputEnabled
    {
        get => microphoneOutputEnabled;
        set
        {
            if (!SetField(ref microphoneOutputEnabled, value) || applyingSettings)
            {
                return;
            }

            if (!value)
            {
                MicrophoneMonitoringEnabled = false;
                microphonePeaks.Clear();
                MicrophoneHubPeakPercent = 0;
                ComputerMicrophonePeakPercent = 0;
            }
            _ = ApplyMicrophoneOutputEnabledAsync(value);
            OnPropertyChanged(nameof(MicrophoneOutputStatus));
            OnPropertyChanged(nameof(MicrophoneOutputGlowLevel));
            AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public float MicrophoneHubPeakPercent
    {
        get => microphoneHubPeakPercent;
        private set
        {
            if (SetField(ref microphoneHubPeakPercent, Math.Clamp(value, 0, 100)))
            {
                OnPropertyChanged(nameof(MicrophoneOutputGlowLevel));
            }
        }
    }

    public float ComputerMicrophonePeakPercent
    {
        get => computerMicrophonePeakPercent;
        private set => SetField(ref computerMicrophonePeakPercent, Math.Clamp(value, 0, 100));
    }

    public float MicrophoneOutputGlowLevel =>
        MicrophoneOutputEnabled &&
        !MicrophoneOutputMuted &&
        SelectedMicrophoneOutputDevice is not null
            ? MicrophoneHubPeakPercent * MicrophoneOutputVolumePercent / 100f
            : 0;

    public float MicrophoneOutputVolumePercent
    {
        get => microphoneOutputVolumePercent;
        set
        {
            if (SetField(ref microphoneOutputVolumePercent, Math.Clamp(value, 0, 100)) &&
                !applyingSettings)
            {
                OnPropertyChanged(nameof(MicrophoneOutputGlowLevel));
                AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool MicrophoneOutputMuted
    {
        get => microphoneOutputMuted;
        set
        {
            if (SetField(ref microphoneOutputMuted, value) && !applyingSettings)
            {
                OnPropertyChanged(nameof(MicrophoneOutputGlowLevel));
                AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool MicrophoneMonitoringEnabled
    {
        get => microphoneMonitoringEnabled;
        set
        {
            if (SetField(ref microphoneMonitoringEnabled, value) && !applyingSettings)
            {
                if (!value)
                {
                    _ = ResetMicrophoneMonitoringRoutesAsync();
                }
                AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string MicrophoneOutputStatus
    {
        get
        {
            if (!MicrophoneOutputEnabled)
            {
                return "麦克风中枢已关闭";
            }

            if (SelectedMicrophoneOutputDevice is null)
            {
                return "未检测到虚拟音频线；需安装 VB-CABLE、VoiceMeeter 或聆界虚拟麦克风驱动";
            }

            IAudioDevice? recordingDevice = FindPairedRecordingEndpoint(
                SelectedMicrophoneOutputDevice,
                RecordingDevices);
            return recordingDevice is null
                ? $"已找到 {SelectedMicrophoneOutputDevice.DisplayName}，但未找到配对的 Windows 录音端点"
                : $"已绑定录音设备：{recordingDevice.DisplayName}";
        }
    }

    public bool FollowSystemDefaultPlayback
    {
        get => followSystemDefaultPlayback;
        set
        {
            if (!SetField(ref followSystemDefaultPlayback, value) || applyingSettings)
            {
                return;
            }

            if (value)
            {
                _ = RefreshOutputsAsync(null);
            }

            AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
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
        MasterVolumePercent = Math.Clamp(preferredMasterVolume, 0f, 1f) * 100;
        LocalSourceVolumePercent = Math.Clamp(localSourceVolume, 0f, 1f) * 100;
        IsLocalSourceMuted = localSourceMuted;
        FollowSystemDefaultPlayback = followSystemDefault;
        automaticRoutingEnabled = enableAutomaticRouting;
        MicrophoneOutputVolumePercent = Math.Clamp(preferredMicrophoneOutputVolume, 0f, 1f) * 100;
        MicrophoneOutputMuted = microphoneOutputMuted;
        MicrophoneOutputEnabled = microphoneOutputEnabled;
        MicrophoneMonitoringEnabled = microphoneOutputEnabled && microphoneMonitoringEnabled;
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
        configuredOutputRoutes.Clear();
        foreach (ListenSphere.Configuration.AudioOutputRouteSettings route in savedOutputRoutes ?? [])
        {
            configuredOutputRoutes[new OutputRouteKey(route.ChannelId, route.DeviceId)] = route;
        }
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
        deviceNotifications.Changed += OnDeviceChanged;
        await server.StartAsync();
        RestartDiscoveryPublisher();
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        NetworkStatus =
            $"正在发布 {ListenSphereDiscovery.QualifiedServiceName} · TCP {server.Port}";
        await EnsureBluetoothListeningAsync(CancellationToken.None);
        await usbHost.StartAsync(CancellationToken.None);
        UsbStatus = "正在等待原生 USB 设备";
        await RefreshOutputsAsync(
            preferredPlaybackDeviceId,
            preferredMicrophoneOutputDeviceId,
            preferredMicrophoneMonitoringDeviceId,
            preferredComputerMicrophoneDeviceId);
        if (MicrophoneOutputEnabled)
        {
            await EnsureComputerMicrophoneCaptureAsync();
        }
        audioLoop = ConsumeAudioAsync(audioLifetime.Token);
        bluetoothAudioLoop = ConsumeBluetoothAudioAsync(audioLifetime.Token);
        usbAudioLoop = ConsumeUsbAudioAsync(audioLifetime.Token);
        bluetoothRecoveryLoop = RecoverBluetoothAvailabilityAsync(audioLifetime.Token);
        mixerLoop = PlayMixedAudioAsync(audioLifetime.Token);
        pairingCodeCountdownLoop = MonitorPairingCodeAsync(audioLifetime.Token);
        await RefreshTrustedDevicesAsync();
        UpdateDiagnosticSnapshot(
            server.AudioReceiver.Statistics,
            (playback as IAudioPlaybackDiagnostics)?.Statistics ??
            new AudioPlaybackStatistics(0, 0, 0, 0, 0, 0),
            remoteMixer.Statistics);
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
            MasterVolumePercent = Math.Clamp(masterVolume, 0f, 1f) * 100;
            FollowSystemDefaultPlayback = followSystemDefault;
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

            IAudioDevice? output = followSystemDefault
                ? PlaybackDevices.FirstOrDefault(device => device.IsDefault)
                : PlaybackDevices.FirstOrDefault(device =>
                    string.Equals(device.Id, playbackDeviceId, StringComparison.Ordinal));
            if (output is not null)
            {
                SelectedPlaybackDevice = output;
            }
        }
        finally
        {
            applyingSettings = false;
        }

        if (SelectedPlaybackDevice is not null)
        {
            await SwitchPlaybackAsync(SelectedPlaybackDevice, synchronizeMasterVolume: false);
            await SetMasterVolumeAsync();
        }

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
        CaptureOutputRoutes() => configuredOutputRoutes.Values.ToArray();

    public void RegisterLocalApplicationSource(
        Guid channelId,
        int processId,
        string displayName,
        string identityKey)
    {
        if (channelId == Guid.Empty || processId <= 0 || string.IsNullOrWhiteSpace(identityKey))
        {
            return;
        }

        localApplicationSources.TryGetValue(
            channelId,
            out LocalApplicationSource? previousSource);
        localApplicationSources[channelId] = new LocalApplicationSource(
            processId,
            displayName,
            identityKey);
        if (previousSource is not null && previousSource.ProcessId != processId)
        {
            _ = RestartLocalApplicationCaptureAsync(channelId);
            return;
        }

        if (configuredOutputRoutes.Keys.Any(key => key.ChannelId == channelId))
        {
            _ = EnsureLocalApplicationCaptureAsync(channelId);
        }
    }

    public async Task UnregisterLocalApplicationSourceAsync(Guid channelId)
    {
        localApplicationSources.TryRemove(channelId, out _);
        if (activeLocalApplicationCaptures.TryRemove(
                channelId,
                out WasapiProcessLoopbackCaptureSource? capture))
        {
            await capture.DisposeAsync();
        }
    }

    private async Task RefreshOutputsAsync() =>
        await RefreshOutputsAsync(SelectedPlaybackDevice?.Id);

    public async Task RefreshAsync()
    {
        RestartDiscoveryPublisher();
        await EnsureBluetoothListeningAsync(CancellationToken.None);
        await RefreshOutputsAsync(SelectedPlaybackDevice?.Id);
        await RefreshTrustedDevicesAsync();
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs eventArgs)
    {
        _ = Application.Current.Dispatcher.BeginInvoke(RefreshWirelessManualEndpoint);
        networkChangeDebounce?.Cancel();
        networkChangeDebounce?.Dispose();
        networkChangeDebounce = CancellationTokenSource.CreateLinkedTokenSource(
            audioLifetime.Token);
        _ = RestartDiscoveryPublisherAfterDelayAsync(networkChangeDebounce.Token);
    }

    private async Task RestartDiscoveryPublisherAfterDelayAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);
            await Application.Current.Dispatcher.InvokeAsync(RestartDiscoveryPublisher);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Failed to republish mDNS after network change");
        }
    }

    private void RestartDiscoveryPublisher()
    {
        RefreshWirelessManualEndpoint();
        MdnsControllerPublisher replacement = new(Identity, server.Port);
        MdnsControllerPublisher? previous = publisher;
        publisher = replacement;
        previous?.Dispose();
        NetworkStatus =
            $"正在发布 {ListenSphereDiscovery.QualifiedServiceName} · TCP {server.Port}";
    }

    private void RefreshWirelessManualEndpoint()
    {
        IReadOnlyList<System.Net.IPAddress> addresses =
            ListenSphereDiscovery.GetManualConnectAddresses();
        WirelessIpAddressText = addresses.Count == 0
            ? "IP 地址：等待网络"
            : $"IP 地址：{string.Join(" / ", addresses)}";
        WirelessPortText = $"端口：{server.Port}";
    }

    private async Task RecoverBluetoothAvailabilityAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await EnsureBluetoothListeningAsync(cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }
    }

    private async Task EnsureBluetoothListeningAsync(CancellationToken cancellationToken)
    {
        try
        {
            await bluetoothHost.StartAsync(cancellationToken);
            await Application.Current.Dispatcher.InvokeAsync(() =>
                BluetoothStatus = "RFCOMM 音频服务已开启");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            bool changed = !string.Equals(
                BluetoothStatus,
                "蓝牙不可用 · 开启系统蓝牙后将自动重试",
                StringComparison.Ordinal);
            await Application.Current.Dispatcher.InvokeAsync(() =>
                BluetoothStatus = "蓝牙不可用 · 开启系统蓝牙后将自动重试");
            if (changed)
            {
                Log.Warning(exception, "Bluetooth unavailable; automatic retry is active");
            }
        }
    }

    private async Task RefreshOutputsAsync(
        string? preferredDeviceId,
        string? preferredMicrophoneDeviceId = null,
        string? preferredMicrophoneMonitoringDeviceId = null,
        string? preferredComputerMicrophoneDeviceId = null)
    {
        try
        {
            IReadOnlyList<IAudioDevice> devices =
                await audioDeviceManager.GetPlaybackDevicesAsync(CancellationToken.None);
            IReadOnlyList<IAudioDevice> recordingDevices =
                await audioDeviceManager.GetRecordingDevicesAsync(CancellationToken.None);
            applyingSettings = true;
            PlaybackDevices.Clear();
            MicrophoneOutputDevices.Clear();
            RecordingDevices.Clear();
            foreach (IAudioDevice device in devices)
            {
                PlaybackDevices.Add(device);
                if (IsVirtualMicrophoneRenderEndpoint(device.DisplayName))
                {
                    MicrophoneOutputDevices.Add(device);
                }
            }
            foreach (IAudioDevice device in recordingDevices)
            {
                RecordingDevices.Add(device);
            }

            SelectedPlaybackDevice =
                (FollowSystemDefaultPlayback
                    ? devices.FirstOrDefault(device => device.IsDefault)
                    : devices.FirstOrDefault(device =>
                        string.Equals(device.Id, preferredDeviceId, StringComparison.Ordinal))) ??
                devices.FirstOrDefault(device => device.IsDefault) ??
                devices.FirstOrDefault();
            SelectedMicrophoneOutputDevice =
                MicrophoneOutputDevices.FirstOrDefault(device =>
                    string.Equals(
                        device.Id,
                        preferredMicrophoneDeviceId ?? selectedMicrophoneOutputDevice?.Id,
                        StringComparison.Ordinal)) ??
                MicrophoneOutputDevices.FirstOrDefault();
            SelectedMicrophoneMonitoringDevice =
                devices.FirstOrDefault(device =>
                    string.Equals(
                        device.Id,
                        preferredMicrophoneMonitoringDeviceId ?? selectedMicrophoneMonitoringDevice?.Id,
                        StringComparison.Ordinal)) ??
                SelectedPlaybackDevice;
            SelectedComputerMicrophoneDevice =
                recordingDevices.FirstOrDefault(device => device.IsDefault) ??
                recordingDevices.FirstOrDefault(device =>
                    string.Equals(
                        device.Id,
                        preferredComputerMicrophoneDeviceId ??
                            selectedComputerMicrophoneDevice?.Id,
                        StringComparison.Ordinal)) ??
                recordingDevices.FirstOrDefault();
            OnPropertyChanged(nameof(MicrophoneOutputStatus));
            applyingSettings = false;
            if (SelectedPlaybackDevice is null)
            {
                await playbackGate.WaitAsync();
                try
                {
                    if (playback is not null)
                    {
                        IAudioPlaybackSink previous = playback;
                        playback = null;
                        await DisposePlaybackSafelyAsync(previous);
                    }
                }
                finally
                {
                    playbackGate.Release();
                }

                AudioStatus = "没有可用的 Windows 播放设备；远程音频将被接收但不播放。";
                await RebuildAdditionalOutputsAsync();
                await EnsureLocalOutputCaptureAsync();
                return;
            }

            await SwitchPlaybackAsync(SelectedPlaybackDevice);
            await ResetMicrophoneOutputRoutesAsync();
        }
        catch (Exception exception)
        {
            applyingSettings = false;
            AudioErrorText = $"刷新输出设备失败：{exception.Message}";
            Log.Error(exception, "Failed to refresh playback endpoints");
        }
    }

    private static bool IsVirtualMicrophoneRenderEndpoint(string displayName)
    {
        string normalized = displayName.Trim();
        return normalized.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("VoiceMeeter Input", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Virtual Cable", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("虚拟音频", StringComparison.OrdinalIgnoreCase);
    }

    private static IAudioDevice? FindPairedRecordingEndpoint(
        IAudioDevice renderDevice,
        IEnumerable<IAudioDevice> recordingDevices)
    {
        string renderName = renderDevice.DisplayName;
        string[] preferredNames = renderName.Contains("CABLE", StringComparison.OrdinalIgnoreCase)
            ? ["CABLE Output", "VB-Audio"]
            : renderName.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase)
                ? ["VoiceMeeter Output", "VoiceMeeter"]
                : ["Virtual Cable", "虚拟音频"];
        return recordingDevices.FirstOrDefault(device => preferredNames.Any(name =>
            device.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task SwitchPlaybackAsync(
        IAudioDevice output,
        bool synchronizeMasterVolume = true)
    {
        await playbackGate.WaitAsync();
        try
        {
            AudioErrorText = string.Empty;
            if (playback is not null)
            {
                IAudioPlaybackSink previous = playback;
                playback = null;
                await DisposePlaybackSafelyAsync(previous);
            }

            var sink = new WasapiPlaybackSink(
                output.Id,
                GetPlaybackProfile(output));
            sink.PlaybackStopped += OnPlaybackStopped;
            try
            {
                await sink.StartAsync(CancellationToken.None);
                playback = sink;
            }
            catch
            {
                await DisposePlaybackSafelyAsync(sink);
                throw;
            }

            AudioStatus = $"UDP {server.AudioReceiver.Port} · 播放到：{output.DisplayName}";
            if (synchronizeMasterVolume)
            {
                float windowsVolume = await outputVolume.GetVolumeAsync(
                    output.Id,
                    CancellationToken.None);
                bool wasApplyingSettings = applyingSettings;
                applyingSettings = true;
                try
                {
                    MasterVolumePercent = Math.Clamp(windowsVolume, 0f, 1f) * 100;
                }
                finally
                {
                    applyingSettings = wasApplyingSettings;
                }
            }
            applyingSystemMute = true;
            try
            {
                IsSystemMuted = await outputVolume.GetMuteAsync(output.Id, CancellationToken.None);
            }
            finally
            {
                applyingSystemMute = false;
            }
            diagnostics.Record(
                DiagnosticSeverity.Information,
                "playback.endpoint.ready",
                new Dictionary<string, object?> { ["isDefault"] = output.IsDefault });
        }
        catch (Exception exception)
        {
            playback = null;
            AudioErrorText = $"无法使用输出设备“{output.DisplayName}”：{exception.Message}";
            Log.Error(exception, "Failed to switch playback endpoint {DeviceId}", output.Id);
            diagnostics.Record(
                DiagnosticSeverity.Error,
                "playback.endpoint.failed",
                new Dictionary<string, object?> { ["exceptionType"] = exception.GetType().Name });
        }
        finally
        {
            playbackGate.Release();
        }
        await RebuildAdditionalOutputsAsync();
        await EnsureLocalOutputCaptureAsync();
    }

    private async Task ConsumeAudioAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (NetworkAudioFrame frame in
                server.AudioReceiver.ReadAllAsync(cancellationToken))
            {
                channelsBySession.TryGetValue(frame.SessionId, out var channel);
                Span<float> samples = MemoryMarshal.Cast<byte, float>(frame.Pcm.AsSpan());
                float gain = ApplyChannelAndGroupProcessing(frame.SessionId, samples, channel);
                PcmGainProcessor.Apply(frame.Pcm, gain);
                ChannelDynamicsResult dynamics = ApplyChannelLimiter(
                    frame.SessionId,
                    samples,
                    channel);
                float? channelPeak = channel is null
                    ? null
                    : AudioLevelCalculator.Calculate(
                        MemoryMarshal.Cast<byte, float>(frame.Pcm)).Peak;
                if (channel is not null && channelPeak is float observedPeak)
                {
                    channel.ObservePostProcessingPeak(observedPeak);
                }
                await WriteSecondaryOutputRoutesAsync(
                    channel,
                    frame.Pcm,
                    frame.Timestamp,
                    cancellationToken).ConfigureAwait(false);
                await WriteMicrophoneOutputAsync(
                    channel,
                    frame.Pcm,
                    frame.Timestamp,
                    cancellationToken).ConfigureAwait(false);
                if (channel?.IsMicrophoneSource == true)
                {
                    await WriteMicrophoneMonitoringAsync(
                        channel,
                        frame.Pcm,
                        frame.Timestamp,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    remoteMixer.Enqueue(frame.SessionId, frame.Pcm);
                }

                if (channel is not null &&
                    channelPeak is float publishedPeak &&
                    ShouldPublishAudioLevel(frame.SessionId))
                {
                    _ = Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        channel.PeakPercent = publishedPeak * 100;
                        channel.UpdateDynamicsStatus(
                            dynamics,
                            channel.IsVoiceDuckingTarget && voiceDucking.IsVoiceActive);
                    });
                }

            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AudioStatus = "远程音频接收已停止";
        }
        catch (Exception exception)
        {
            AudioErrorText = $"远程音频播放异常：{exception.Message}";
            Log.Error(exception, "Remote audio playback loop failed");
            diagnostics.Record(
                DiagnosticSeverity.Error,
                "playback.loop.failed",
                new Dictionary<string, object?> { ["exceptionType"] = exception.GetType().Name });
        }
    }

    private async Task ConsumeBluetoothAudioAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (BluetoothPcm16Frame frame in
                bluetoothHost.ReadAudioFramesAsync(cancellationToken))
            {
                var pcm = new byte[3_840];
                Span<float> stereo = MemoryMarshal.Cast<byte, float>(pcm.AsSpan());
                for (int sample = 0; sample < BluetoothRfcommProbeHost.FrameSamples; sample++)
                {
                    int sourceIndex = sample * frame.ChannelCount;
                    float left = BinaryPrimitives.ReadInt16LittleEndian(
                        frame.Pcm.AsSpan(sourceIndex * sizeof(short))) / 32768f;
                    float right = frame.ChannelCount == 2
                        ? BinaryPrimitives.ReadInt16LittleEndian(
                            frame.Pcm.AsSpan((sourceIndex + 1) * sizeof(short))) / 32768f
                        : left;
                    stereo[sample * 2] = left;
                    stereo[(sample * 2) + 1] = right;
                }

                channelsBySession.TryGetValue(frame.SessionId, out var channel);
                float gain = ApplyChannelAndGroupProcessing(frame.SessionId, stereo, channel);
                PcmGainProcessor.Apply(pcm, gain);
                ChannelDynamicsResult dynamics = ApplyChannelLimiter(
                    frame.SessionId,
                    stereo,
                    channel);
                float? observedPeak = channel is null
                    ? null
                    : AudioLevelCalculator.Calculate(stereo).Peak;
                if (channel is not null && observedPeak is float peak)
                {
                    channel.ObservePostProcessingPeak(peak);
                }
                float? publishedPeak = channel is not null &&
                    ShouldPublishAudioLevel(frame.SessionId)
                    ? observedPeak
                    : null;
                await WriteSecondaryOutputRoutesAsync(
                    channel,
                    pcm,
                    frame.Timestamp,
                    cancellationToken).ConfigureAwait(false);
                await WriteMicrophoneOutputAsync(
                    channel,
                    pcm,
                    frame.Timestamp,
                    cancellationToken).ConfigureAwait(false);
                if (channel?.IsMicrophoneSource == true)
                {
                    await WriteMicrophoneMonitoringAsync(
                        channel,
                        pcm,
                        frame.Timestamp,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    remoteMixer.RegisterStream(frame.SessionId, preferredStartupFrames: 18);
                    remoteMixer.Enqueue(frame.SessionId, pcm);
                }
                if (channel is not null && publishedPeak is float publishedValue)
                {
                    _ = Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        channel.PeakPercent = publishedValue * 100;
                        channel.UpdateDynamicsStatus(
                            dynamics,
                            channel.IsVoiceDuckingTarget && voiceDucking.IsVoiceActive);
                    });
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AudioErrorText = $"蓝牙音频播放异常：{exception.Message}";
            Log.Error(exception, "Bluetooth audio playback loop failed");
        }
    }

    private async Task ConsumeUsbAudioAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (UsbPcm16Frame frame in usbHost.ReadAudioFramesAsync(cancellationToken))
            {
                var pcm = new byte[3_840];
                Span<float> stereo = MemoryMarshal.Cast<byte, float>(pcm.AsSpan());
                for (int sample = 0; sample < UsbAccessoryHost.FrameSamples; sample++)
                {
                    int sourceIndex = sample * frame.ChannelCount;
                    float left = BinaryPrimitives.ReadInt16LittleEndian(
                        frame.Pcm.AsSpan(sourceIndex * sizeof(short))) / 32768f;
                    float right = frame.ChannelCount == 2
                        ? BinaryPrimitives.ReadInt16LittleEndian(
                            frame.Pcm.AsSpan((sourceIndex + 1) * sizeof(short))) / 32768f
                        : left;
                    stereo[sample * 2] = left;
                    stereo[(sample * 2) + 1] = right;
                }

                channelsBySession.TryGetValue(frame.SessionId, out var channel);
                float gain = ApplyChannelAndGroupProcessing(frame.SessionId, stereo, channel);
                PcmGainProcessor.Apply(pcm, gain);
                ChannelDynamicsResult dynamics = ApplyChannelLimiter(
                    frame.SessionId,
                    stereo,
                    channel);
                float? observedPeak = channel is null
                    ? null
                    : AudioLevelCalculator.Calculate(stereo).Peak;
                if (channel is not null && observedPeak is float peak)
                {
                    channel.ObservePostProcessingPeak(peak);
                }
                float? publishedPeak = channel is not null &&
                    ShouldPublishAudioLevel(frame.SessionId)
                    ? observedPeak
                    : null;
                await WriteSecondaryOutputRoutesAsync(
                    channel,
                    pcm,
                    frame.Timestamp,
                    cancellationToken).ConfigureAwait(false);
                await WriteMicrophoneOutputAsync(
                    channel,
                    pcm,
                    frame.Timestamp,
                    cancellationToken).ConfigureAwait(false);
                if (channel?.IsMicrophoneSource == true)
                {
                    await WriteMicrophoneMonitoringAsync(
                        channel,
                        pcm,
                        frame.Timestamp,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    remoteMixer.RegisterStream(frame.SessionId, preferredStartupFrames: 6);
                    remoteMixer.Enqueue(frame.SessionId, pcm);
                }
                if (channel is not null && publishedPeak is float publishedValue)
                {
                    _ = Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        channel.PeakPercent = publishedValue * 100;
                        channel.UpdateDynamicsStatus(
                            dynamics,
                            channel.IsVoiceDuckingTarget && voiceDucking.IsVoiceActive);
                    });
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AudioErrorText = $"USB 音频播放异常：{exception.Message}";
            Log.Error(exception, "USB audio playback loop failed");
        }
    }

    private bool ShouldPublishAudioLevel(Guid sessionId)
    {
        long now = Stopwatch.GetTimestamp();
        if (meterUpdatesBySession.TryGetValue(sessionId, out long previous) &&
            Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromMilliseconds(33))
        {
            return false;
        }

        meterUpdatesBySession[sessionId] = now;
        return true;
    }

    private float ApplyChannelAndGroupProcessing(
        Guid sessionId,
        Span<float> samples,
        RemoteChannelItemViewModel? channel)
    {
        if (channel is null)
        {
            return 1f;
        }

        ChannelDynamicsProcessor dynamics = dynamicsBySession.GetOrAdd(
            sessionId,
            _ => new ChannelDynamicsProcessor());
        ChannelDynamicsResult result = dynamics.ProcessBeforeEqualizer(
            samples,
            channel.DynamicsSettings);
        dynamicsResultsBySession[sessionId] = result;
        if (channel.IsVoiceDuckingTrigger)
        {
            voiceDucking.ObserveVoice(samples);
        }

        if (channel.IsEqualizerEnabled)
        {
            equalizersBySession.GetOrAdd(sessionId, _ => new GraphicEqualizer())
                .Process(samples, channel.EqualizerGains);
        }

        if (!groupBusesByName.TryGetValue(
                channel.SelectedChannelGroup,
                out GroupBusItemViewModel? group))
        {
            float duckingGain = channel.IsVoiceDuckingTarget
                ? voiceDucking.GetTargetGain(channel.VoiceDuckingReductionDb)
                : 1f;
            return channel.EffectiveGain * duckingGain;
        }

        if (group.HasEqualization)
        {
            groupEqualizersBySession.GetOrAdd(sessionId, _ => new GraphicEqualizer())
                .Process(samples, group.EqualizerGains);
        }

        float groupDuckingGain = channel.IsVoiceDuckingTarget
            ? voiceDucking.GetTargetGain(channel.VoiceDuckingReductionDb)
            : 1f;
        return channel.EffectiveGain * group.EffectiveGain * groupDuckingGain;
    }

    private ChannelDynamicsResult ApplyChannelLimiter(
        Guid sessionId,
        Span<float> samples,
        RemoteChannelItemViewModel? channel)
    {
        if (channel is null || !dynamicsBySession.TryGetValue(sessionId, out var dynamics))
        {
            return default;
        }

        dynamicsResultsBySession.TryGetValue(sessionId, out ChannelDynamicsResult previous);
        ChannelDynamicsResult result = dynamics.ApplyLimiter(
            samples,
            channel.DynamicsSettings,
            previous);
        dynamicsResultsBySession[sessionId] = result;
        channel.ObserveLimiterActivity(result.Limited);
        return result;
    }

    private async Task PlayMixedAudioAsync(CancellationToken cancellationToken)
    {
        var mixedPcm = new byte[3_840];
        ulong timestamp = 0;
        long ticks = 0;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (remoteMixer.TryMixNext(mixedPcm))
                {
                    masterLimiter.Process(mixedPcm);
                    await WritePlaybackFrameAsync(
                        new AudioFrame(
                            mixedPcm,
                            AudioFormat.Default,
                            480,
                            timestamp),
                        cancellationToken).ConfigureAwait(false);
                }

                timestamp += 480;
                ticks++;
                if (ticks % 100 == 0)
                {
                    UdpAudioReceiverStatistics network = server.AudioReceiver.Statistics;
                    AudioPlaybackStatistics audio =
                        (playback as IAudioPlaybackDiagnostics)?.Statistics ??
                        new AudioPlaybackStatistics(0, 0, 0, 0, 0, 0);
                    RemoteMixerStatistics mixer = remoteMixer.Statistics;
                    MasterLimiterStatistics limiter = masterLimiter.Statistics;
                    UdpAudioSessionStatistics[] sessionStatistics =
                        server.AudioReceiver.SessionStatistics.ToArray();
                    BluetoothAudioSessionStatistics[] bluetoothStatistics =
                        bluetoothHost.SessionStatistics.ToArray();
                    UpdateDiagnosticSnapshot(network, audio, mixer, bluetoothStatistics);
                    _ = Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        foreach (UdpAudioSessionStatistics session in sessionStatistics)
                        {
                            if (channelsBySession.TryGetValue(
                                    session.SessionId,
                                    out RemoteChannelItemViewModel? channel))
                            {
                                if (channel.UpdateNetworkQuality(session))
                                {
                                    diagnostics.Record(
                                        DiagnosticSeverity.Warning,
                                        "audio.channel.quality-warning",
                                        new Dictionary<string, object?>
                                        {
                                            ["targetBufferMs"] = session.TargetBufferMilliseconds,
                                            ["jitterMs"] = Math.Round(session.EstimatedJitterMilliseconds, 1)
                                        });
                                }
                            }
                        }

                        foreach (BluetoothAudioSessionStatistics session in bluetoothStatistics)
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

                        UpdateBluetoothDuplexStatus(bluetoothStatistics);

                        AudioStatus =
                            $"UDP {server.AudioReceiver.Port} · 混音 {mixer.ActiveStreams} 路" +
                            $" · 缺帧 {mixer.StreamUnderflows:N0}" +
                            $" · 丢包 {network.EstimatedLostDatagrams:N0}" +
                            $" · 网络缓冲 {network.AdaptiveTargetMilliseconds} ms" +
                            $" · 抖动 {network.EstimatedJitterMilliseconds:F1} ms" +
                            $" · 队列溢出 {mixer.StreamOverflows:N0}" +
                            $" · 限幅 {limiter.LimitedSamples:N0}" +
                            $" · 播放缓冲 {audio.BufferedMilliseconds} ms" +
                            $" · 漂移 {audio.EstimatedClockDriftPpm:F0} ppm" +
                            FormatBluetoothAudioStatus(bluetoothStatistics);
                    });
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            AudioErrorText = $"远程混音播放异常：{exception.Message}";
            Log.Error(exception, "Remote mixer playback loop failed");
            diagnostics.Record(
                DiagnosticSeverity.Error,
                "mixer.loop.failed",
                new Dictionary<string, object?> { ["exceptionType"] = exception.GetType().Name });
        }
    }

    private async Task WritePlaybackFrameAsync(
        AudioFrame frame,
        CancellationToken cancellationToken)
    {
        IAudioPlaybackSink? failedPlayback = null;
        Exception? playbackFailure = null;
        await playbackGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (playback is not null)
            {
                try
                {
                    await playback.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    failedPlayback = playback;
                    playback = null;
                    playbackFailure = exception;
                }
            }
        }
        finally
        {
            playbackGate.Release();
        }

        if (failedPlayback is not null && playbackFailure is not null)
        {
            await DisposePlaybackSafelyAsync(failedPlayback).ConfigureAwait(false);
            AudioErrorText = $"播放设备暂时不可用，正在自动恢复：{playbackFailure.Message}";
            diagnostics.Record(
                DiagnosticSeverity.Warning,
                "playback.write.failed",
                new Dictionary<string, object?>
                {
                    ["exceptionType"] = playbackFailure.GetType().Name
                });
            OnDeviceChanged(this, WindowsDeviceChange.StateChanged);
        }
    }

    public async Task AddSecondaryOutputRouteAsync(Guid channelId, string deviceId)
    {
        RemoteChannelItemViewModel? channel = RemoteChannels.FirstOrDefault(
            item => item.ChannelId == channelId && item.IsOnline);
        IAudioDevice? device = PlaybackDevices.FirstOrDefault(
            item => string.Equals(item.Id, deviceId, StringComparison.Ordinal));
        bool isLocalSound = channelId == LocalSoundChannelId;
        bool isLocalApplication = localApplicationSources.TryGetValue(
            channelId,
            out LocalApplicationSource? localApplication);
        if ((!isLocalSound && !isLocalApplication && channel is null) || device is null ||
            string.Equals(SelectedPlaybackDevice?.Id, deviceId, StringComparison.Ordinal))
        {
            return;
        }

        var key = new OutputRouteKey(channelId, deviceId);
        configuredOutputRoutes[key] = new ListenSphere.Configuration.AudioOutputRouteSettings(
            channelId,
            deviceId,
            isLocalSound
                ? "本地声音"
                : isLocalApplication
                    ? localApplication!.DisplayName
                    : channel!.DisplayName);
        await EnsureSecondaryOutputRouteAsync(key, device);
        await EnsureLocalOutputCaptureAsync();
        if (isLocalApplication)
        {
            await EnsureLocalApplicationCaptureAsync(channelId);
        }
        await RebuildAdditionalOutputsAsync();
        AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
        string sourceName = isLocalSound
            ? "本地声音"
            : isLocalApplication
                ? localApplication!.DisplayName
                : channel!.DisplayName;
        AudioStatus = $"已将“{sourceName}”同时路由到 {device.DisplayName}。";
    }

    public async Task RemoveSecondaryOutputRouteAsync(Guid channelId, string deviceId)
    {
        var key = new OutputRouteKey(channelId, deviceId);
        configuredOutputRoutes.TryRemove(key, out _);
        if (activeOutputRoutes.TryRemove(key, out SecondaryPlaybackRoute? route))
        {
            await route.DisposeAsync();
        }
        await EnsureLocalOutputCaptureAsync();
        if (!configuredOutputRoutes.Keys.Any(key => key.ChannelId == channelId) &&
            activeLocalApplicationCaptures.TryRemove(
                channelId,
                out WasapiProcessLoopbackCaptureSource? capture))
        {
            await capture.DisposeAsync();
        }
        await RebuildAdditionalOutputsAsync();
        AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<ApplicationOutputRouteInfo> GetApplicationOutputRoutes(Guid channelId)
    {
        return configuredOutputRoutes
            .Where(pair => pair.Key.ChannelId == channelId)
            .Select(pair =>
            {
                IAudioDevice? device = PlaybackDevices.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, pair.Key.DeviceId, StringComparison.Ordinal));
                return new ApplicationOutputRouteInfo(
                    pair.Key.DeviceId,
                    device?.DisplayName ?? pair.Key.DeviceId,
                    activeOutputRoutes.ContainsKey(pair.Key));
            })
            .OrderBy(route => route.DeviceName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private async Task RebuildAdditionalOutputsAsync()
    {
        await additionalOutputsGate.WaitAsync();
        try
        {
            string? primaryDeviceId = SelectedPlaybackDevice?.Id;
        HashSet<string> availableDeviceIds = PlaybackDevices
            .Select(device => device.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach ((OutputRouteKey key, SecondaryPlaybackRoute route) in activeOutputRoutes.ToArray())
        {
            if (!configuredOutputRoutes.ContainsKey(key) ||
                !availableDeviceIds.Contains(key.DeviceId) ||
                string.Equals(key.DeviceId, primaryDeviceId, StringComparison.Ordinal))
            {
                if (activeOutputRoutes.TryRemove(key, out _))
                {
                    await route.DisposeAsync();
                }
            }
        }

        foreach ((OutputRouteKey key, ListenSphere.Configuration.AudioOutputRouteSettings _) in
                 configuredOutputRoutes.ToArray())
        {
            if (string.Equals(key.DeviceId, primaryDeviceId, StringComparison.Ordinal))
            {
                continue;
            }
            IAudioDevice? device = PlaybackDevices.FirstOrDefault(
                item => string.Equals(item.Id, key.DeviceId, StringComparison.Ordinal));
            if (device is not null)
            {
                await EnsureSecondaryOutputRouteAsync(key, device);
            }
        }

            AdditionalOutputs.Clear();
            foreach (IAudioDevice device in PlaybackDevices.Where(device =>
                         !string.Equals(device.Id, primaryDeviceId, StringComparison.Ordinal)))
            {
            float endpointVolume = 100;
            bool endpointMuted = false;
            try
            {
                endpointVolume = await outputVolume.GetVolumeAsync(
                    device.Id,
                    CancellationToken.None) * 100;
                endpointMuted = await outputVolume.GetMuteAsync(
                    device.Id,
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "Failed to read output endpoint state for {DeviceId}", device.Id);
            }

            var target = new AdditionalOutputDeviceItemViewModel(
                device.Id,
                device.DisplayName,
                endpointVolume,
                endpointMuted,
                QueueOutputDeviceVolumeChange,
                SetOutputDeviceMute);
            foreach ((OutputRouteKey key, ListenSphere.Configuration.AudioOutputRouteSettings settings) in
                     configuredOutputRoutes.Where(pair =>
                         string.Equals(pair.Key.DeviceId, device.Id, StringComparison.Ordinal)))
            {
                string sourceName = RemoteChannels.FirstOrDefault(channel =>
                    channel.ChannelId == key.ChannelId)?.DisplayName ??
                    localApplicationSources.GetValueOrDefault(key.ChannelId)?.DisplayName ??
                    settings.ChannelName ??
                    "已保存的音源";
                target.Routes.Add(new AdditionalOutputRouteItemViewModel(
                    key.ChannelId,
                    device.Id,
                    sourceName,
                    activeOutputRoutes.ContainsKey(key),
                    RemoveSecondaryOutputRouteAsync));
            }
            target.RefreshSummary();
                AdditionalOutputs.Add(target);
            }
        }
        finally
        {
            additionalOutputsGate.Release();
        }
    }

    private async Task EnsureSecondaryOutputRouteAsync(OutputRouteKey key, IAudioDevice device)
    {
        if (activeOutputRoutes.ContainsKey(key))
        {
            return;
        }

        var sink = new WasapiPlaybackSink(
            device.Id,
            GetPlaybackProfile(device));
        var route = new SecondaryPlaybackRoute(sink);
        try
        {
            await sink.StartAsync(CancellationToken.None);
            if (!activeOutputRoutes.TryAdd(key, route))
            {
                await route.DisposeAsync();
            }
        }
        catch (Exception exception)
        {
            await route.DisposeAsync();
            Log.Warning(
                exception,
                "Failed to start secondary output {DeviceId} for channel {ChannelId}",
                device.Id,
                key.ChannelId);
            AudioErrorText = $"附加输出“{device.DisplayName}”暂不可用：{exception.Message}";
        }
    }

    private async Task EnsureLocalApplicationCaptureAsync(Guid channelId)
    {
        if (activeLocalApplicationCaptures.ContainsKey(channelId) ||
            !localApplicationSources.TryGetValue(
                channelId,
                out LocalApplicationSource? source))
        {
            return;
        }

        WasapiProcessLoopbackCaptureSource capture =
            processCaptureSourceFactory.Create(source.ProcessId);
        if (!activeLocalApplicationCaptures.TryAdd(channelId, capture))
        {
            await capture.DisposeAsync();
            return;
        }

        try
        {
            await capture.StartAsync(
                new LocalApplicationOutputFrameSink(this, channelId),
                audioLifetime.Token);
        }
        catch (Exception exception)
        {
            activeLocalApplicationCaptures.TryRemove(channelId, out _);
            await capture.DisposeAsync();
            Log.Warning(
                exception,
                "Failed to start process loopback for local application {ProcessId}",
                source.ProcessId);
            AudioErrorText = $"无法捕获本机应用“{source.DisplayName}”：{exception.Message}";
        }
    }

    private async Task RestartLocalApplicationCaptureAsync(Guid channelId)
    {
        if (activeLocalApplicationCaptures.TryRemove(
                channelId,
                out WasapiProcessLoopbackCaptureSource? capture))
        {
            await capture.DisposeAsync();
        }

        if (configuredOutputRoutes.Keys.Any(key => key.ChannelId == channelId))
        {
            await EnsureLocalApplicationCaptureAsync(channelId);
        }
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

        await WriteOutputRoutesByChannelIdAsync(
            channel.ChannelId,
            pcm,
            timestamp,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteOutputRoutesByChannelIdAsync(
        Guid channelId,
        byte[] pcm,
        ulong timestamp,
        CancellationToken cancellationToken)
    {

        foreach ((OutputRouteKey key, SecondaryPlaybackRoute route) in activeOutputRoutes.ToArray())
        {
            if (key.ChannelId != channelId)
            {
                continue;
            }
            try
            {
                await route.WriteAsync(pcm, timestamp, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                if (activeOutputRoutes.TryRemove(key, out SecondaryPlaybackRoute? failed))
                {
                    await failed.DisposeAsync().ConfigureAwait(false);
                }
                Log.Warning(exception, "Secondary output write failed for {DeviceId}", key.DeviceId);
                _ = Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    AudioErrorText = $"附加输出播放失败，刷新设备后将尝试恢复：{exception.Message}";
                    _ = RebuildAdditionalOutputsAsync();
                });
            }
        }
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

        await WriteMicrophoneOutputFrameAsync(
            channel.ChannelId,
            pcm,
            timestamp,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteMicrophoneOutputFrameAsync(
        Guid sourceId,
        byte[] pcm,
        ulong timestamp,
        CancellationToken cancellationToken)
    {
        IAudioDevice? device = SelectedMicrophoneOutputDevice;
        if (!MicrophoneOutputEnabled)
        {
            return;
        }

        ObserveMicrophonePeak(sourceId, pcm);
        if (device is null)
        {
            return;
        }

        if (!activeMicrophoneRoutes.TryGetValue(sourceId, out SecondaryPlaybackRoute? route))
        {
            var sink = new WasapiPlaybackSink(device.Id, GetPlaybackProfile(device));
            var candidate = new SecondaryPlaybackRoute(sink);
            try
            {
                await sink.StartAsync(cancellationToken).ConfigureAwait(false);
                if (!activeMicrophoneRoutes.TryAdd(sourceId, candidate))
                {
                    await candidate.DisposeAsync().ConfigureAwait(false);
                }
                route = activeMicrophoneRoutes.GetValueOrDefault(sourceId);
            }
            catch (Exception exception)
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
                Log.Warning(exception, "Failed to start virtual microphone output {DeviceId}", device.Id);
                _ = Application.Current.Dispatcher.BeginInvoke(() =>
                    AudioErrorText = $"麦克风输出“{device.DisplayName}”暂不可用：{exception.Message}");
                return;
            }
        }

        if (route is null)
        {
            return;
        }

        try
        {
            float gain = MicrophoneOutputMuted ? 0f : MicrophoneOutputVolumePercent / 100f;
            await route.WriteAsync(pcm, timestamp, cancellationToken, gain).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (activeMicrophoneRoutes.TryRemove(sourceId, out SecondaryPlaybackRoute? failed))
            {
                await failed.DisposeAsync().ConfigureAwait(false);
            }
            Log.Warning(exception, "Virtual microphone output write failed");
            _ = Application.Current.Dispatcher.BeginInvoke(() =>
                AudioErrorText = $"麦克风输出中断，刷新设备后可恢复：{exception.Message}");
        }
    }

    private void ObserveMicrophonePeak(Guid sourceId, byte[] pcm)
    {
        float peak = AudioLevelCalculator.Calculate(
            MemoryMarshal.Cast<byte, float>(pcm)).Peak * 100;
        long now = Environment.TickCount64;
        microphonePeaks[sourceId] = new MicrophonePeakState(peak, now);
        long previousPublishAt = Interlocked.Read(ref lastMicrophonePeakPublishAt);
        if (now - previousPublishAt < 33 ||
            Interlocked.CompareExchange(ref lastMicrophonePeakPublishAt, now, previousPublishAt) !=
            previousPublishAt)
        {
            return;
        }

        float aggregate = microphonePeaks
            .Where(pair => now - pair.Value.ObservedAtMilliseconds <= 500)
            .Select(pair => pair.Value.PeakPercent)
            .DefaultIfEmpty(0)
            .Max();
        float computerPeak = microphonePeaks.TryGetValue(
                ComputerMicrophoneChannelId,
                out MicrophonePeakState computerState) &&
            now - computerState.ObservedAtMilliseconds <= 500
                ? computerState.PeakPercent
                : 0;
        _ = Application.Current.Dispatcher.BeginInvoke(() =>
        {
            MicrophoneHubPeakPercent = aggregate;
            ComputerMicrophonePeakPercent = computerPeak;
        });
    }

    private async Task ResetMicrophoneOutputRoutesAsync()
    {
        foreach ((Guid channelId, SecondaryPlaybackRoute route) in activeMicrophoneRoutes.ToArray())
        {
            if (activeMicrophoneRoutes.TryRemove(channelId, out _))
            {
                await route.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task ApplyMicrophoneOutputEnabledAsync(bool enabled)
    {
        if (enabled)
        {
            await EnsureComputerMicrophoneCaptureAsync();
            return;
        }

        await StopComputerMicrophoneCaptureAsync();
        await ResetMicrophoneOutputRoutesAsync();
        await ResetMicrophoneMonitoringRoutesAsync();
    }

    private async Task RestartComputerMicrophoneCaptureAsync()
    {
        await StopComputerMicrophoneCaptureAsync();
        if (MicrophoneOutputEnabled)
        {
            await EnsureComputerMicrophoneCaptureAsync();
        }
    }

    private async Task ChangeDefaultComputerMicrophoneAsync(
        IAudioDevice? device,
        IAudioDevice? previous)
    {
        if (device is null)
        {
            await RestartComputerMicrophoneCaptureAsync();
            AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            await audioDeviceManager.SetDefaultRecordingDeviceAsync(
                device.Id,
                CancellationToken.None);
            await RestartComputerMicrophoneCaptureAsync();
            AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
            AudioStatus = $"Windows 默认麦克风已切换为 {device.DisplayName}。";
        }
        catch (Exception exception)
        {
            Log.Warning(
                exception,
                "Failed to set Windows default recording endpoint {DeviceId}",
                device.Id);
            applyingSettings = true;
            SelectedComputerMicrophoneDevice = previous;
            applyingSettings = false;
            await RestartComputerMicrophoneCaptureAsync();
            AudioErrorText = $"无法修改 Windows 默认麦克风：{exception.Message}";
        }
    }

    private async Task EnsureComputerMicrophoneCaptureAsync()
    {
        IAudioDevice? device = SelectedComputerMicrophoneDevice;
        if (!MicrophoneOutputEnabled || device is null)
        {
            return;
        }

        await computerMicrophoneGate.WaitAsync();
        try
        {
            if (computerMicrophoneCapture is not null)
            {
                return;
            }

            WasapiLoopbackCaptureSource capture =
                recordingCaptureSourceFactory.Create(device.Id);
            try
            {
                await capture.StartAsync(
                    new ComputerMicrophoneFrameSink(this),
                    audioLifetime.Token);
                computerMicrophoneCapture = capture;
            }
            catch (Exception exception)
            {
                await capture.DisposeAsync();
                Log.Warning(exception, "Failed to capture computer microphone {DeviceId}", device.Id);
                AudioErrorText = $"电脑麦克风“{device.DisplayName}”无法启动：{exception.Message}";
            }
        }
        finally
        {
            computerMicrophoneGate.Release();
        }
    }

    private async Task StopComputerMicrophoneCaptureAsync()
    {
        WasapiLoopbackCaptureSource? capture;
        await computerMicrophoneGate.WaitAsync();
        try
        {
            capture = computerMicrophoneCapture;
            computerMicrophoneCapture = null;
        }
        finally
        {
            computerMicrophoneGate.Release();
        }

        if (capture is not null)
        {
            await capture.DisposeAsync();
        }

        RemoveMicrophonePeak(ComputerMicrophoneChannelId);
    }

    private async ValueTask WriteMicrophoneMonitoringAsync(
        RemoteChannelItemViewModel channel,
        byte[] pcm,
        ulong timestamp,
        CancellationToken cancellationToken)
    {
        await WriteMicrophoneMonitoringFrameAsync(
            channel.ChannelId,
            pcm,
            timestamp,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteMicrophoneMonitoringFrameAsync(
        Guid sourceId,
        byte[] pcm,
        ulong timestamp,
        CancellationToken cancellationToken)
    {
        IAudioDevice? device = SelectedMicrophoneMonitoringDevice;
        if (!MicrophoneOutputEnabled || !MicrophoneMonitoringEnabled || device is null)
        {
            return;
        }

        if (!activeMicrophoneMonitoringRoutes.TryGetValue(
                sourceId,
                out SecondaryPlaybackRoute? route))
        {
            var sink = new WasapiPlaybackSink(device.Id, GetPlaybackProfile(device));
            var candidate = new SecondaryPlaybackRoute(sink);
            try
            {
                await sink.StartAsync(cancellationToken).ConfigureAwait(false);
                if (!activeMicrophoneMonitoringRoutes.TryAdd(sourceId, candidate))
                {
                    await candidate.DisposeAsync().ConfigureAwait(false);
                }
                route = activeMicrophoneMonitoringRoutes.GetValueOrDefault(sourceId);
            }
            catch (Exception exception)
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
                Log.Warning(exception, "Failed to start microphone monitoring output {DeviceId}", device.Id);
                _ = Application.Current.Dispatcher.BeginInvoke(() =>
                    AudioErrorText = $"麦克风监听设备“{device.DisplayName}”暂不可用：{exception.Message}");
                return;
            }
        }

        if (route is null)
        {
            return;
        }

        try
        {
            float gain = MicrophoneOutputMuted ? 0f : MicrophoneOutputVolumePercent / 100f;
            await route.WriteAsync(pcm, timestamp, cancellationToken, gain).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (activeMicrophoneMonitoringRoutes.TryRemove(
                    sourceId,
                    out SecondaryPlaybackRoute? failed))
            {
                await failed.DisposeAsync().ConfigureAwait(false);
            }
            Log.Warning(exception, "Microphone monitoring output write failed");
            _ = Application.Current.Dispatcher.BeginInvoke(() =>
                AudioErrorText = $"麦克风监听中断，刷新设备后可恢复：{exception.Message}");
        }
    }

    private async Task ResetMicrophoneMonitoringRoutesAsync()
    {
        foreach ((Guid channelId, SecondaryPlaybackRoute route) in
                 activeMicrophoneMonitoringRoutes.ToArray())
        {
            if (activeMicrophoneMonitoringRoutes.TryRemove(channelId, out _))
            {
                await route.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task RemoveMicrophoneOutputRouteAsync(Guid channelId)
    {
        if (activeMicrophoneRoutes.TryRemove(channelId, out SecondaryPlaybackRoute? route))
        {
            await route.DisposeAsync().ConfigureAwait(false);
        }

        RemoveMicrophonePeak(channelId);
    }

    private void RemoveMicrophonePeak(Guid sourceId)
    {
        microphonePeaks.TryRemove(sourceId, out _);
        long now = Environment.TickCount64;
        float aggregate = microphonePeaks
            .Where(pair => now - pair.Value.ObservedAtMilliseconds <= 500)
            .Select(pair => pair.Value.PeakPercent)
            .DefaultIfEmpty(0)
            .Max();
        float computerPeak = microphonePeaks.TryGetValue(
                ComputerMicrophoneChannelId,
                out MicrophonePeakState computerState) &&
            now - computerState.ObservedAtMilliseconds <= 500
                ? computerState.PeakPercent
                : 0;
        _ = Application.Current.Dispatcher.BeginInvoke(() =>
        {
            MicrophoneHubPeakPercent = aggregate;
            ComputerMicrophonePeakPercent = computerPeak;
        });
    }

    private async Task RemoveMicrophoneMonitoringRouteAsync(Guid channelId)
    {
        if (activeMicrophoneMonitoringRoutes.TryRemove(
                channelId,
                out SecondaryPlaybackRoute? route))
        {
            await route.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task EnsureLocalOutputCaptureAsync()
    {
        await localCaptureGate.WaitAsync();
        try
        {
            bool shouldCapture = SelectedPlaybackDevice is not null &&
                configuredOutputRoutes.Keys.Any(key => key.ChannelId == LocalSoundChannelId);
            string? desiredDeviceId = shouldCapture ? SelectedPlaybackDevice!.Id : null;
            if (localCaptureSource is not null &&
                string.Equals(localCaptureDeviceId, desiredDeviceId, StringComparison.Ordinal))
            {
                return;
            }

            if (localCaptureSource is not null)
            {
                await localCaptureSource.DisposeAsync();
                localCaptureSource = null;
                localCaptureDeviceId = null;
            }
            if (desiredDeviceId is null)
            {
                return;
            }

            WasapiLoopbackCaptureSource source = captureSourceFactory.Create(desiredDeviceId);
            source.CaptureStopped += OnLocalOutputCaptureStopped;
            try
            {
                await source.StartAsync(new LocalOutputFrameSink(this), audioLifetime.Token);
                localCaptureSource = source;
                localCaptureDeviceId = desiredDeviceId;
            }
            catch
            {
                source.CaptureStopped -= OnLocalOutputCaptureStopped;
                await source.DisposeAsync();
                throw;
            }
        }
        catch (Exception exception)
        {
            AudioErrorText = $"无法转发本地声音：{exception.Message}";
            Log.Warning(exception, "Failed to start local loopback output routing");
        }
        finally
        {
            localCaptureGate.Release();
        }
    }

    private void OnLocalOutputCaptureStopped(object? sender, WasapiCaptureStoppedEventArgs args)
    {
        if (args.Exception is not null)
        {
            _ = Application.Current.Dispatcher.BeginInvoke(() =>
                AudioErrorText = $"本地声音转发已停止：{args.Exception.Message}");
        }
    }

    private void OnDeviceChanged(object? sender, WindowsDeviceChange change)
    {
        diagnostics.Record(
            DiagnosticSeverity.Information,
            "windows.audio-device.changed",
            new Dictionary<string, object?> { ["change"] = change.ToString() });
        deviceChangeDebounce?.Cancel();
        deviceChangeDebounce?.Dispose();
        deviceChangeDebounce = new CancellationTokenSource();
        _ = RecoverPlaybackAfterDeviceChangeAsync(change, deviceChangeDebounce.Token);
    }

    private async Task RecoverPlaybackAfterDeviceChangeAsync(
        WindowsDeviceChange change,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(600, cancellationToken);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                AudioStatus = $"检测到音频设备{GetDeviceChangeText(change)}，正在恢复播放…";
            });
            Task refresh = await Application.Current.Dispatcher.InvokeAsync(
                () => RefreshOutputsAsync(SelectedPlaybackDevice?.Id));
            await refresh;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Automatic playback device recovery failed");
            _ = Application.Current.Dispatcher.BeginInvoke(() =>
                AudioErrorText = $"播放设备自动恢复失败：{exception.Message}");
        }
    }

    private void OnPlaybackStopped(object? sender, WasapiPlaybackStoppedEventArgs args)
    {
        diagnostics.Record(
            args.Exception is null ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error,
            "playback.unexpected-stop",
            args.Exception is null
                ? null
                : new Dictionary<string, object?>
                {
                    ["exceptionType"] = args.Exception.GetType().Name
                });
        OnDeviceChanged(this, WindowsDeviceChange.StateChanged);
    }

    private void UpdateDiagnosticSnapshot(
        UdpAudioReceiverStatistics network,
        AudioPlaybackStatistics audio,
        RemoteMixerStatistics mixer,
        IReadOnlyList<BluetoothAudioSessionStatistics>? bluetooth = null)
    {
        BluetoothAudioSessionStatistics[] bluetoothSessions = bluetooth?.ToArray() ?? [];
        diagnostics.UpdateRuntimeSnapshot(new DiagnosticRuntimeSnapshot(
            DateTimeOffset.UtcNow,
            typeof(ControllerNetworkViewModel).Assembly.GetName().Version?.ToString() ?? "unknown",
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.TickCount64 / 1000,
            network.DatagramsReceived,
            network.InvalidDatagrams,
            network.AuthenticationFailures,
            network.ConcealmentFrames,
            network.EstimatedLostDatagrams,
            network.LateDatagrams,
            network.OutputOverflows,
            network.OutputQueueDepth,
            network.ActiveSessions,
            network.JitterBufferedFrames,
            network.AdaptiveTargetMilliseconds,
            network.EstimatedJitterMilliseconds,
            bluetoothSessions.Sum(session => session.EncodedFrames),
            bluetoothSessions.Sum(session => session.DecodedFrames),
            bluetoothSessions.Sum(session => session.TimestampGaps),
            bluetoothSessions.Sum(session => session.QueueDrops),
            bluetoothSessions.Sum(session => session.DecodeFailures),
            bluetoothSessions.Sum(session => session.QueueDepth),
            mixer.MixedFrames,
            mixer.StreamUnderflows,
            mixer.StreamOverflows,
            mixer.ClippedSamples,
            audio.FramesWritten,
            audio.BufferUnderruns,
            audio.BufferOverflows,
            audio.DriftCorrections,
            audio.BufferedMilliseconds,
            audio.EstimatedClockDriftPpm));
    }

    private static string FormatBluetoothAudioStatus(
        IReadOnlyList<BluetoothAudioSessionStatistics> sessions)
    {
        if (sessions.Count == 0)
        {
            return string.Empty;
        }

        long drops = sessions.Sum(session => session.QueueDrops);
        long gaps = sessions.Sum(session => session.TimestampGaps);
        int depth = sessions.Sum(session => session.QueueDepth);
        string formats = string.Join(
            "/",
            sessions.Select(session => session.Codec.ToString()).Distinct(StringComparer.Ordinal));
        return $" · 蓝牙 {formats} · 队列 {depth} · 缺口 {gaps} · 丢帧 {drops}";
    }

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

    private static string GetDeviceChangeText(WindowsDeviceChange change) => change switch
    {
        WindowsDeviceChange.Added => "接入",
        WindowsDeviceChange.Removed => "移除",
        WindowsDeviceChange.DefaultChanged => "默认项变化",
        _ => "状态变化"
    };

    private static WasapiPlaybackProfile GetPlaybackProfile(IAudioDevice device) =>
        device is WindowsAudioDevice { IsBluetooth: true }
            ? WasapiPlaybackProfile.BluetoothResilient
            : WasapiPlaybackProfile.Standard;

    private async Task DisposePlaybackSafelyAsync(IAudioPlaybackSink sink)
    {
        if (sink is WasapiPlaybackSink wasapiSink)
        {
            wasapiSink.PlaybackStopped -= OnPlaybackStopped;
        }

        try
        {
            await sink.StopAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Playback stop failed during recovery");
        }

        try
        {
            await sink.DisposeAsync();
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Playback dispose failed during recovery");
        }
    }

    private void QueueMasterVolumeChange()
    {
        volumeDebounce?.Cancel();
        volumeDebounce?.Dispose();
        foreach (CancellationTokenSource cancellation in outputVolumeDebounces.Values)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
        outputVolumeDebounces.Clear();
        volumeDebounce = new CancellationTokenSource();
        _ = SetMasterVolumeAfterDelayAsync(volumeDebounce);
    }

    private async Task SetMasterVolumeAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(120, cancellation.Token);
            await SetMasterVolumeAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            _ = Application.Current.Dispatcher.BeginInvoke(() =>
                AudioErrorText = $"设置主音量失败：{exception.Message}");
        }
    }

    private async Task SetMasterVolumeAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedPlaybackDevice is not null)
        {
            await outputVolume.SetVolumeAsync(
                SelectedPlaybackDevice.Id,
                MasterVolumePercent / 100,
                cancellationToken);
        }
    }

    private async Task SetSystemMuteAsync(bool isMuted)
    {
        if (SelectedPlaybackDevice is null) return;
        try
        {
            await outputVolume.SetMuteAsync(
                SelectedPlaybackDevice.Id,
                isMuted,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            AudioErrorText = $"设置系统静音失败：{exception.Message}";
            Log.Warning(exception, "Failed to set output endpoint mute");
        }
    }

    private void QueueOutputDeviceVolumeChange(string deviceId, float volume)
    {
        if (outputVolumeDebounces.TryRemove(deviceId, out CancellationTokenSource? previous))
        {
            previous.Cancel();
            previous.Dispose();
        }

        var cancellation = new CancellationTokenSource();
        outputVolumeDebounces[deviceId] = cancellation;
        _ = SetOutputDeviceVolumeAfterDelayAsync(deviceId, volume, cancellation);
    }

    private async Task SetOutputDeviceVolumeAfterDelayAsync(
        string deviceId,
        float volume,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(120, cancellation.Token);
            await outputVolume.SetVolumeAsync(deviceId, volume, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            _ = Application.Current.Dispatcher.BeginInvoke(() =>
                AudioErrorText = $"设置输出设备音量失败：{exception.Message}");
        }
        finally
        {
            if (outputVolumeDebounces.TryGetValue(deviceId, out CancellationTokenSource? current) &&
                ReferenceEquals(current, cancellation))
            {
                outputVolumeDebounces.TryRemove(deviceId, out _);
            }

            cancellation.Dispose();
        }
    }

    private void SetOutputDeviceMute(string deviceId, bool isMuted) =>
        _ = SetOutputDeviceMuteAsync(deviceId, isMuted);

    private async Task SetOutputDeviceMuteAsync(string deviceId, bool isMuted)
    {
        try
        {
            await outputVolume.SetMuteAsync(deviceId, isMuted, CancellationToken.None);
        }
        catch (Exception exception)
        {
            AudioErrorText = $"设置输出设备静音失败：{exception.Message}";
            Log.Warning(exception, "Failed to set output endpoint mute for {DeviceId}", deviceId);
        }
    }

    private Task GenerateCodeAsync()
    {
        PairingCode code = pairingCodes.Generate();
        PairingCode = $"{code.Value[..3]} {code.Value[3..]}";
        pairingCodeExpiresAt = code.ExpiresAt;
        PairingCodeActionText = "重新生成";
        UpdatePairingCodeCountdown();
        return Task.CompletedTask;
    }

    private async Task MonitorPairingCodeAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await Application.Current.Dispatcher.InvokeAsync(UpdatePairingCodeCountdown);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
    }

    private void UpdatePairingCodeCountdown()
    {
        if (pairingCodeExpiresAt is not { } expiresAt)
        {
            return;
        }

        TimeSpan remaining = expiresAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            ClearPairingCode("配对码已过期，请重新生成。");
            return;
        }

        if (!pairingCodes.HasActiveCode)
        {
            PairingHint = "配对码已验证，正在建立设备信任…";
            return;
        }

        int remainingSeconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        PairingHint =
            $"剩余 {remainingSeconds / 60:00}:{remainingSeconds % 60:00} · 单次使用，仅首次配对需要。";
    }

    private void CompletePairingCodeIfConsumed(string successMessage)
    {
        if (pairingCodeExpiresAt is not { } expiresAt || pairingCodes.HasActiveCode)
        {
            return;
        }

        ClearPairingCode(
            DateTimeOffset.UtcNow >= expiresAt
                ? "配对码已过期，请重新生成。"
                : successMessage);
    }

    private void ClearPairingCode(string hint)
    {
        pairingCodes.Cancel();
        pairingCodeExpiresAt = null;
        PairingCode = "------";
        PairingCodeActionText = "生成配对码";
        PairingHint = hint;
    }

    private async Task RevokeDeviceAsync(Guid deviceId)
    {
        TrustedDevice? trusted = TrustedDevices.FirstOrDefault(
            device => device.DeviceId == deviceId);
        RemoteChannelItemViewModel? channel = RemoteChannels.FirstOrDefault(
            item => item.ParentDeviceId == deviceId && !item.IsApplicationSource);
        if (trusted is null && channel is null)
        {
            return;
        }

        string name = trusted?.DisplayName ?? channel!.DisplayName;
        if (!await RequestDeleteConfirmationAsync(name))
        {
            return;
        }

        await bluetoothHost.DisconnectDeviceAsync(deviceId);
        await usbHost.DisconnectDeviceAsync(deviceId);
        await server.RevokeAsync(deviceId, CancellationToken.None);
        NetworkStatus = $"已删除设备：{name}；再次连接需要重新配对。";
        foreach (RemoteChannelItemViewModel removing in RemoteChannels
                     .Where(item => item.ParentDeviceId == deviceId).ToArray())
        {
            foreach (OutputRouteKey routeKey in configuredOutputRoutes.Keys
                         .Where(key => key.ChannelId == removing.ChannelId).ToArray())
            {
                configuredOutputRoutes.TryRemove(routeKey, out _);
                if (activeOutputRoutes.TryRemove(routeKey, out SecondaryPlaybackRoute? outputRoute))
                {
                    await outputRoute.DisposeAsync();
                }
            }
            if (removing.SessionId is Guid sessionId)
            {
                channelsBySession.TryRemove(sessionId, out _);
                remoteMixer.RemoveStream(sessionId);
                equalizersBySession.TryRemove(sessionId, out _);
                groupEqualizersBySession.TryRemove(sessionId, out _);
                dynamicsBySession.TryRemove(sessionId, out _);
                dynamicsResultsBySession.TryRemove(sessionId, out _);
            }

            RemoteChannels.Remove(removing);
            VisibleRemoteChannels.Remove(removing);
            ActiveRemoteChannels.Remove(removing);
        }

        await RefreshTrustedDevicesAsync();
        await RebuildAdditionalOutputsAsync();
        AudioSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<bool> RequestDeleteConfirmationAsync(string deviceName)
    {
        if (deleteConfirmation is not null)
        {
            return false;
        }

        DeleteConfirmationDeviceName = deviceName;
        deleteConfirmation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        IsDeleteConfirmationVisible = true;
        return await deleteConfirmation.Task;
    }

    private Task CompleteDeleteConfirmationAsync(bool confirmed)
    {
        TaskCompletionSource<bool>? completion = deleteConfirmation;
        deleteConfirmation = null;
        IsDeleteConfirmationVisible = false;
        DeleteConfirmationDeviceName = string.Empty;
        completion?.TrySetResult(confirmed);
        return Task.CompletedTask;
    }

    private async Task RefreshTrustedDevicesAsync()
    {
        IReadOnlyList<TrustedDevice> devices = await trustStore.GetAllAsync(
            CancellationToken.None);
        TrustedDevices.Clear();
        foreach (TrustedDevice device in devices.OrderBy(item => item.DisplayName))
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

    private void OnGroupBusSettingsChanged() =>
        AudioSettingsChanged?.Invoke(this, EventArgs.Empty);

    private void RebuildActiveGroupBuses()
    {
        foreach (GroupBusItemViewModel group in GroupBuses)
        {
            group.ChannelCount = ActiveRemoteChannels.Count(channel =>
                string.Equals(channel.SelectedChannelGroup, group.Name, StringComparison.Ordinal));
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
        await bluetoothHost.DisconnectDeviceAsync(deviceId);
        await usbHost.DisconnectDeviceAsync(deviceId);
        await server.DisconnectDeviceAsync(deviceId);
        foreach (RemoteChannelItemViewModel affected in RemoteChannels.Where(
                     item => item.ParentDeviceId == deviceId).ToArray())
        {
            if (affected.SessionId is Guid sessionId)
            {
                channelsBySession.TryRemove(sessionId, out _);
                remoteMixer.RemoveStream(sessionId);
                equalizersBySession.TryRemove(sessionId, out _);
                groupEqualizersBySession.TryRemove(sessionId, out _);
                dynamicsBySession.TryRemove(sessionId, out _);
                dynamicsResultsBySession.TryRemove(sessionId, out _);
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
                        channelsBySession.TryRemove(legacySession, out _);
                        remoteMixer.RemoveStream(legacySession);
                        equalizersBySession.TryRemove(legacySession, out _);
                        groupEqualizersBySession.TryRemove(legacySession, out _);
                        dynamicsBySession.TryRemove(legacySession, out _);
                        dynamicsResultsBySession.TryRemove(legacySession, out _);
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
                    channelsBySession[sessionId] = channel;
                    remoteMixer.RegisterStream(sessionId);
                }
                else if (args.State == DeviceConnectionState.Offline)
                {
                    if (args.AudioSessionId is Guid endedSession)
                    {
                        channelsBySession.TryRemove(endedSession, out _);
                        remoteMixer.RemoveStream(endedSession);
                        equalizersBySession.TryRemove(endedSession, out _);
                        groupEqualizersBySession.TryRemove(endedSession, out _);
                        dynamicsBySession.TryRemove(endedSession, out _);
                        dynamicsResultsBySession.TryRemove(endedSession, out _);
                    }

                    channel.SessionId = null;
                    channel.PeakPercent = 0;
                    await RemoveMicrophoneOutputRouteAsync(channel.ChannelId);
                    await RemoveMicrophoneMonitoringRouteAsync(channel.ChannelId);
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
                            channelsBySession.TryRemove(childSession, out _);
                            remoteMixer.RemoveStream(childSession);
                            equalizersBySession.TryRemove(childSession, out _);
                            groupEqualizersBySession.TryRemove(childSession, out _);
                            dynamicsBySession.TryRemove(childSession, out _);
                            dynamicsResultsBySession.TryRemove(childSession, out _);
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
                    CompletePairingCodeIfConsumed(
                        "设备已建立证书固定信任；后续将自动重连。");
                    await RefreshTrustedDevicesAsync();
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

    private void RemoveApplicationChannel(RemoteChannelItemViewModel channel)
    {
        if (channel.SessionId is Guid sessionId)
        {
            channelsBySession.TryRemove(sessionId, out _);
            remoteMixer.RemoveStream(sessionId);
            equalizersBySession.TryRemove(sessionId, out _);
            groupEqualizersBySession.TryRemove(sessionId, out _);
            dynamicsBySession.TryRemove(sessionId, out _);
            dynamicsResultsBySession.TryRemove(sessionId, out _);
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
                channelsBySession[args.SessionId] = channel;
                remoteMixer.RegisterStream(args.SessionId, preferredStartupFrames: 18);
                CompletePairingCodeIfConsumed(
                    "蓝牙设备已建立身份信任；后续可直接重连。");
                await RefreshTrustedDevicesAsync();
            }
            else
            {
                channelsBySession.TryRemove(args.SessionId, out _);
                remoteMixer.RemoveStream(args.SessionId);
                equalizersBySession.TryRemove(args.SessionId, out _);
                groupEqualizersBySession.TryRemove(args.SessionId, out _);
                dynamicsBySession.TryRemove(args.SessionId, out _);
                dynamicsResultsBySession.TryRemove(args.SessionId, out _);
                channel.SessionId = null;
                channel.PeakPercent = 0;
                await RemoveMicrophoneOutputRouteAsync(channel.ChannelId);
                await RemoveMicrophoneMonitoringRouteAsync(channel.ChannelId);
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
                channelsBySession[args.SessionId] = channel;
                remoteMixer.RegisterStream(args.SessionId, preferredStartupFrames: 6);
                CompletePairingCodeIfConsumed(
                    "有线设备已建立身份信任；后续插线可直接重连。");
                await RefreshTrustedDevicesAsync();
            }
            else
            {
                channelsBySession.TryRemove(args.SessionId, out _);
                remoteMixer.RemoveStream(args.SessionId);
                equalizersBySession.TryRemove(args.SessionId, out _);
                groupEqualizersBySession.TryRemove(args.SessionId, out _);
                dynamicsBySession.TryRemove(args.SessionId, out _);
                dynamicsResultsBySession.TryRemove(args.SessionId, out _);
                channel.SessionId = null;
                channel.PeakPercent = 0;
                await RemoveMicrophoneOutputRouteAsync(channel.ChannelId);
                await RemoveMicrophoneMonitoringRouteAsync(channel.ChannelId);
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
        await CompleteDeleteConfirmationAsync(false);
        server.PeerChanged -= OnPeerChanged;
        bluetoothHost.ProbeReceived -= OnBluetoothProbeReceived;
        bluetoothHost.SessionChanged -= OnBluetoothSessionChanged;
        bluetoothHost.Faulted -= OnBluetoothHostFaulted;
        usbHost.StatusChanged -= OnUsbStatusChanged;
        usbHost.SessionChanged -= OnUsbSessionChanged;
        usbHost.Faulted -= OnUsbHostFaulted;
        deviceNotifications.Changed -= OnDeviceChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        volumeDebounce?.Cancel();
        volumeDebounce?.Dispose();
        deviceChangeDebounce?.Cancel();
        deviceChangeDebounce?.Dispose();
        networkChangeDebounce?.Cancel();
        networkChangeDebounce?.Dispose();
        await audioLifetime.CancelAsync();
        if (pairingCodeCountdownLoop is not null)
        {
            await pairingCodeCountdownLoop;
        }
        publisher?.Dispose();
        publisher = null;
        await server.DisposeAsync();
        if (audioLoop is not null)
        {
            await audioLoop;
        }

        if (bluetoothAudioLoop is not null)
        {
            await bluetoothAudioLoop;
        }

        if (usbAudioLoop is not null)
        {
            await usbAudioLoop;
        }

        if (bluetoothRecoveryLoop is not null)
        {
            try { await bluetoothRecoveryLoop; }
            catch (OperationCanceledException) { }
        }

        if (mixerLoop is not null)
        {
            await mixerLoop;
        }

        await localCaptureGate.WaitAsync();
        try
        {
            if (localCaptureSource is not null)
            {
                localCaptureSource.CaptureStopped -= OnLocalOutputCaptureStopped;
                await localCaptureSource.DisposeAsync();
                localCaptureSource = null;
                localCaptureDeviceId = null;
            }
        }
        finally
        {
            localCaptureGate.Release();
        }

        foreach ((OutputRouteKey key, SecondaryPlaybackRoute route) in activeOutputRoutes.ToArray())
        {
            if (activeOutputRoutes.TryRemove(key, out _))
            {
                await route.DisposeAsync();
            }
        }
        foreach ((Guid channelId, WasapiProcessLoopbackCaptureSource capture) in
                 activeLocalApplicationCaptures.ToArray())
        {
            if (activeLocalApplicationCaptures.TryRemove(channelId, out _))
            {
                await capture.DisposeAsync();
            }
        }
        await StopComputerMicrophoneCaptureAsync();
        await ResetMicrophoneOutputRoutesAsync();
        await ResetMicrophoneMonitoringRoutesAsync();
        AdditionalOutputs.Clear();

        await playbackGate.WaitAsync();
        try
        {
            if (playback is not null)
            {
                await DisposePlaybackSafelyAsync(playback);
                playback = null;
            }
        }
        finally
        {
            playbackGate.Release();
        }

        playbackGate.Dispose();
        localCaptureGate.Dispose();
        computerMicrophoneGate.Dispose();
        additionalOutputsGate.Dispose();
        await bluetoothHost.DisposeAsync();
        await usbHost.DisposeAsync();
        audioLifetime.Dispose();
    }

    private readonly record struct OutputRouteKey(Guid ChannelId, string DeviceId);
    private readonly record struct MicrophonePeakState(
        float PeakPercent,
        long ObservedAtMilliseconds);
    public sealed record ApplicationOutputRouteInfo(
        string DeviceId,
        string DeviceName,
        bool IsActive);
    private sealed record LocalApplicationSource(
        int ProcessId,
        string DisplayName,
        string IdentityKey);

    private sealed class SecondaryPlaybackRoute(WasapiPlaybackSink sink) : IAsyncDisposable
    {
        private readonly MasterSoftLimiter limiter = new();

        public async ValueTask WriteAsync(
            byte[] pcm,
            ulong timestamp,
            CancellationToken cancellationToken,
            float gain = 1f)
        {
            byte[] copy = pcm.ToArray();
            PcmGainProcessor.Apply(copy, Math.Clamp(gain, 0f, 1f));
            limiter.Process(copy);
            await sink.WriteAsync(
                new AudioFrame(copy, AudioFormat.Default, 480, timestamp),
                cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await sink.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Log.Debug(exception, "Secondary playback stop failed");
            }
            await sink.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class LocalOutputFrameSink(ControllerNetworkViewModel owner) : IAudioFrameSink
    {
        public async ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken)
        {
            byte[] pcm = frame.Data.ToArray();
            PcmGainProcessor.Apply(
                pcm,
                owner.IsLocalSourceMuted ? 0f : owner.LocalSourceVolumePercent / 100f);
            await owner.WriteOutputRoutesByChannelIdAsync(
                LocalSoundChannelId,
                pcm,
                frame.Timestamp,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class LocalApplicationOutputFrameSink(
        ControllerNetworkViewModel owner,
        Guid channelId) : IAudioFrameSink
    {
        public async ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken)
        {
            byte[] pcm = frame.Data.ToArray();
            PcmGainProcessor.Apply(
                pcm,
                owner.IsLocalSourceMuted ? 0f : owner.LocalSourceVolumePercent / 100f);
            await owner.WriteOutputRoutesByChannelIdAsync(
                channelId,
                pcm,
                frame.Timestamp,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ComputerMicrophoneFrameSink(
        ControllerNetworkViewModel owner) : IAudioFrameSink
    {
        public async ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken)
        {
            byte[] pcm = frame.Data.ToArray();
            await owner.WriteMicrophoneOutputFrameAsync(
                ComputerMicrophoneChannelId,
                pcm,
                frame.Timestamp,
                cancellationToken).ConfigureAwait(false);
            await owner.WriteMicrophoneMonitoringFrameAsync(
                ComputerMicrophoneChannelId,
                pcm,
                frame.Timestamp,
                cancellationToken).ConfigureAwait(false);
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
