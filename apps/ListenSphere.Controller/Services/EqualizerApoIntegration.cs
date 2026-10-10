using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace ListenSphere.Controller.Services;

public sealed record EqualizerApoIntegrationSnapshot(
    bool IsInstalled,
    bool IsSynchronized,
    string StatusText,
    string StatusColor,
    string ActionText,
    string? InstallationRoot,
    string? ErrorText = null);

public interface IEqualizerApoIntegration
{
    EqualizerApoIntegrationSnapshot Snapshot { get; }
    Task ApplyAsync(bool enabled, IReadOnlyList<float> gains, CancellationToken cancellationToken);
    void OpenManagement();
}

public sealed class EqualizerApoIntegration : IEqualizerApoIntegration
{
    private const string ManagedFileName = "ListenSphere.txt";
    private const string IncludeDirective = $"Include: {ManagedFileName}";
    private const string DownloadUrl = "https://sourceforge.net/projects/equalizerapo/files/latest/download";
    private static readonly string[] DeviceSelectorFileNames =
        ["DeviceSelector.exe", "Configurator.exe"];
    private static readonly TimeSpan[] WriteRetryDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1)
    ];
    private static readonly int[] Frequencies =
        [20, 60, 100, 250, 500, 1_000, 2_000, 4_000, 8_000, 20_000];
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string? requestedInstallationRoot;

    public EqualizerApoIntegration() : this(null)
    {
    }

    internal EqualizerApoIntegration(string? installationRoot)
    {
        requestedInstallationRoot = installationRoot;
        Snapshot = Detect();
    }

    public EqualizerApoIntegrationSnapshot Snapshot { get; private set; }

    public async Task ApplyAsync(
        bool enabled,
        IReadOnlyList<float> gains,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gains);
        if (gains.Count != Frequencies.Length)
        {
            throw new ArgumentException("Equalizer APO 配置需要十个频段。", nameof(gains));
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Snapshot = Detect();
            if (!Snapshot.IsInstalled || Snapshot.InstallationRoot is null)
            {
                return;
            }

            string configDirectory = Path.Combine(Snapshot.InstallationRoot, "config");
            string mainConfigPath = Path.Combine(configDirectory, "config.txt");
            string managedConfigPath = Path.Combine(configDirectory, ManagedFileName);
            string managedConfiguration = CreateManagedConfiguration(enabled, gains);
            await ExecuteWithTransientFileRetryAsync(async () =>
            {
                Directory.CreateDirectory(configDirectory);
                await EnsureIncludeAsync(mainConfigPath, cancellationToken).ConfigureAwait(false);
                await WriteAtomicallyAsync(
                    managedConfigPath,
                    managedConfiguration,
                    cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            Snapshot = Snapshot with
            {
                IsSynchronized = true,
                StatusText = enabled
                    ? "声音效果已生效 · 当前输出设备已由 Equalizer APO 处理"
                    : "Equalizer APO 已连接 · 本地声音 EQ 已旁路",
                StatusColor = "#3DBA79",
                ActionText = "选择生效设备",
                ErrorText = null
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Snapshot = Snapshot with
            {
                IsSynchronized = false,
                StatusText = $"Equalizer APO 配置失败：{exception.Message}",
                StatusColor = "#E05D5D",
                ErrorText = exception.Message
            };
        }
        finally
        {
            gate.Release();
        }
    }

    public void OpenManagement()
    {
        string? deviceSelector = Snapshot.InstallationRoot is null
            ? null
            : FindDeviceSelector(Snapshot.InstallationRoot);
        string target = deviceSelector ?? DownloadUrl;
        var startInfo = new ProcessStartInfo(target) { UseShellExecute = true };
        if (deviceSelector is not null && Snapshot.InstallationRoot is not null)
        {
            // Equalizer APO 1.4.x resolves its bundled Qt plugins relative to the
            // working directory, matching the shortcut created by its installer.
            startInfo.WorkingDirectory = Snapshot.InstallationRoot;
        }

        Process.Start(startInfo);
    }

    internal static string CreateManagedConfiguration(
        bool enabled,
        IReadOnlyList<float> gains)
    {
        var builder = new StringBuilder()
            .AppendLine("# Managed by ListenSphere. Changes made here may be overwritten.")
            .AppendLine($"# Updated: {DateTimeOffset.Now:O}");
        if (!enabled)
        {
            return builder.AppendLine("# Equalizer bypassed in ListenSphere.").ToString();
        }

        float headroom = Math.Max(0f, gains.Max());
        float preamp = headroom > 0f ? -headroom : 0f;
        builder.Append("Preamp: ")
            .Append(preamp.ToString("0.0", CultureInfo.InvariantCulture))
            .AppendLine(" dB");
        for (var index = 0; index < Frequencies.Length; index++)
        {
            float gain = Math.Clamp(gains[index], -20f, 20f);
            builder.Append("Filter ").Append(index + 1).Append(": ON PK Fc ")
                .Append(Frequencies[index]).Append(" Hz Gain ")
                .Append(gain.ToString("0.0", CultureInfo.InvariantCulture))
                .AppendLine(" dB Q 1.35");
        }
        return builder.ToString();
    }

    private EqualizerApoIntegrationSnapshot Detect()
    {
        string? root = FindInstallationRoot();
        if (root is null)
        {
            return new EqualizerApoIntegrationSnapshot(
                false,
                false,
                "未检测到 Equalizer APO · 系统主输出 EQ 尚未生效",
                "#E5A94D",
                "安装 Equalizer APO",
                null);
        }

        return new EqualizerApoIntegrationSnapshot(
            true,
            false,
            "已检测到 Equalizer APO · 正在同步配置…",
            "#8C98AA",
            "选择生效设备",
            root);
    }

    private string? FindInstallationRoot()
    {
        IEnumerable<string?> candidates = requestedInstallationRoot is not null
            ? [requestedInstallationRoot]
            :
            [
                ReadInstallationRootFromRegistry(),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EqualizerAPO"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "EqualizerAPO")
            ];
        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(path =>
                FindDeviceSelector(path) is not null &&
                Directory.Exists(Path.Combine(path, "config")));
    }

    private static string? ReadInstallationRootFromRegistry()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\EqualizerAPO");
            string? installLocation = key?.GetValue("InstallLocation") as string;
            if (!string.IsNullOrWhiteSpace(installLocation))
            {
                return Environment.ExpandEnvironmentVariables(installLocation);
            }

            string? uninstallCommand = key?.GetValue("UninstallString") as string;
            if (string.IsNullOrWhiteSpace(uninstallCommand))
            {
                return null;
            }

            string expanded = Environment.ExpandEnvironmentVariables(uninstallCommand).Trim();
            string executable = expanded.StartsWith('"')
                ? expanded[1..expanded.IndexOf('"', 1)]
                : expanded.Split(' ', 2)[0];
            return Path.GetDirectoryName(executable);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string? FindDeviceSelector(string installationRoot) =>
        DeviceSelectorFileNames
            .Select(fileName => Path.Combine(installationRoot, fileName))
            .FirstOrDefault(File.Exists);

    private static async Task ExecuteWithTransientFileRetryAsync(
        Func<Task> operation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await operation().ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException &&
                attempt < WriteRetryDelays.Length)
            {
                await Task.Delay(WriteRetryDelays[attempt], cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task EnsureIncludeAsync(
        string mainConfigPath,
        CancellationToken cancellationToken)
    {
        string existing = File.Exists(mainConfigPath)
            ? await File.ReadAllTextAsync(mainConfigPath, cancellationToken).ConfigureAwait(false)
            : string.Empty;
        bool included = existing
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line => string.Equals(line, IncludeDirective, StringComparison.OrdinalIgnoreCase));
        if (included)
        {
            return;
        }

        string next = $"# ListenSphere managed EQ{Environment.NewLine}{IncludeDirective}" +
            Environment.NewLine + Environment.NewLine + existing;
        await WriteAtomicallyAsync(mainConfigPath, next, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteAtomicallyAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                content,
                Utf8WithoutBom,
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
