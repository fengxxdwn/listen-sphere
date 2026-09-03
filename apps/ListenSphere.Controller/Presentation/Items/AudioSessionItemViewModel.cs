using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using ListenSphere.Windows.AudioSessions;

namespace ListenSphere.Controller;

public sealed class AudioSessionItemViewModel : INotifyPropertyChanged
{
    private const long VolumeSnapshotGuardMilliseconds = 1500;
    private readonly Action<string, float> volumeChanged;
    private readonly Action<string, bool> muteChanged;
    private bool applyingSnapshot;
    private string displayName;
    private int processId;
    private float volumePercent;
    private bool isMuted;
    private bool isActive;
    private float peakPercent;
    private ImageSource? icon;
    private bool isEditorOpen;
    private string windowsOutputText = "Windows 输出：未知设备";
    private string[] sessionIds = [];
    private AvailableApplicationOutputDeviceItemViewModel? selectedAdditionalOutput;
    private float? pendingVolumePercent;
    private long volumeSnapshotGuardUntil;

    public AudioSessionItemViewModel(
        WindowsAudioSession snapshot,
        Action<string, float> volumeChanged,
        Action<string, bool> muteChanged)
        : this([snapshot], volumeChanged, muteChanged)
    {
    }

    public AudioSessionItemViewModel(
        IReadOnlyList<WindowsAudioSession> snapshots,
        Action<string, float> volumeChanged,
        Action<string, bool> muteChanged)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0)
        {
            throw new ArgumentException("At least one audio session is required.", nameof(snapshots));
        }

        ApplicationIdentityKey = CreateApplicationIdentityKey(snapshots[0]);
        RoutingChannelId = CreateStableRoutingChannelId(ApplicationIdentityKey);
        this.volumeChanged = volumeChanged;
        this.muteChanged = muteChanged;
        displayName = snapshots[0].DisplayName;
        Update(snapshots);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string SessionId => sessionIds[0];
    public IReadOnlyList<string> SessionIds => sessionIds;
    public string ApplicationIdentityKey { get; }
    public Guid RoutingChannelId { get; }
    public bool CanRouteToListenSphere => ProcessId > 0;
    public ObservableCollection<LocalApplicationOutputRouteItemViewModel>
        AdditionalOutputRoutes
    { get; } = [];
    public ObservableCollection<AvailableApplicationOutputDeviceItemViewModel>
        AvailableAdditionalOutputs
    { get; } = [];
    public AvailableApplicationOutputDeviceItemViewModel? SelectedAdditionalOutput
    {
        get => selectedAdditionalOutput;
        set => SetField(ref selectedAdditionalOutput, value);
    }
    public string AdditionalOutputSummary => AdditionalOutputRoutes.Count == 0
        ? "尚未添加聆界附加输出"
        : $"已添加 {AdditionalOutputRoutes.Count} 个附加输出";
    public bool IsEditorOpen
    {
        get => isEditorOpen;
        set => SetField(ref isEditorOpen, value);
    }

    public string DisplayName
    {
        get => displayName;
        private set => SetField(ref displayName, value);
    }

    public int ProcessId
    {
        get => processId;
        private set
        {
            if (SetField(ref processId, value))
            {
                OnPropertyChanged(nameof(ProcessText));
            }
        }
    }

    public string ProcessText => ProcessId > 0 ? $"PID {ProcessId}" : "Windows";
    public string WindowsOutputText => windowsOutputText;

    public float VolumePercent
    {
        get => volumePercent;
        set
        {
            float normalized = Math.Clamp(value, 0, 100);
            if (SetField(ref volumePercent, normalized) && !applyingSnapshot)
            {
                pendingVolumePercent = normalized;
                volumeSnapshotGuardUntil =
                    Environment.TickCount64 + VolumeSnapshotGuardMilliseconds;
                foreach (string sessionId in sessionIds)
                {
                    volumeChanged(sessionId, normalized / 100);
                }
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
                if (!applyingSnapshot)
                {
                    foreach (string sessionId in sessionIds)
                    {
                        muteChanged(sessionId, value);
                    }
                }
            }
        }
    }

    public string MuteButtonText => IsMuted ? "取消静音" : "静音";

    public bool IsActive
    {
        get => isActive;
        private set
        {
            if (SetField(ref isActive, value))
            {
                OnPropertyChanged(nameof(ActivityText));
                OnPropertyChanged(nameof(ActivityBrush));
            }
        }
    }

    public string ActivityText => IsActive ? "正在发声" : "静音";
    public Brush ActivityBrush => IsActive ? Brushes.SeaGreen : Brushes.Gray;

    public float PeakPercent
    {
        get => peakPercent;
        private set => SetField(ref peakPercent, value);
    }

    public ImageSource? Icon
    {
        get => icon;
        private set
        {
            if (SetField(ref icon, value))
            {
                OnPropertyChanged(nameof(HasIcon));
            }
        }
    }

    public bool HasIcon => Icon is not null;

    public void Update(WindowsAudioSession snapshot) => Update([snapshot]);

    public void Update(IReadOnlyList<WindowsAudioSession> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0)
        {
            return;
        }

        WindowsAudioSession representative = snapshots
            .OrderByDescending(snapshot => snapshot.IsActive)
            .ThenByDescending(snapshot => snapshot.Peak)
            .First();
        applyingSnapshot = true;
        try
        {
            sessionIds = snapshots
                .Select(snapshot => snapshot.SessionId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            OnPropertyChanged(nameof(SessionId));
            OnPropertyChanged(nameof(SessionIds));
            DisplayName = representative.DisplayName;
            ProcessId = representative.ProcessId;
            ApplySnapshotVolume(representative.Volume * 100);
            IsMuted = snapshots.All(snapshot => snapshot.IsMuted);
            IsActive = snapshots.Any(snapshot => snapshot.IsActive);
            PeakPercent = snapshots.Max(snapshot => snapshot.Peak) * 100;
            Icon = ApplicationIconLoader.Load(
                representative.ProcessPath,
                representative.IconPath);
            string[] outputNames = snapshots
                .Select(snapshot => snapshot.OutputDeviceName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            string nextOutputText = outputNames.Length switch
            {
                0 => "Windows 输出：未知设备",
                1 => $"Windows 输出：{outputNames[0]}",
                _ => $"Windows 输出：{outputNames.Length} 个设备 · {string.Join(" / ", outputNames)}"
            };
            if (!string.Equals(windowsOutputText, nextOutputText, StringComparison.Ordinal))
            {
                windowsOutputText = nextOutputText;
                OnPropertyChanged(nameof(WindowsOutputText));
            }
        }
        finally
        {
            applyingSnapshot = false;
        }
    }

    public void ReplaceAdditionalOutputRoutes(
        IReadOnlyList<ControllerNetworkViewModel.ApplicationOutputRouteInfo> routes,
        IReadOnlyList<AvailableApplicationOutputDeviceItemViewModel> availableDevices,
        Func<Guid, string, Task> remove)
    {
        bool routesChanged = AdditionalOutputRoutes.Count != routes.Count ||
            AdditionalOutputRoutes.Zip(routes).Any(pair =>
                !string.Equals(
                    pair.First.DeviceId,
                    pair.Second.DeviceId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    pair.First.DeviceName,
                    pair.Second.DeviceName,
                    StringComparison.Ordinal) ||
                pair.First.IsActive != pair.Second.IsActive);
        if (routesChanged)
        {
            AdditionalOutputRoutes.Clear();
            foreach (ControllerNetworkViewModel.ApplicationOutputRouteInfo route in routes)
            {
                AdditionalOutputRoutes.Add(new LocalApplicationOutputRouteItemViewModel(
                    RoutingChannelId,
                    route.DeviceId,
                    route.DeviceName,
                    route.IsActive,
                    remove));
            }

            OnPropertyChanged(nameof(AdditionalOutputSummary));
        }

        bool availableDevicesChanged =
            AvailableAdditionalOutputs.Count != availableDevices.Count ||
            !AvailableAdditionalOutputs.SequenceEqual(availableDevices);
        if (availableDevicesChanged)
        {
            string? selectedDeviceId = SelectedAdditionalOutput?.DeviceId;
            AvailableAdditionalOutputs.Clear();
            foreach (AvailableApplicationOutputDeviceItemViewModel device in availableDevices)
            {
                AvailableAdditionalOutputs.Add(device);
            }

            SelectedAdditionalOutput = AvailableAdditionalOutputs.FirstOrDefault(device =>
                string.Equals(device.DeviceId, selectedDeviceId, StringComparison.Ordinal)) ??
                AvailableAdditionalOutputs.FirstOrDefault();
        }
    }

    private void ApplySnapshotVolume(float snapshotVolumePercent)
    {
        float normalized = Math.Clamp(snapshotVolumePercent, 0, 100);
        bool guardActive =
            pendingVolumePercent.HasValue &&
            Environment.TickCount64 <= volumeSnapshotGuardUntil;
        bool confirmsPendingValue =
            pendingVolumePercent.HasValue &&
            Math.Abs(normalized - pendingVolumePercent.Value) <= 0.5f;

        if (guardActive && !confirmsPendingValue)
        {
            return;
        }

        pendingVolumePercent = null;
        volumeSnapshotGuardUntil = 0;
        VolumePercent = normalized;
    }

    public static string CreateApplicationIdentityKey(WindowsAudioSession snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.ProcessPath))
        {
            try
            {
                return $"path:{Path.GetFullPath(snapshot.ProcessPath).ToUpperInvariant()}";
            }
            catch (Exception) when (
                snapshot.ProcessPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                // Fall back to the display identity below for protected/system sessions.
            }
        }

        string prefix = snapshot.ProcessId > 0 ? "app" : "system";
        return $"{prefix}:{snapshot.DisplayName.Trim().ToUpperInvariant()}";
    }

    private static Guid CreateStableRoutingChannelId(string identityKey)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identityKey));
        return new Guid(hash.AsSpan(0, 16));
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

public sealed class LocalApplicationOutputRouteItemViewModel
{
    public LocalApplicationOutputRouteItemViewModel(
        Guid channelId,
        string deviceId,
        string deviceName,
        bool isActive,
        Func<Guid, string, Task> remove)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;
        IsActive = isActive;
        RemoveCommand = new AsyncRelayCommand(() => remove(channelId, DeviceId));
    }

    public string DeviceId { get; }
    public string DeviceName { get; }
    public bool IsActive { get; }
    public string StatusText => IsActive ? "正在输出" : "等待设备恢复";
    public AsyncRelayCommand RemoveCommand { get; }
}

public sealed record AvailableApplicationOutputDeviceItemViewModel(
    string DeviceId,
    string DeviceName);
