namespace ListenSphere.Controller.Presentation;

public sealed class ControllerDashboardViewModel(ControllerViewModel owner) : IDisposable
{
    public ControllerNetworkViewModel Network => owner.Network;
    public LocalSessionsViewModel LocalSessions => owner.LocalSessions;
    public DiagnosticsViewModel Diagnostics => owner.Diagnostics;
    public AsyncRelayCommand EnableSenderCommand => owner.EnableSenderCommand;

    public void ReportError(string message) => LocalSessions.ReportError(message);

    public void Dispose()
    {
    }
}