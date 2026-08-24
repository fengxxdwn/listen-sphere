using ListenSphere.Windows.Audio;
using ListenSphere.Windows.Devices;
using NAudio.CoreAudioApi;
using Xunit;

namespace ListenSphere.Windows.TechnicalTests;

public sealed class WindowsAdapterSmokeTests
{
    [Fact]
    public void StableNAudioWasapiTypes_AreResolvable()
    {
        Assert.Equal("WasapiOut", WasapiCapability.PlaybackAdapterType.Name);
        Assert.Equal("WasapiLoopbackCapture", WasapiCapability.LoopbackCaptureAdapterType.Name);
        Assert.True(Environment.Is64BitProcess);
    }

    [Fact]
    public async Task DeviceManager_EnumeratesStableUniqueEndpointIds()
    {
        var manager = new WasapiAudioDeviceManager();

        var devices = await manager.GetPlaybackDevicesAsync(CancellationToken.None);

        Assert.Equal(
            devices.Count,
            devices.Select(device => device.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.InRange(devices.Count(device => device.IsDefault), 0, 1);
        using var enumerator = new MMDeviceEnumerator();
        if (enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Console))
        {
            using var systemDefault =
                enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            Assert.Equal(
                systemDefault.ID,
                Assert.Single(devices, device => device.IsDefault).Id);
        }

        Assert.All(devices, device =>
        {
            Assert.False(string.IsNullOrWhiteSpace(device.Id));
            Assert.False(string.IsNullOrWhiteSpace(device.DisplayName));
        });
    }

    [Fact]
    public void WindowsAudioDevice_LabelsTheDefaultEndpoint()
    {
        var device = new WindowsAudioDevice("id", "Headphones", true);

        Assert.Equal("Headphones（默认）", device.DisplayLabel);
    }

    [Fact]
    public void DeviceNotificationSource_RegistersAndUnregisters()
    {
        using var source = new WasapiDeviceNotificationSource();
        Assert.IsAssignableFrom<IWindowsDeviceNotificationSource>(source);
    }
}
