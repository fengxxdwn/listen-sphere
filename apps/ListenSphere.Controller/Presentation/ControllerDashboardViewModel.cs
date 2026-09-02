using System.Collections.ObjectModel;
using System.ComponentModel;

namespace ListenSphere.Controller.Presentation;

public sealed class ControllerDashboardViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ControllerViewModel owner;
    private bool disposed;

    public ControllerDashboardViewModel(ControllerViewModel owner)
    {
        this.owner = owner;
        owner.PropertyChanged += OnOwnerPropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ControllerNetworkViewModel Network => owner.Network;
    public ObservableCollection<AudioSessionItemViewModel> Sessions => owner.Sessions;
    public AsyncRelayCommand RefreshCommand => owner.RefreshCommand;
    public AsyncRelayCommand EnableSenderCommand => owner.EnableSenderCommand;
    public AsyncRelayCommand ExportDiagnosticsCommand => owner.ExportDiagnosticsCommand;
    public string StatusText => owner.StatusText;
    public string ErrorText => owner.ErrorText;
    public string DiagnosticStatusText => owner.DiagnosticStatusText;
    public float LocalPeakPercent => owner.LocalPeakPercent;

    public void ReportError(string message) => owner.ReportError(message);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        owner.PropertyChanged -= OnOwnerPropertyChanged;
    }

    private void OnOwnerPropertyChanged(object? sender, PropertyChangedEventArgs args) =>
        PropertyChanged?.Invoke(this, args);
}
