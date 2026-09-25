using System.Diagnostics;
using System.Xml.Linq;
using Xunit;

namespace ListenSphere.Architecture.Tests;

public sealed class GovernanceTests
{
    private static readonly IReadOnlyDictionary<string, string[]> AllowedCoreReferences =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["ListenSphere.Audio.Abstractions"] = [],
            ["ListenSphere.Audio.Engine"] = ["ListenSphere.Audio.Abstractions"],
            ["ListenSphere.Configuration"] = [],
            ["ListenSphere.Device"] = [],
            ["ListenSphere.Diagnostics"] = [],
            ["ListenSphere.Network"] = ["ListenSphere.Device", "ListenSphere.Protocol"],
            ["ListenSphere.Protocol"] = []
        };

    [Fact]
    public void CoreProjectReferences_FollowTheApprovedDependencyGraph()
    {
        string root = FindRepository();
        foreach (string project in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            string name = Path.GetFileNameWithoutExtension(project);
            string[] dependencies = GetProjectReferences(project);
            if (AllowedCoreReferences.TryGetValue(name, out string[]? allowed))
            {
                Assert.Equal(allowed.Order(StringComparer.Ordinal), dependencies.Order(StringComparer.Ordinal));
                continue;
            }

            Assert.StartsWith("ListenSphere.Windows.", name, StringComparison.Ordinal);
            Assert.DoesNotContain(dependencies, dependency => !AllowedCoreReferences.ContainsKey(dependency));
        }
    }

    [Fact]
    public void ProductApplications_DoNotIntroduceRuntimeTestDependencies()
    {
        string root = FindRepository();
        foreach (string project in Directory.EnumerateFiles(Path.Combine(root, "apps"), "*.csproj", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(GetProjectReferences(project), dependency =>
                dependency.EndsWith(".Tests", StringComparison.Ordinal) ||
                !dependency.StartsWith("ListenSphere.", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void GitIndex_ContainsNoGeneratedOrIdeFiles()
    {
        var process = new ProcessStartInfo("git")
        {
            WorkingDirectory = FindRepository(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        process.ArgumentList.Add("ls-files");
        process.ArgumentList.Add("-z");
        using Process git = Process.Start(process) ?? throw new InvalidOperationException("Cannot start git.");
        string paths = git.StandardOutput.ReadToEnd();
        string error = git.StandardError.ReadToEnd();
        git.WaitForExit();
        Assert.True(git.ExitCode == 0, error);

        string[] forbiddenDirectories = ["bin", "obj", ".vs", ".idea", ".vscode", ".gradle", ".kotlin", "artifacts", "TestResults"];
        string[] forbiddenExtensions = [".apk", ".log", ".user", ".suo"];
        foreach (string path in paths.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            string normalized = path.Replace('\\', '/');
            string[] segments = normalized.Split('/');
            bool generatedDirectory = segments[..^1].Any(segment =>
                forbiddenDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase));
            bool generatedFile = forbiddenExtensions.Any(extension =>
                normalized.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) ||
                normalized.EndsWith("_wpftmp.csproj", StringComparison.OrdinalIgnoreCase);
            Assert.False(generatedDirectory || generatedFile, $"Git tracks generated or IDE file: {normalized}");
        }
    }

    [Fact]
    public void WindowsPortablePackagingScript_UsesCentralVersionAndSafePublishSettings()
    {
        string root = FindRepository();
        string scriptPath = Path.Combine(root, "scripts", "Publish-ListenSphereWindows.ps1");
        Assert.True(File.Exists(scriptPath), $"Packaging script is missing: {scriptPath}");

        string script = File.ReadAllText(scriptPath);
        Assert.Contains("eng/ListenSphere.Version.props", script, StringComparison.Ordinal);
        Assert.Contains("ListenSphereProductVersion", script, StringComparison.Ordinal);
        Assert.DoesNotContain("0.6.0-beta.1", script, StringComparison.Ordinal);
        Assert.Contains("--self-contained', 'true", script, StringComparison.Ordinal);
        Assert.Contains("PublishSingleFile=false", script, StringComparison.Ordinal);
        Assert.Contains("PublishTrimmed=false", script, StringComparison.Ordinal);
        Assert.Contains("ListenSphere-Controller-$Runtime-$version.zip", script, StringComparison.Ordinal);
        Assert.Contains("ListenSphere-Sender-$Runtime-$version.zip", script, StringComparison.Ordinal);
        Assert.Contains("SHA256SUMS.txt", script, StringComparison.Ordinal);

        string gitignore = File.ReadAllText(Path.Combine(root, ".gitignore"));
        Assert.Contains("artifacts/", gitignore, StringComparison.Ordinal);
    }

    private static string[] GetProjectReferences(string project)
    {
        XDocument document = XDocument.Load(project);
        return document.Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFileNameWithoutExtension(path!.Replace('\\', '/')))
            .ToArray();
    }

    private static string FindRepository()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "ListenSphere.sln")))
            directory = Directory.GetParent(directory)?.FullName;
        return Assert.IsType<string>(directory);
    }
}
