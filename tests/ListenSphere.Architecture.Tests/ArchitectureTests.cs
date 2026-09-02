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
        XElement contentHost = Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "ContentControl" &&
                       element.Attributes().Any(attribute =>
                           attribute.Name.LocalName == "Name" &&
                           attribute.Value == "DashboardContentHost"));
        XAttribute? content = contentHost.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == "Content");

        Assert.Equal("{Binding Dashboard}", content?.Value);
        Assert.Single(
            contentHost.Descendants(),
            element => element.Name.LocalName == "ControllerDashboardView");
        Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "ControllerDialogHostView");
        Assert.DoesNotContain(
            document.Descendants(),
            element => element.Name.LocalName == "ScrollViewer");
        string mainWindow = File.ReadAllText(Path.Combine(
            repository,
            "apps",
            "ListenSphere.Controller",
            "MainWindow.xaml"));
        Assert.DoesNotContain("欢迎使用聆界", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("删除这台设备", mainWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void ControllerXaml_NetworkBindingsUseNamedPresentationModels()
    {
        string repository = FindRepository();
        string controllerDirectory = Path.Combine(
            repository,
            "apps",
            "ListenSphere.Controller");
        HashSet<string> allowedModels =
        [
            "Transport",
            "RemoteDevices",
            "AudioOutput",
            "LocalRouting",
            "Microphone",
            "RemoteAudio",
            "GroupMixer"
        ];

        string[] bindings = Directory
            .EnumerateFiles(controllerDirectory, "*.xaml", SearchOption.AllDirectories)
            .Select(XDocument.Load)
            .SelectMany(document => document.Descendants())
            .SelectMany(element => element.Attributes())
            .Select(attribute => attribute.Value)
            .Where(value => value.Contains("{Binding Network.", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(bindings);
        foreach (string binding in bindings)
        {
            int start = binding.IndexOf("Network.", StringComparison.Ordinal) + "Network.".Length;
            int end = binding.IndexOfAny(['.', ',', '}'], start);
            string model = end < 0 ? binding[start..] : binding[start..end];
            Assert.Contains(model, allowedModels);
        }
    }

    [Fact]
    public void ControllerNetworkShell_IsSmallAndCardModelsAreSeparate()
    {
        string repository = FindRepository();
        string controllerDirectory = Path.Combine(
            repository,
            "apps",
            "ListenSphere.Controller");
        string shellPath = Path.Combine(controllerDirectory, "ControllerNetworkViewModel.cs");
        string runtimePath = Path.Combine(
            controllerDirectory,
            "Presentation",
            "Network",
            "ControllerNetworkRuntime.cs");
        string itemsDirectory = Path.Combine(
            controllerDirectory,
            "Presentation",
            "Items");

        Assert.True(new FileInfo(shellPath).Length < 30_000);
        string runtime = File.ReadAllText(runtimePath);
        string items = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(itemsDirectory, "*.cs").Select(File.ReadAllText));
        Assert.DoesNotContain("class RemoteChannelItemViewModel", runtime, StringComparison.Ordinal);
        Assert.Contains("class RemoteChannelItemViewModel", items, StringComparison.Ordinal);
        Assert.Contains("class AdditionalOutputDeviceItemViewModel", items, StringComparison.Ordinal);
        Assert.Contains("class AdditionalOutputRouteItemViewModel", items, StringComparison.Ordinal);
        Assert.Contains("class RoutingRuleItemViewModel", items, StringComparison.Ordinal);
        Assert.Contains("class GroupBusItemViewModel", items, StringComparison.Ordinal);
    }

    [Fact]
    public void ControllerDialogViews_AreLoadableXamlComponents()
    {
        string repository = FindRepository();
        string views = Path.Combine(
            repository,
            "apps",
            "ListenSphere.Controller",
            "Views");
        string[] names =
        [
            "ControllerDialogHostView",
            "FirstRunGuideDialogView",
            "DeleteDeviceConfirmationDialogView"
        ];

        foreach (string name in names)
        {
            XDocument document = XDocument.Load(Path.Combine(views, $"{name}.xaml"));
            XElement root = Assert.IsType<XElement>(document.Root);
            Assert.Equal("UserControl", root.Name.LocalName);
            Assert.Contains(
                root.Attributes(),
                attribute => attribute.Name.LocalName == "Class" &&
                             attribute.Value == $"ListenSphere.Controller.Views.{name}");
            Assert.True(File.Exists(Path.Combine(views, $"{name}.xaml.cs")));
        }
    }

    private static string FindRepository()
    {
        string? repository = AppContext.BaseDirectory;
        while (repository is not null &&
               !File.Exists(Path.Combine(repository, "ListenSphere.sln")))
        {
            repository = Directory.GetParent(repository)?.FullName;
        }

        return Assert.IsType<string>(repository);
    }
}
