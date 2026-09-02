using System.Collections.Concurrent;
using ListenSphere.Audio.Abstractions;
using ListenSphere.Audio.Engine;
using ListenSphere.Configuration;
using ListenSphere.Windows.Audio;
using Serilog;

namespace ListenSphere.Controller.Coordinators;

internal sealed record LocalAudioRouteSnapshot(
    Guid ChannelId,
    string DeviceId,
    string SourceName,
    string DeviceName,
    bool IsActive);

internal sealed record LocalAudioOutputDeviceSnapshot(
    string DeviceId,
    string DisplayName,
    float VolumePercent,
    bool IsMuted,
    IReadOnlyList<LocalAudioRouteSnapshot> Routes);

internal sealed record LocalAudioRoutingSnapshot(
    IReadOnlyList<LocalAudioOutputDeviceSnapshot> Outputs,
    IReadOnlyList<AudioOutputRouteSettings> ConfiguredRoutes,
    string Status,
    string ErrorText,
    long Revision)
{
    public static LocalAudioRoutingSnapshot Empty { get; } = new(
        Array.Empty<LocalAudioOutputDeviceSnapshot>(),
        Array.Empty<AudioOutputRouteSettings>(),
        string.Empty,
        string.Empty,
        0);
}

internal interface ILocalAudioRoutingRuntime
{
    IAudioPlaybackSink CreatePlaybackSink(IAudioDevice device);
    IAudioCaptureSource CreateSystemLoopback(string deviceId);
    IAudioCaptureSource CreateProcessLoopback(int processId);
}

internal sealed class ControllerLocalAudioRoutingRuntime(
    IWasapiCaptureSourceFactory captureFactory,
    IProcessLoopbackCaptureSourceFactory processCaptureFactory) : ILocalAudioRoutingRuntime
{
    public IAudioPlaybackSink CreatePlaybackSink(IAudioDevice device) =>
        new WasapiPlaybackSink(
            device.Id,
            device is WindowsAudioDevice { IsBluetooth: true }
                ? WasapiPlaybackProfile.BluetoothResilient
                : WasapiPlaybackProfile.Standard);

    public IAudioCaptureSource CreateSystemLoopback(string deviceId) =>
        captureFactory.Create(deviceId);

    public IAudioCaptureSource CreateProcessLoopback(int processId) =>
        processCaptureFactory.Create(processId);
}

internal sealed class LocalAudioRoutingCoordinator : IAsyncDisposable
{
    private readonly ILocalAudioRoutingRuntime runtime;
    private readonly Guid localSoundChannelId;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim reconcileGate = new(1, 1);
    private readonly object snapshotGate = new();
    private readonly ConcurrentDictionary<OutputRouteKey, SecondaryPlaybackRoute> activeRoutes = [];
    private readonly ConcurrentDictionary<OutputRouteKey, AudioOutputRouteSettings>
        configuredRoutes = [];
    private readonly ConcurrentDictionary<OutputRouteKey, long> routeVersions = [];
    private readonly ConcurrentDictionary<Guid, IAudioCaptureSource> applicationCaptures = [];
    private readonly ConcurrentDictionary<Guid, LocalApplicationSource> applicationSources = [];
    private IReadOnlyList<IAudioDevice> devices = Array.Empty<IAudioDevice>();
    private IReadOnlyList<AudioOutputEndpointSnapshot> endpoints =
        Array.Empty<AudioOutputEndpointSnapshot>();
    private string? primaryDeviceId;
    private IAudioCaptureSource? systemCapture;
    private string? systemCaptureDeviceId;
    private LocalAudioRoutingSnapshot snapshot = LocalAudioRoutingSnapshot.Empty;
    private float localGain = 1f;
    private bool disposed;

    public LocalAudioRoutingCoordinator(
        IWasapiCaptureSourceFactory captureFactory,
        IProcessLoopbackCaptureSourceFactory processCaptureFactory,
        Guid localSoundChannelId)
        : this(
            new ControllerLocalAudioRoutingRuntime(captureFactory, processCaptureFactory),
            localSoundChannelId)
    {
    }

    internal LocalAudioRoutingCoordinator(
        ILocalAudioRoutingRuntime runtime,
        Guid localSoundChannelId)
    {
        this.runtime = runtime;
        this.localSoundChannelId = localSoundChannelId;
    }

    public event EventHandler<LocalAudioRoutingSnapshot>? SnapshotChanged;
    public event EventHandler? RoutesChanged;

    public LocalAudioRoutingSnapshot Snapshot => Volatile.Read(ref snapshot);

    public void ConfigureRoutes(IEnumerable<AudioOutputRouteSettings> routes)
    {
        configuredRoutes.Clear();
        foreach (AudioOutputRouteSettings route in routes)
        {
            configuredRoutes[new OutputRouteKey(route.ChannelId, route.DeviceId)] = route;
        }
        PublishSnapshot();
    }

    public void SetLocalSourceGain(float volume, bool isMuted) =>
        Volatile.Write(ref localGain, isMuted ? 0f : Math.Clamp(volume, 0f, 1f));

    public IReadOnlyList<AudioOutputRouteSettings> CaptureRoutes() =>
        configuredRoutes.Values.ToArray();

    public string? GetApplicationSourceName(Guid channelId) =>
        applicationSources.TryGetValue(channelId, out LocalApplicationSource? source)
            ? source.DisplayName
            : null;

    public void RegisterApplicationSource(
        Guid channelId,
        int processId,
        string displayName,
        string identityKey)
    {
        if (channelId == Guid.Empty || processId <= 0 || string.IsNullOrWhiteSpace(identityKey))
        {
            return;
        }
        applicationSources.TryGetValue(channelId, out LocalApplicationSource? previous);
        applicationSources[channelId] = new LocalApplicationSource(
            processId,
            displayName,
            identityKey);
        if (previous is not null && previous.ProcessId != processId)
        {
            _ = RestartApplicationCaptureAsync(channelId);
        }
        else if (HasRoutes(channelId))
        {
            _ = EnsureApplicationCaptureAsync(channelId);
        }
        PublishSnapshot();
    }

    public async Task UnregisterApplicationSourceAsync(Guid channelId)
    {
        applicationSources.TryRemove(channelId, out _);
        if (applicationCaptures.TryRemove(channelId, out IAudioCaptureSource? capture))
        {
            await capture.DisposeAsync().ConfigureAwait(false);
        }
        PublishSnapshot();
    }

    public async Task UpdateOutputDevicesAsync(
        IReadOnlyList<IAudioDevice> nextDevices,
        IReadOnlyList<AudioOutputEndpointSnapshot> nextEndpoints,
        string? nextPrimaryDeviceId,
        CancellationToken cancellationToken = default)
    {
        devices = nextDevices.ToArray();
        endpoints = nextEndpoints.ToArray();
        primaryDeviceId = nextPrimaryDeviceId;
        await ReconcileAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> AddRouteAsync(
        Guid channelId,
        string deviceId,
        string sourceName,
        CancellationToken cancellationToken = default)
    {
        IAudioDevice? device = FindDevice(deviceId);
        if (device is null || string.Equals(deviceId, primaryDeviceId, StringComparison.Ordinal))
        {
            return Task.FromResult(false);
        }

        var key = new OutputRouteKey(channelId, deviceId);
        long version = routeVersions.AddOrUpdate(key, 1, (_, current) => current + 1);
        configuredRoutes[key] = new AudioOutputRouteSettings(channelId, deviceId, sourceName);
        PublishSnapshot(status: $"正在将“{sourceName}”路由到 {device.DisplayName}…");
        RoutesChanged?.Invoke(this, EventArgs.Empty);
        return Task.Run(
            () => AddRouteCoreAsync(key, device, sourceName, version, cancellationToken),
            cancellationToken);
    }

    private async Task<bool> AddRouteCoreAsync(
        OutputRouteKey key,
        IAudioDevice device,
        string sourceName,
        long version,
        CancellationToken cancellationToken)
    {
        await reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsCurrentConfiguredRoute(key, version))
            {
                return false;
            }
            await EnsureRouteAsync(key, device, cancellationToken).ConfigureAwait(false);
            if (!IsCurrentConfiguredRoute(key, version))
            {
                return false;
            }
            await EnsureCaptureForChannelAsync(key.ChannelId).ConfigureAwait(false);
            if (!IsCurrentConfiguredRoute(key, version))
            {
                return false;
            }
            PublishSnapshot(status: $"已将“{sourceName}”同时路由到 {device.DisplayName}。");
            return true;
        }
        finally
        {
            reconcileGate.Release();
        }
    }

    public Task RemoveRouteAsync(Guid channelId, string deviceId)
    {
        var key = new OutputRouteKey(channelId, deviceId);
        long version = routeVersions.AddOrUpdate(key, 1, (_, current) => current + 1);
        bool removedConfiguration = configuredRoutes.TryRemove(key, out _);
        bool removedActiveRoute = activeRoutes.TryRemove(
            key,
            out SecondaryPlaybackRoute? route);
        Log.Information(
            "Removing secondary output route {ChannelId} from {DeviceId}; configured={Configured}, active={Active}",
            channelId,
            deviceId,
            removedConfiguration,
            removedActiveRoute);
        PublishSnapshot();
        RoutesChanged?.Invoke(this, EventArgs.Empty);
        return Task.Run(() => RemoveRouteCoreAsync(key, version, route));
    }

    private async Task RemoveRouteCoreAsync(
        OutputRouteKey key,
        long version,
        SecondaryPlaybackRoute? immediateRoute)
    {
        await reconcileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            bool removalIsCurrent = routeVersions.TryGetValue(key, out long currentVersion) &&
                currentVersion == version &&
                !configuredRoutes.ContainsKey(key);
            SecondaryPlaybackRoute? delayedRoute = null;
            if (removalIsCurrent)
            {
                activeRoutes.TryRemove(key, out delayedRoute);
                await EnsureSystemCaptureAsync().ConfigureAwait(false);
                if (!HasRoutes(key.ChannelId) &&
                    applicationCaptures.TryRemove(
                        key.ChannelId,
                        out IAudioCaptureSource? capture))
                {
                    await capture.DisposeAsync().ConfigureAwait(false);
                }
            }
            if (immediateRoute is not null)
            {
                await immediateRoute.DisposeAsync().ConfigureAwait(false);
            }
            if (delayedRoute is not null && !ReferenceEquals(delayedRoute, immediateRoute))
            {
                await delayedRoute.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            reconcileGate.Release();
        }
    }

    public IReadOnlyList<LocalAudioRouteSnapshot> GetRoutes(Guid channelId) =>
        configuredRoutes
            .Where(pair => pair.Key.ChannelId == channelId)
            .Select(pair => new LocalAudioRouteSnapshot(
                channelId,
                pair.Key.DeviceId,
                applicationSources.GetValueOrDefault(channelId)?.DisplayName ??
                    pair.Value.ChannelName ?? "已保存的音源",
                FindDevice(pair.Key.DeviceId)?.DisplayName ?? pair.Key.DeviceId,
                activeRoutes.ContainsKey(pair.Key)))
            .OrderBy(route => route.DeviceName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    public async Task RemoveChannelsAsync(IEnumerable<Guid> channelIds)
    {
        HashSet<Guid> removing = channelIds.ToHashSet();
        foreach (OutputRouteKey key in configuredRoutes.Keys
                     .Where(key => removing.Contains(key.ChannelId)).ToArray())
        {
            configuredRoutes.TryRemove(key, out _);
            if (activeRoutes.TryRemove(key, out SecondaryPlaybackRoute? route))
            {
                await route.DisposeAsync().ConfigureAwait(false);
            }
        }
        foreach (Guid channelId in removing)
        {
            if (applicationCaptures.TryRemove(channelId, out IAudioCaptureSource? capture))
            {
                await capture.DisposeAsync().ConfigureAwait(false);
            }
            applicationSources.TryRemove(channelId, out _);
        }
        await EnsureSystemCaptureAsync().ConfigureAwait(false);
        PublishSnapshot();
        RoutesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask WriteAsync(
        Guid channelId,
        byte[] pcm,
        ulong timestamp,
        CancellationToken cancellationToken)
    {
        foreach ((OutputRouteKey key, SecondaryPlaybackRoute route) in activeRoutes.ToArray())
        {
            if (key.ChannelId != channelId)
            {
                continue;
            }
            try
            {
                await route.WriteAsync(pcm, timestamp, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                if (activeRoutes.TryRemove(key, out SecondaryPlaybackRoute? failed))
                {
                    await failed.DisposeAsync().ConfigureAwait(false);
                }
                Log.Warning(exception, "Secondary output write failed for {DeviceId}", key.DeviceId);
                PublishSnapshot(error:
                    $"附加输出播放失败，刷新设备后将尝试恢复：{exception.Message}");
            }
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HashSet<string> available = devices.Select(device => device.Id)
                .ToHashSet(StringComparer.Ordinal);
            foreach ((OutputRouteKey key, SecondaryPlaybackRoute route) in activeRoutes.ToArray())
            {
                if (!configuredRoutes.ContainsKey(key) ||
                    !available.Contains(key.DeviceId) ||
                    string.Equals(key.DeviceId, primaryDeviceId, StringComparison.Ordinal))
                {
                    if (activeRoutes.TryRemove(key, out _))
                    {
                        await route.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            foreach (OutputRouteKey key in configuredRoutes.Keys.ToArray())
            {
                if (string.Equals(key.DeviceId, primaryDeviceId, StringComparison.Ordinal))
                {
                    continue;
                }
                IAudioDevice? device = FindDevice(key.DeviceId);
                if (device is not null)
                {
                    await EnsureRouteAsync(key, device, cancellationToken).ConfigureAwait(false);
                }
            }
            await EnsureSystemCaptureAsync().ConfigureAwait(false);
            foreach (Guid channelId in applicationSources.Keys.Where(HasRoutes).ToArray())
            {
                await EnsureApplicationCaptureAsync(channelId).ConfigureAwait(false);
            }
            PublishSnapshot();
        }
        finally
        {
            reconcileGate.Release();
        }
    }

    private async Task EnsureRouteAsync(
        OutputRouteKey key,
        IAudioDevice device,
        CancellationToken cancellationToken)
    {
        if (activeRoutes.ContainsKey(key))
        {
            return;
        }
        var route = new SecondaryPlaybackRoute(runtime.CreatePlaybackSink(device));
        try
        {
            await route.StartAsync(cancellationToken).ConfigureAwait(false);
            if (!activeRoutes.TryAdd(key, route))
            {
                await route.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            await route.DisposeAsync().ConfigureAwait(false);
            Log.Warning(exception,
                "Failed to start secondary output {DeviceId} for channel {ChannelId}",
                device.Id, key.ChannelId);
            PublishSnapshot(error: $"附加输出“{device.DisplayName}”暂不可用：{exception.Message}");
        }
    }

    private Task EnsureCaptureForChannelAsync(Guid channelId) =>
        channelId == localSoundChannelId
            ? EnsureSystemCaptureAsync()
            : EnsureApplicationCaptureAsync(channelId);

    private async Task EnsureSystemCaptureAsync()
    {
        bool shouldCapture = primaryDeviceId is not null && HasRoutes(localSoundChannelId);
        string? desired = shouldCapture ? primaryDeviceId : null;
        if (systemCapture is not null && string.Equals(
                systemCaptureDeviceId, desired, StringComparison.Ordinal))
        {
            return;
        }
        if (systemCapture is not null)
        {
            if (systemCapture is WasapiLoopbackCaptureSource previous)
            {
                previous.CaptureStopped -= OnSystemCaptureStopped;
            }
            await systemCapture.DisposeAsync().ConfigureAwait(false);
            systemCapture = null;
            systemCaptureDeviceId = null;
        }
        if (desired is null)
        {
            return;
        }
        IAudioCaptureSource source = runtime.CreateSystemLoopback(desired);
        if (source is WasapiLoopbackCaptureSource wasapi)
        {
            wasapi.CaptureStopped += OnSystemCaptureStopped;
        }
        try
        {
            await source.StartAsync(
                new RoutingFrameSink(this, localSoundChannelId),
                lifetime.Token).ConfigureAwait(false);
            systemCapture = source;
            systemCaptureDeviceId = desired;
        }
        catch (Exception exception)
        {
            if (source is WasapiLoopbackCaptureSource failed)
            {
                failed.CaptureStopped -= OnSystemCaptureStopped;
            }
            await source.DisposeAsync().ConfigureAwait(false);
            PublishSnapshot(error: $"无法转发本地声音：{exception.Message}");
            Log.Warning(exception, "Failed to start local loopback output routing");
        }
    }

    private async Task EnsureApplicationCaptureAsync(Guid channelId)
    {
        if (applicationCaptures.ContainsKey(channelId) ||
            !applicationSources.TryGetValue(channelId, out LocalApplicationSource? source) ||
            !HasRoutes(channelId))
        {
            return;
        }
        IAudioCaptureSource capture = runtime.CreateProcessLoopback(source.ProcessId);
        if (!applicationCaptures.TryAdd(channelId, capture))
        {
            await capture.DisposeAsync().ConfigureAwait(false);
            return;
        }
        try
        {
            await capture.StartAsync(
                new RoutingFrameSink(this, channelId),
                lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            applicationCaptures.TryRemove(channelId, out _);
            await capture.DisposeAsync().ConfigureAwait(false);
            PublishSnapshot(error: $"无法捕获本机应用“{source.DisplayName}”：{exception.Message}");
            Log.Warning(exception,
                "Failed to start process loopback for local application {ProcessId}",
                source.ProcessId);
        }
    }

    private async Task RestartApplicationCaptureAsync(Guid channelId)
    {
        if (applicationCaptures.TryRemove(channelId, out IAudioCaptureSource? capture))
        {
            await capture.DisposeAsync().ConfigureAwait(false);
        }
        await EnsureApplicationCaptureAsync(channelId).ConfigureAwait(false);
    }

    private void OnSystemCaptureStopped(object? sender, WasapiCaptureStoppedEventArgs args)
    {
        if (args.Exception is not null)
        {
            PublishSnapshot(error: $"本地声音转发已停止：{args.Exception.Message}");
        }
    }

    private bool HasRoutes(Guid channelId) =>
        configuredRoutes.Keys.Any(key => key.ChannelId == channelId);

    private bool IsCurrentConfiguredRoute(OutputRouteKey key, long version) =>
        configuredRoutes.ContainsKey(key) &&
        routeVersions.TryGetValue(key, out long currentVersion) &&
        currentVersion == version;

    private IAudioDevice? FindDevice(string deviceId) => devices.FirstOrDefault(device =>
        string.Equals(device.Id, deviceId, StringComparison.Ordinal));

    private void PublishSnapshot(string? status = null, string? error = null)
    {
        LocalAudioRoutingSnapshot next;
        lock (snapshotGate)
        {
            LocalAudioOutputDeviceSnapshot[] outputs = devices
                .Where(device => !string.Equals(
                    device.Id,
                    primaryDeviceId,
                    StringComparison.Ordinal))
                .Select(device =>
                {
                    AudioOutputEndpointSnapshot? endpoint = endpoints.FirstOrDefault(candidate =>
                        string.Equals(candidate.Device.Id, device.Id, StringComparison.Ordinal));
                    LocalAudioRouteSnapshot[] routes = configuredRoutes
                        .Where(pair => string.Equals(
                            pair.Key.DeviceId, device.Id, StringComparison.Ordinal))
                        .Select(pair => new LocalAudioRouteSnapshot(
                            pair.Key.ChannelId,
                            device.Id,
                            applicationSources.GetValueOrDefault(pair.Key.ChannelId)?.DisplayName ??
                                pair.Value.ChannelName ?? "已保存的音源",
                            device.DisplayName,
                            activeRoutes.ContainsKey(pair.Key)))
                        .ToArray();
                    return new LocalAudioOutputDeviceSnapshot(
                        device.Id,
                        device.DisplayName,
                        endpoint?.VolumePercent ?? 100,
                        endpoint?.IsMuted ?? false,
                        routes);
                })
                .ToArray();
            LocalAudioRoutingSnapshot current = Snapshot;
            next = new LocalAudioRoutingSnapshot(
                outputs,
                configuredRoutes.Values.ToArray(),
                status ?? current.Status,
                error ?? current.ErrorText,
                current.Revision + 1);
            Volatile.Write(ref snapshot, next);
        }
        SnapshotChanged?.Invoke(this, next);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        await lifetime.CancelAsync().ConfigureAwait(false);
        if (systemCapture is not null)
        {
            if (systemCapture is WasapiLoopbackCaptureSource wasapi)
            {
                wasapi.CaptureStopped -= OnSystemCaptureStopped;
            }
            await systemCapture.DisposeAsync().ConfigureAwait(false);
            systemCapture = null;
        }
        foreach ((OutputRouteKey key, SecondaryPlaybackRoute route) in activeRoutes.ToArray())
        {
            if (activeRoutes.TryRemove(key, out _))
            {
                await route.DisposeAsync().ConfigureAwait(false);
            }
        }
        foreach ((Guid channelId, IAudioCaptureSource capture) in applicationCaptures.ToArray())
        {
            if (applicationCaptures.TryRemove(channelId, out _))
            {
                await capture.DisposeAsync().ConfigureAwait(false);
            }
        }
        reconcileGate.Dispose();
        lifetime.Dispose();
    }

    private readonly record struct OutputRouteKey(Guid ChannelId, string DeviceId);
    private sealed record LocalApplicationSource(int ProcessId, string DisplayName, string IdentityKey);

    private sealed class RoutingFrameSink(
        LocalAudioRoutingCoordinator owner,
        Guid channelId) : IAudioFrameSink
    {
        public async ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken)
        {
            byte[] pcm = frame.Data.ToArray();
            PcmGainProcessor.Apply(pcm, Volatile.Read(ref owner.localGain));
            await owner.WriteAsync(channelId, pcm, frame.Timestamp, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
