using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ListenSphere.Audio.Engine;
using ListenSphere.Device;
using ListenSphere.Network;
using ListenSphere.Protocol;
using ListenSphere.Windows.Bluetooth;

namespace ListenSphere.Controller;

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
