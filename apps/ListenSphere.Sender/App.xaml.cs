using System.IO;
using System.Windows;
using ListenSphere.Audio.Abstractions;
using ListenSphere.Configuration;
using ListenSphere.Device;
using ListenSphere.Diagnostics;
using ListenSphere.Network;
using ListenSphere.Windows.Audio;
using ListenSphere.Windows.AudioSessions;
using ListenSphere.Windows.Devices;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace ListenSphere.Sender;

public partial class App : Application
{
    private ServiceProvider? serviceProvider;
    private LocalDeviceIdentity? localIdentity;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ListenSphere",
            "Sender");
        localIdentity = LocalIdentityStore.LoadOrCreate(
            dataDirectory,
            $"{Environment.MachineName} Sender",
            DeviceCapabilities.AudioSend | DeviceCapabilities.RemoteControl);
        var trustStore = new JsonTrustedDeviceStore(
            Path.Combine(dataDirectory, "trusted-devices.json"));
        var settingsStore = new JsonSettingsStore(
            Path.Combine(dataDirectory, "settings.json"));
        var diagnosticService = new DiagnosticArchiveService(
            Path.Combine(dataDirectory, "logs"));
        Log.Logger = new LoggerConfiguration()
            .Enrich.WithProperty("Application", "ListenSphere Sender")
            .WriteTo.Debug()
            .WriteTo.Sink(diagnosticService)
            .CreateLogger();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        serviceProvider = new ServiceCollection()
            .AddSingleton(localIdentity)
            .AddSingleton<ITrustedDeviceStore>(trustStore)
            .AddSingleton<ISettingsStore>(settingsStore)
            .AddSingleton(diagnosticService)
            .AddSingleton<IDiscoveryService, MdnsDiscoveryService>()
            .AddSingleton<ListenSphereControlClient>()
            .AddSingleton<SenderNetworkViewModel>()
            .AddSingleton<IAudioDeviceManager, WasapiAudioDeviceManager>()
            .AddSingleton<IWindowsDeviceNotificationSource, WasapiDeviceNotificationSource>()
            .AddSingleton<IWasapiCaptureSourceFactory, WasapiCaptureSourceFactory>()
            .AddSingleton<IProcessLoopbackCaptureSourceFactory, ProcessLoopbackCaptureSourceFactory>()
            .AddSingleton<IWindowsAudioSessionManager, WasapiAudioSessionManager>()
            .AddSingleton<ITestTonePlayer, TestTonePlayer>()
            .AddSingleton<IDebugWaveRecorder, DebugWaveRecorder>()
            .AddSingleton<SenderViewModel>()
            .AddSingleton<MainWindow>()
            .BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });

        Log.Information("ListenSphere Sender P6 started");
        MainWindow = serviceProvider.GetRequiredService<MainWindow>();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("ListenSphere Sender P6 stopped");
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        Log.CloseAndFlush();
        serviceProvider?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        localIdentity?.Dispose();
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args) =>
        Log.Fatal(args.Exception, "Unhandled Sender dispatcher exception");

    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception exception)
        {
            Log.Fatal(exception, "Unhandled Sender process exception");
        }
        else
        {
            Log.Fatal("Unhandled Sender process exception object");
        }
    }

    private static void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs args)
    {
        Log.Error(args.Exception, "Unobserved Sender task exception");
        args.SetObserved();
    }
}
