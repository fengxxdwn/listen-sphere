using System.Collections.ObjectModel;

namespace ListenSphere.Controller.Design;

public sealed class ControllerDashboardDesignData
{
    public ControllerDashboardDesignData()
    {
        var remote = new DashboardRemoteChannelDesignData(
            "Xiaomi 2604FRK1EC",
            "无线网络",
            "正在传输",
            true,
            64,
            false,
            "原声");
        Network.RemoteDevices.ActiveRemoteChannels.Add(remote);
        Network.RemoteDevices.VisibleRemoteChannels.Add(remote);
        LocalSessions.Sessions.Add(new DashboardSessionDesignData(
            "播放器",
            "PID 2048",
            "Windows 输出：默认扬声器",
            72,
            false,
            38));
    }

    public DashboardNetworkDesignData Network { get; } = new();
    public DashboardLocalSessionsDesignData LocalSessions { get; } = new();
    public DashboardDiagnosticsDesignData Diagnostics { get; } = new();
}

public sealed class DashboardLocalSessionsDesignData
{
    public ObservableCollection<DashboardSessionDesignData> Sessions { get; } = [];
    public string StatusText { get; } = "音频设备与应用会话已就绪";
    public string ErrorText { get; } = string.Empty;
    public float PeakPercent { get; } = 42;
}

public sealed class DashboardDiagnosticsDesignData
{
    public string StatusText { get; } = "可导出脱敏诊断包";
}

public sealed class DashboardNetworkDesignData
{
    public DashboardNetworkDesignData Transport => this;
    public DashboardNetworkDesignData RemoteDevices => this;
    public DashboardNetworkDesignData AudioOutput => this;
    public DashboardNetworkDesignData LocalRouting => this;
    public DashboardNetworkDesignData Microphone => this;
    public DashboardNetworkDesignData RemoteAudio => this;
    public DashboardNetworkDesignData GroupMixer => this;
    public string NetworkStatus { get; } = "无线网络 · 192.168.1.25:51493";
    public string EmptyTransportText { get; } = "选择连接方式后添加设备";
    public string WirelessIpAddressText { get; } = "IP 地址：192.168.1.25";
    public string WirelessPortText { get; } = "端口：51493";
    public string BluetoothStatus { get; } = "可用";
    public string UsbStatus { get; } = "等待设备";
    public bool IsWirelessSelected { get; } = true;
    public bool IsBluetoothSelected { get; }
    public bool IsWiredSelected { get; }
    public bool IsLocalSourceMuted { get; }
    public string LocalSourceMuteButtonText { get; } = "静音";
    public float LocalSourceVolumePercent { get; } = 80;
    public ObservableCollection<DashboardRemoteChannelDesignData>
        ActiveRemoteChannels
    { get; } = [];
    public ObservableCollection<DashboardRemoteChannelDesignData>
        VisibleRemoteChannels
    { get; } = [];
    public ObservableCollection<DashboardOutputDesignData> AdditionalOutputs { get; } =
        [new("默认扬声器", "已接收 2 个音源", 62)];
}

public sealed record DashboardRemoteChannelDesignData(
    string DisplayName,
    string ListTransportLabel,
    string StatusText,
    bool IsOnline,
    float VolumePercent,
    bool IsMuted,
    string SelectedEqualizerPresetName)
{
    public string SourceName => DisplayName;
    public string SourceSubtitle => ListTransportLabel;
    public string SourceGlyph => "";
    public float AudioGlowLevel => 36;
    public string NetworkQualityText => "质量良好";
    public string NetworkQualityToolTip => "缓冲稳定";
    public string NetworkQualityColor => "#35D39A";
    public string MuteButtonText => IsMuted ? "取消静音" : "静音";
}

public sealed record DashboardSessionDesignData(
    string DisplayName,
    string ProcessText,
    string WindowsOutputText,
    float VolumePercent,
    bool IsMuted,
    float PeakPercent)
{
    public bool HasIcon => false;
    public string MuteButtonText => IsMuted ? "取消静音" : "静音";
}

public sealed record DashboardOutputDesignData(
    string DisplayName,
    string Summary,
    float VolumePercent);
