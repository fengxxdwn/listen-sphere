using System.Reflection;
using System.Xml.Linq;
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

    [Fact]
    public void ControllerXaml_DoesNotApplyButtonOnlyStyleToToggleButtons()
    {
        string? repository = AppContext.BaseDirectory;
        while (repository is not null &&
               !File.Exists(Path.Combine(repository, "ListenSphere.sln")))
        {
            repository = Directory.GetParent(repository)?.FullName;
        }

        Assert.NotNull(repository);
        string controllerDirectory = Path.Combine(
            repository,
            "apps",
            "ListenSphere.Controller");
        IEnumerable<XElement> toggleButtons = Directory
            .EnumerateFiles(controllerDirectory, "*.xaml", SearchOption.AllDirectories)
            .Select(XDocument.Load)
            .SelectMany(document => document.Descendants())
            .Where(element => element.Name.LocalName == "ToggleButton");

        Assert.DoesNotContain(toggleButtons, element =>
            string.Equals(
                element.Attribute("Style")?.Value,
                "{StaticResource IconButton}",
                StringComparison.Ordinal));
    }

    [Fact]
    public void ControllerMainWindow_DelegatesDashboardToPageViewModel()
    {
        string? repository = AppContext.BaseDirectory;
        while (repository is not null &&
               !File.Exists(Path.Combine(repository, "ListenSphere.sln")))
        {
            repository = Directory.GetParent(repository)?.FullName;
        }

        Assert.NotNull(repository);
        XDocument document = XDocument.Load(Path.Combine(
            repository,
            "apps",
            "ListenSphere.Controller",
            "MainWindow.xaml"));
        XElement dashboard = Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "ControllerDashboardView");
        XAttribute? dataContext = dashboard.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == "DataContext");

        Assert.Equal("{Binding Dashboard}", dataContext?.Value);
        Assert.DoesNotContain(
            document.Descendants(),
            element => element.Name.LocalName == "ScrollViewer");
    }
}
