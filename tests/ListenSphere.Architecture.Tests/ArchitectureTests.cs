using System.Reflection;
using ListenSphere.Audio.Abstractions;
using ListenSphere.Audio.Engine;
using ListenSphere.Configuration;
using ListenSphere.Device;
using ListenSphere.Diagnostics;
using ListenSphere.Network;
using ListenSphere.Protocol;
using Xunit;

namespace ListenSphere.Architecture.Tests;

public sealed class ArchitectureTests
{
    private static readonly Assembly[] CoreAssemblies =
    [
        typeof(AudioFormat).Assembly,
        typeof(MixingOptions).Assembly,
        typeof(ListenSphereSettings).Assembly,
        typeof(DeviceDescriptor).Assembly,
        typeof(DiagnosticEvent).Assembly,
        typeof(IDiscoveryService).Assembly,
        typeof(AudioPacketHeader).Assembly
    ];

    [Fact]
    public void PlatformNeutralCore_DoesNotReferenceWindowsUiOrAudioAdapters()
    {
        string[] forbiddenPrefixes =
        [
            "NAudio",
            "PresentationCore",
            "PresentationFramework",
            "System.Windows",
            "WindowsBase",
            "ListenSphere.Controller",
            "ListenSphere.Sender",
            "ListenSphere.Windows"
        ];

        foreach (var assembly in CoreAssemblies.Distinct())
        {
            var references = assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty);
            foreach (var reference in references)
            {
                Assert.DoesNotContain(
                    forbiddenPrefixes,
                    prefix => reference.StartsWith(prefix, StringComparison.Ordinal));
            }
        }
    }
}
