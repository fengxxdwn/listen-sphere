using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ListenSphere.Audio.Engine;
using ListenSphere.Device;
using ListenSphere.Network;
using ListenSphere.Protocol;
using ListenSphere.Windows.Bluetooth;

namespace ListenSphere.Controller;

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
