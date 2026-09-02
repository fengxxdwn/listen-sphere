using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ListenSphere.Audio.Engine;
using ListenSphere.Device;
using ListenSphere.Network;
using ListenSphere.Protocol;
using ListenSphere.Windows.Bluetooth;

namespace ListenSphere.Controller;

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
