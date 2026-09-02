using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ListenSphere.Controller;

public abstract class NetworkPresentationModel : INotifyPropertyChanged, IDisposable
{
    private bool disposed;

    protected NetworkPresentationModel(ControllerNetworkRuntime runtime)
    {
        Runtime = runtime;
        Runtime.PropertyChanged += OnRuntimePropertyChanged;
    }

    protected ControllerNetworkRuntime Runtime { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Runtime.PropertyChanged -= OnRuntimePropertyChanged;
        GC.SuppressFinalize(this);
    }

    private void OnRuntimePropertyChanged(object? sender, PropertyChangedEventArgs args) =>
        OnPropertyChanged(args.PropertyName);

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
