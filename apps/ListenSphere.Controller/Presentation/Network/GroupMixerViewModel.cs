using System.Collections.ObjectModel;

namespace ListenSphere.Controller;

public sealed class GroupMixerViewModel(ControllerNetworkRuntime runtime)
    : NetworkPresentationModel(runtime)
{
    public ObservableCollection<GroupBusItemViewModel> GroupBuses => Runtime.GroupBuses;
    public ObservableCollection<GroupBusItemViewModel> ActiveGroupBuses =>
        Runtime.ActiveGroupBuses;
    public ObservableCollection<RoutingRuleItemViewModel> RoutingRules =>
        Runtime.RoutingRules;
    public bool IsGroupMixerOpen
    {
        get => Runtime.IsGroupMixerOpen;
        set => Runtime.IsGroupMixerOpen = value;
    }
    public bool AutomaticRoutingEnabled
    {
        get => Runtime.AutomaticRoutingEnabled;
        set => Runtime.AutomaticRoutingEnabled = value;
    }
    public string AutomaticRoutingStatus => Runtime.AutomaticRoutingStatus;
}
