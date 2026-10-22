using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ListenSphere.Controller.Services;

public sealed record AvailableUpdate(
    string Version,
    string ReleaseUrl,
    Uri DownloadUrl,
    string FileName,
    long Size,
    string Sha256);

public sealed class GitHubUpdateService
{
    private static readonly HttpClient Client = CreateClient();
    private const string ReleasesUrl =
        "https://api.github.com/repos/fengxxdwn/listen-sphere/releases?per_page=20";
    private static readonly Regex VersionPattern = new(
        @"^v?(\d+)\.(\d+)\.(\d+)(?:-(beta|rc)\.(\d+))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public string CurrentVersion { get; } =
        typeof(GitHubUpdateService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "0.0.0";

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await Client.GetAsync(
            ReleasesUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        string current = CurrentVersion;
        bool includePrerelease = VersionPattern.Match(current).Groups[4].Success;
        AvailableUpdate? latest = null;
        foreach (JsonElement release in document.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean() ||
                (!includePrerelease && release.GetProperty("prerelease").GetBoolean()))
            {
                continue;
            }

            string tag = release.GetProperty("tag_name").GetString() ?? string.Empty;
            string version = tag.TrimStart('v', 'V');
            if (CompareVersions(version, current) <= 0 ||
                (latest is not null && CompareVersions(version, latest.Version) <= 0))
            {
                continue;
            }

            string fileName = $"ListenSphere-Setup-{version}-win-x64.exe";
            foreach (JsonElement asset in release.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != fileName ||
                    asset.GetProperty("state").GetString() != "uploaded")
                {
                    continue;
                }

                string digest = asset.TryGetProperty("digest", out JsonElement digestElement)
                    ? digestElement.GetString() ?? string.Empty : string.Empty;
                string address = asset.GetProperty("browser_download_url").GetString() ?? string.Empty;
                if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
                    !Regex.IsMatch(digest[7..], "^[a-fA-F0-9]{64}$") ||
                    !Uri.TryCreate(address, UriKind.Absolute, out Uri? downloadUrl) ||
                    downloadUrl.Scheme != Uri.UriSchemeHttps ||
                    !string.Equals(downloadUrl.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                    !downloadUrl.AbsolutePath.StartsWith(
                        "/fengxxdwn/listen-sphere/releases/download/", StringComparison.Ordinal))
                {
                    continue;
                }

                latest = new AvailableUpdate(
                    version,
                    release.GetProperty("html_url").GetString() ?? string.Empty,
                    downloadUrl,
                    fileName,
                    asset.GetProperty("size").GetInt64(),
                    digest[7..].ToLowerInvariant());
                break;
            }
        }

        return latest;
    }

    public async Task<string> DownloadAsync(
        AvailableUpdate update,
        IProgress<int> progress,
        CancellationToken cancellationToken)
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ListenSphere", "Controller", "Updates", update.Version);
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, update.FileName);
        if (File.Exists(destination) && await VerifyAsync(destination, update, cancellationToken))
        {
            progress.Report(100);
            return destination;
        }

        string temporary = Path.Combine(directory, $"{Guid.NewGuid():N}.download");
        try
        {
            using HttpResponseMessage response = await Client.GetAsync(
                update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != update.Size)
            {
                throw new InvalidDataException("GitHub 更新包大小与发布信息不一致。");
            }

            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (FileStream target = new(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous))
            {
                byte[] buffer = new byte[81920];
                long downloaded = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    downloaded += count;
                    if (downloaded > update.Size)
                    {
                        throw new InvalidDataException("更新包超出 GitHub 声明的大小。");
                    }
                    progress.Report((int)Math.Min(99, downloaded * 100 / Math.Max(1, update.Size)));
                }
            }

            if (!await VerifyAsync(temporary, update, cancellationToken))
            {
                throw new InvalidDataException("更新包大小或 SHA-256 校验失败。");
            }
            File.Move(temporary, destination, true);
            progress.Report(100);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task<bool> VerifyAsync(
        string path, AvailableUpdate update, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length != update.Size) return false;
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return string.Equals(Convert.ToHexStringLower(hash), update.Sha256, StringComparison.Ordinal);
    }

    internal static int CompareVersions(string left, string right)
    {
        Match a = VersionPattern.Match(left);
        Match b = VersionPattern.Match(right);
        if (!a.Success || !b.Success) return 0;
        for (int i = 1; i <= 3; i++)
        {
            int difference = int.Parse(a.Groups[i].Value).CompareTo(int.Parse(b.Groups[i].Value));
            if (difference != 0) return difference;
        }
        if (!a.Groups[4].Success) return b.Groups[4].Success ? 1 : 0;
        if (!b.Groups[4].Success) return -1;
        int rankA = a.Groups[4].Value.Equals("rc", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        int rankB = b.Groups[4].Value.Equals("rc", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        return rankA != rankB ? rankA.CompareTo(rankB) :
            int.Parse(a.Groups[5].Value).CompareTo(int.Parse(b.Groups[5].Value));
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ListenSphere", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
