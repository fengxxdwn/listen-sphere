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

    [Fact]
    public void WindowsInstaller_IsStableAndAvoidsProhibitedSystemChanges()
    {
        string root = FindRepository();
        string installerPath = Path.Combine(root, "packaging", "windows", "ListenSphere.iss");
        string buildScriptPath = Path.Combine(root, "scripts", "Build-ListenSphereInstaller.ps1");
        Assert.True(File.Exists(installerPath), $"Installer script is missing: {installerPath}");
        Assert.True(File.Exists(buildScriptPath), $"Installer build script is missing: {buildScriptPath}");

        string installer = File.ReadAllText(installerPath);
        Assert.Matches(@"AppId=\{\{[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\}", installer);
        Assert.DoesNotContain("0.6.0-beta.1", installer, StringComparison.Ordinal);
        Assert.Contains("SetupIconFile={#SourceRoot}\\assets\\branding\\windows\\ListenSphere.ico", installer, StringComparison.Ordinal);
        Assert.Contains("CloseApplications=yes", installer, StringComparison.Ordinal);
        Assert.Contains("RestartApplications=no", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalAppData", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("netsh", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("firewall", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("taskkill", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[UninstallDelete]", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("service", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("driver", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\CurrentVersion\\Run", installer, StringComparison.OrdinalIgnoreCase);

        string buildScript = File.ReadAllText(buildScriptPath);
        Assert.Contains("eng/ListenSphere.Version.props", buildScript, StringComparison.Ordinal);
        Assert.Contains("ListenSphereProductVersion", buildScript, StringComparison.Ordinal);
        Assert.Contains("ListenSphereVersionPrefix", buildScript, StringComparison.Ordinal);
        Assert.Contains("FileVersion", buildScript, StringComparison.Ordinal);
        Assert.Contains("/DProductVersion=$productVersion", buildScript, StringComparison.Ordinal);
        Assert.Contains("/DNumericVersion=$numericVersion", buildScript, StringComparison.Ordinal);
        Assert.DoesNotContain("0.6.0-beta.1", buildScript, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidPackaging_UsesCentralVersionAndSafeSigningInputs()
    {
        string root = FindRepository();
        string scriptPath = Path.Combine(root, "scripts", "Build-ListenSphereAndroid.ps1");
        string gradlePath = Path.Combine(
            root, "apps", "ListenSphere.Mobile", "app", "build.gradle.kts");
        Assert.True(File.Exists(scriptPath), $"Android packaging script is missing: {scriptPath}");

        string script = File.ReadAllText(scriptPath);
        string gradle = File.ReadAllText(gradlePath);
        foreach (string environmentName in new[]
        {
            "LISTENSPHERE_ANDROID_KEYSTORE_PATH",
            "LISTENSPHERE_ANDROID_KEYSTORE_PASSWORD",
            "LISTENSPHERE_ANDROID_KEY_ALIAS",
            "LISTENSPHERE_ANDROID_KEY_PASSWORD"
        })
        {
            Assert.Contains(environmentName, script, StringComparison.Ordinal);
            Assert.Contains(environmentName, gradle, StringComparison.Ordinal);
        }

        Assert.Contains("eng/ListenSphere.Version.props", script, StringComparison.Ordinal);
        Assert.Contains("ListenSphereProductVersion", script, StringComparison.Ordinal);
        Assert.Contains("ListenSphereAndroidVersionCode", script, StringComparison.Ordinal);
        Assert.Contains("ListenSphere-Mobile-release-unsigned-$versionName.apk", script, StringComparison.Ordinal);
        Assert.DoesNotContain("0.6.0-beta.1", script, StringComparison.Ordinal);
        Assert.DoesNotContain("0.6.0-beta.1", gradle, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"versionName\s*=\s*""", gradle);
        Assert.DoesNotMatch(@"versionCode\s*=\s*26\b", gradle);
        Assert.DoesNotMatch(@"(?i)(storePassword|keyPassword)\s*=\s*""[^""]+""", gradle);
        Assert.Contains("applicationId = \"io.listensphere.mobile\"", gradle, StringComparison.Ordinal);
        Assert.Contains("isMinifyEnabled = false", gradle, StringComparison.Ordinal);

        string gitignore = File.ReadAllText(Path.Combine(root, ".gitignore"));
        Assert.Contains("*.jks", gitignore, StringComparison.Ordinal);
        Assert.Contains("*.keystore", gitignore, StringComparison.Ordinal);
        Assert.Contains("keystore.properties", gitignore, StringComparison.Ordinal);
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
