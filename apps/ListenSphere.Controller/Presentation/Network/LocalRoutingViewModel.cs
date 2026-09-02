namespace ListenSphere.Controller;

public sealed class LocalRoutingViewModel(ControllerNetworkRuntime runtime)
    : NetworkPresentationModel(runtime)
{
    public float LocalSourceVolumePercent
    {
        get => Runtime.LocalSourceVolumePercent;
        set => Runtime.LocalSourceVolumePercent = value;
    }
    public bool IsLocalSourceMuted
    {
        get => Runtime.IsLocalSourceMuted;
        set => Runtime.IsLocalSourceMuted = value;
    }
    public string LocalSourceMuteButtonText => Runtime.LocalSourceMuteButtonText;

    public Task AddSecondaryOutputRouteAsync(Guid channelId, string deviceId) =>
        Runtime.AddSecondaryOutputRouteAsync(channelId, deviceId);

    public Task RemoveSecondaryOutputRouteAsync(Guid channelId, string deviceId) =>
        Runtime.RemoveSecondaryOutputRouteAsync(channelId, deviceId);

    public IReadOnlyList<ControllerNetworkRuntime.ApplicationOutputRouteInfo>
        GetApplicationOutputRoutes(Guid channelId) =>
        Runtime.GetApplicationOutputRoutes(channelId);
}
