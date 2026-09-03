namespace ListenSphere.Controller.Presentation;

public sealed class FirstRunViewModel : ObservableViewModel
{
    private readonly Func<Task<bool>> persist;
    private bool isVisible;

    public FirstRunViewModel(Func<Task<bool>> persist)
    {
        this.persist = persist;
        FinishCommand = new AsyncRelayCommand(FinishAsync);
    }

    public AsyncRelayCommand FinishCommand { get; }

    public bool IsVisible
    {
        get => isVisible;
        private set => SetField(ref isVisible, value);
    }

    public void Initialize(bool completed) => IsVisible = !completed;

    private async Task FinishAsync()
    {
        IsVisible = false;
        await persist();
    }
}