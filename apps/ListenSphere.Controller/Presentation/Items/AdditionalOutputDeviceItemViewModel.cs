using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ListenSphere.Audio.Engine;
using ListenSphere.Device;
using ListenSphere.Network;
using ListenSphere.Protocol;
using ListenSphere.Windows.Bluetooth;

namespace ListenSphere.Controller;

public sealed class AdditionalOutputDeviceItemViewModel : INotifyPropertyChanged
{
    private string summary = "拖动音源卡片到这里";
    private readonly Action<string, float> volumeChanged;
    private readonly Func<string, bool, Task<bool>> setMute;
    private float volumePercent;
    private bool isMuted;

    public AdditionalOutputDeviceItemViewModel(
        string deviceId,
        string displayName,
        float volumePercent,
        bool isMuted,
        Action<string, float> volumeChanged,
        Func<string, bool, Task<bool>> setMute)
    {
        DeviceId = deviceId;
        DisplayName = displayName;
        this.volumePercent = Math.Clamp(volumePercent, 0, 100);
        this.isMuted = isMuted;
        this.volumeChanged = volumeChanged;
        this.setMute = setMute;
        ToggleMuteCommand = new AsyncRelayCommand(ToggleMuteAsync);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string DeviceId { get; }
    public string DisplayName { get; }
    public AsyncRelayCommand ToggleMuteCommand { get; }
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

    public void RefreshEndpointState(float nextVolumePercent, bool nextIsMuted)
    {
        float normalized = Math.Clamp(nextVolumePercent, 0, 100);
        if (Math.Abs(volumePercent - normalized) >= 0.01f)
        {
            volumePercent = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VolumePercent)));
        }

        IsMuted = nextIsMuted;
    }

    public bool RemoveRoute(Guid channelId)
    {
        AdditionalOutputRouteItemViewModel? route = Routes.FirstOrDefault(
            candidate => candidate.ChannelId == channelId);
        if (route is null)
        {
            return false;
        }

        Routes.Remove(route);
        RefreshSummary();
        return true;
    }

    private async Task ToggleMuteAsync()
    {
        bool requested = !IsMuted;
        IsMuted = requested;
        IsMuted = await setMute(DeviceId, requested);
    }
}
