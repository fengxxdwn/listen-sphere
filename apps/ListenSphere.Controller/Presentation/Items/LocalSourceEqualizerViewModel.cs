using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using ListenSphere.Controller.Services;

namespace ListenSphere.Controller;

public sealed class LocalSourceEqualizerViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly EqualizerPresetOption CustomPreset = new("自定义", null);
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

    private readonly Action<bool, IReadOnlyList<float>> processingChanged;
    private readonly Action settingsChanged;
    private readonly IEqualizerApoIntegration equalizerApo;
    private readonly float[] equalizerGains = new float[10];
    private EqualizerPresetOption selectedEqualizerPreset = BuiltInPresets[0];
    private bool isEqualizerEnabled = true;
    private float effectStrengthPercent = 100f;
    private bool isEditorOpen;
    private bool applying;
    private CancellationTokenSource? apoUpdate;
    private string equalizerApoStatusText;
    private string equalizerApoStatusColor;
    private string equalizerApoActionText;
    private bool disposed;

    public LocalSourceEqualizerViewModel(
        Action<bool, IReadOnlyList<float>> processingChanged,
        Action settingsChanged,
        IEqualizerApoIntegration? equalizerApo = null)
    {
        this.processingChanged = processingChanged;
        this.settingsChanged = settingsChanged;
        this.equalizerApo = equalizerApo ?? new EqualizerApoIntegration();
        EqualizerApoIntegrationSnapshot snapshot = this.equalizerApo.Snapshot;
        equalizerApoStatusText = snapshot.StatusText;
        equalizerApoStatusColor = snapshot.StatusColor;
        equalizerApoActionText = snapshot.ActionText;
        ResetEqualizerCommand = new AsyncRelayCommand(ResetEqualizerAsync);
        EqualizerApoActionCommand = new AsyncRelayCommand(OpenEqualizerApoAsync);
        RetryEqualizerApoCommand = new AsyncRelayCommand(() => ApplyToEqualizerApoAsync(TimeSpan.Zero));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string DisplayName => "本地声音";
    public string TransportLabel => "Windows 本机音源";
    public string StatusText => IsEqualizerEnabled ? "均衡器已启用" : "均衡器已旁路";
    public bool IsEditorOpen
    {
        get => isEditorOpen;
        set => SetField(ref isEditorOpen, value);
    }
    public bool IsEqualizerEnabled
    {
        get => isEqualizerEnabled;
        set
        {
            if (!SetField(ref isEqualizerEnabled, value))
            {
                return;
            }

            OnPropertyChanged(nameof(StatusText));
            NotifyProcessingChanged();
        }
    }
    public float EffectStrengthPercent
    {
        get => effectStrengthPercent;
        set
        {
            float normalized = Math.Clamp(value, 0f, 100f);
            if (!SetField(ref effectStrengthPercent, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(EqualizerCurveGains));
            NotifyProcessingChanged();
        }
    }
    public EqualizerPresetOption SelectedEqualizerPreset
    {
        get => selectedEqualizerPreset;
        set
        {
            if (value is not null && !ReferenceEquals(selectedEqualizerPreset, value))
            {
                ApplyEqualizerPreset(value);
            }
        }
    }
    public IReadOnlyList<EqualizerPresetOption> EqualizerPresets => AllPresets;
    public IReadOnlyList<float> EqualizerGains => equalizerGains;
    public IReadOnlyList<float> EqualizerCurveGains => GetEffectiveGains();
    public AsyncRelayCommand ResetEqualizerCommand { get; }
    public AsyncRelayCommand EqualizerApoActionCommand { get; }
    public AsyncRelayCommand RetryEqualizerApoCommand { get; }
    public string EqualizerApoStatusText
    {
        get => equalizerApoStatusText;
        private set => SetField(ref equalizerApoStatusText, value);
    }
    public string EqualizerApoStatusColor
    {
        get => equalizerApoStatusColor;
        private set => SetField(ref equalizerApoStatusColor, value);
    }
    public string EqualizerApoActionText
    {
        get => equalizerApoActionText;
        private set => SetField(ref equalizerApoActionText, value);
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

    public void ApplySettings(
        bool enabled,
        string? presetName,
        IReadOnlyList<float>? gains,
        float effectStrength = 1f)
    {
        applying = true;
        try
        {
            isEqualizerEnabled = enabled;
            OnPropertyChanged(nameof(IsEqualizerEnabled));
            OnPropertyChanged(nameof(StatusText));
            effectStrengthPercent = Math.Clamp(effectStrength, 0f, 1f) * 100f;
            OnPropertyChanged(nameof(EffectStrengthPercent));
            OnPropertyChanged(nameof(EqualizerCurveGains));
            float[] normalized = gains is { Count: 10 }
                ? gains.Select(gain => Math.Clamp(gain, -20f, 20f)).ToArray()
                : new float[10];
            for (var index = 0; index < normalized.Length; index++)
            {
                SetEqualizerBand(index, normalized[index]);
            }
            selectedEqualizerPreset = BuiltInPresets.FirstOrDefault(preset =>
                string.Equals(preset.Name, presetName, StringComparison.Ordinal) &&
                preset.Gains is { Length: 10 } presetGains &&
                presetGains.SequenceEqual(normalized)) ??
                (normalized.All(gain => gain == 0) ? BuiltInPresets[0] : CustomPreset);
            OnPropertyChanged(nameof(SelectedEqualizerPreset));
        }
        finally
        {
            applying = false;
        }

        processingChanged(false, GetEffectiveGains());
        ScheduleEqualizerApoUpdate(TimeSpan.Zero);
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
            NotifyProcessingChanged();
            return;
        }

        applying = true;
        try
        {
            for (var index = 0; index < equalizerGains.Length; index++)
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
        NotifyProcessingChanged();
    }

    private void SetEqualizerBand(int index, float value)
    {
        float normalized = Math.Clamp(value, -20f, 20f);
        if (equalizerGains[index] == normalized)
        {
            return;
        }

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
            NotifyProcessingChanged();
        }
    }

    private void NotifyProcessingChanged()
    {
        if (applying)
        {
            return;
        }
        processingChanged(false, GetEffectiveGains());
        ScheduleEqualizerApoUpdate(TimeSpan.FromMilliseconds(120));
        settingsChanged();
    }

    private Task OpenEqualizerApoAsync()
    {
        equalizerApo.OpenManagement();
        return Task.CompletedTask;
    }

    private void ScheduleEqualizerApoUpdate(TimeSpan delay)
    {
        if (disposed)
        {
            return;
        }
        apoUpdate?.Cancel();
        var cancellation = new CancellationTokenSource();
        apoUpdate = cancellation;
        _ = ApplyToEqualizerApoAsync(delay, cancellation);
    }

    private Task ApplyToEqualizerApoAsync(TimeSpan delay) =>
        ApplyToEqualizerApoAsync(delay, new CancellationTokenSource());

    private async Task ApplyToEqualizerApoAsync(
        TimeSpan delay,
        CancellationTokenSource cancellation)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellation.Token);
            }
            await equalizerApo.ApplyAsync(
                IsEqualizerEnabled,
                GetEffectiveGains(),
                cancellation.Token);
            PublishEqualizerApoSnapshot(equalizerApo.Snapshot);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(apoUpdate, cancellation))
            {
                apoUpdate = null;
            }
            cancellation.Dispose();
        }
    }

    private float[] GetEffectiveGains()
    {
        float scale = effectStrengthPercent / 100f;
        return equalizerGains.Select(gain => gain * scale).ToArray();
    }

    private void PublishEqualizerApoSnapshot(EqualizerApoIntegrationSnapshot snapshot)
    {
        void Apply()
        {
            EqualizerApoStatusText = snapshot.StatusText;
            EqualizerApoStatusColor = snapshot.StatusColor;
            EqualizerApoActionText = snapshot.ActionText;
        }

        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(Apply);
        }
        else
        {
            Apply();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        apoUpdate?.Cancel();
        apoUpdate = null;
        GC.SuppressFinalize(this);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
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
