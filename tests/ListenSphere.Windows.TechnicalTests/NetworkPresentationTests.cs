using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using ListenSphere.Controller;
using Xunit;

namespace ListenSphere.Windows.TechnicalTests;

public sealed class NetworkPresentationTests
{
    [Fact]
    public async Task AdditionalOutputRemoveCommand_ForwardsStableRouteIdentity()
    {
        Guid channelId = Guid.NewGuid();
        const string DeviceId = "secondary-device";
        Guid capturedChannelId = Guid.Empty;
        string? capturedDeviceId = null;
        var invoked = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new AdditionalOutputRouteItemViewModel(
            channelId,
            DeviceId,
            "本地声音",
            true,
            (removedChannelId, removedDeviceId) =>
            {
                capturedChannelId = removedChannelId;
                capturedDeviceId = removedDeviceId;
                invoked.SetResult();
                return Task.CompletedTask;
            });

        item.RemoveCommand.Execute(null);
        await invoked.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        Assert.Equal(channelId, capturedChannelId);
        Assert.Equal(DeviceId, capturedDeviceId);
    }

    [Fact]
    public async Task Commands_AreForwardedFromRuntimeThroughPresentationModels()
    {
        ControllerNetworkRuntime runtime = CreateRuntimeWithoutResources();
        AsyncRelayCommand generate = new(() => Task.CompletedTask);
        AsyncRelayCommand refresh = new(() => Task.CompletedTask);
        AsyncRelayCommand wireless = new(() => Task.CompletedTask);
        SetAutoProperty(runtime, nameof(runtime.GenerateCodeCommand), generate);
        SetAutoProperty(runtime, nameof(runtime.RefreshOutputsCommand), refresh);
        SetAutoProperty(runtime, nameof(runtime.SelectWirelessCommand), wireless);
        var shell = new ControllerNetworkViewModel(runtime, () => ValueTask.CompletedTask);

        Assert.Same(generate, shell.RemoteDevices.GenerateCodeCommand);
        Assert.Same(generate, shell.GenerateCodeCommand);
        Assert.Same(refresh, shell.AudioOutput.RefreshOutputsCommand);
        Assert.Same(wireless, shell.Transport.SelectWirelessCommand);

        await shell.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotentAndDetachesPresentationModels()
    {
        ControllerNetworkRuntime runtime = CreateRuntimeWithoutResources();
        int runtimeDisposals = 0;
        int projectedChanges = 0;
        var shell = new ControllerNetworkViewModel(
            runtime,
            () =>
            {
                runtimeDisposals++;
                return ValueTask.CompletedTask;
            });
        shell.Transport.PropertyChanged += (_, _) => projectedChanges++;

        RaiseRuntimePropertyChanged(runtime, nameof(TransportViewModel.NetworkStatus));
        Assert.Equal(1, projectedChanges);

        await shell.DisposeAsync();
        await shell.DisposeAsync();
        RaiseRuntimePropertyChanged(runtime, nameof(TransportViewModel.NetworkStatus));

        Assert.Equal(1, runtimeDisposals);
        Assert.Equal(1, projectedChanges);
    }

    private static ControllerNetworkRuntime CreateRuntimeWithoutResources() =>
        (ControllerNetworkRuntime)RuntimeHelpers.GetUninitializedObject(
            typeof(ControllerNetworkRuntime));

    private static void SetAutoProperty<T>(
        ControllerNetworkRuntime runtime,
        string propertyName,
        T value)
    {
        FieldInfo field = typeof(ControllerNetworkRuntime).GetField(
            $"<{propertyName}>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Backing field for {propertyName} was not found.");
        field.SetValue(runtime, value);
    }

    private static void RaiseRuntimePropertyChanged(
        ControllerNetworkRuntime runtime,
        string propertyName)
    {
        FieldInfo field = typeof(ControllerNetworkRuntime).GetField(
            nameof(INotifyPropertyChanged.PropertyChanged),
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PropertyChanged event field was not found.");
        var handler = (PropertyChangedEventHandler?)field.GetValue(runtime);
        handler?.Invoke(runtime, new PropertyChangedEventArgs(propertyName));
    }
}
