using System.Diagnostics;
using System.IO;
using System.Windows;
using ListenSphere.Controller.Services;
using Serilog;

namespace ListenSphere.Controller.Presentation;

public sealed class SoftwareUpdateViewModel : ObservableViewModel
{
    private readonly GitHubUpdateService service;
    private readonly Action persistSettings;
    private AvailableUpdate? available;
    private bool checkOnStartup = true;
    private bool busy;
    private int downloadPercent;
    private string statusText = "点击“检查更新”以查询 GitHub Releases。";

    public SoftwareUpdateViewModel(GitHubUpdateService service, Action persistSettings)
    {
        this.service = service;
        this.persistSettings = persistSettings;
        CheckCommand = new AsyncRelayCommand(CheckAsync, () => !Busy);
        DownloadAndInstallCommand = new AsyncRelayCommand(DownloadAndInstallAsync,
            () => !Busy && available is not null);
    }

    public event EventHandler? InstallLaunched;
    public AsyncRelayCommand CheckCommand { get; }
    public AsyncRelayCommand DownloadAndInstallCommand { get; }
    public string CurrentVersion => service.CurrentVersion;
    public string LatestVersion => available?.Version ?? "尚未发现新版本";
    public string ReleaseUrl => available?.ReleaseUrl ?? string.Empty;
    public bool CheckOnStartup
    {
        get => checkOnStartup;
        set
        {
            if (SetField(ref checkOnStartup, value)) persistSettings();
        }
    }
    public bool Busy
    {
        get => busy;
        private set
        {
            if (SetField(ref busy, value))
            {
                CheckCommand.RaiseCanExecuteChanged();
                DownloadAndInstallCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public int DownloadPercent
    {
        get => downloadPercent;
        private set => SetField(ref downloadPercent, value);
    }
    public string StatusText
    {
        get => statusText;
        private set => SetField(ref statusText, value);
    }

    public void CheckOnStartupIfEnabled()
    {
        if (CheckOnStartup) CheckCommand.Execute(null);
    }

    public void Initialize(bool checkOnStartup)
    {
        if (this.checkOnStartup != checkOnStartup)
        {
            this.checkOnStartup = checkOnStartup;
            OnPropertyChanged(nameof(CheckOnStartup));
        }
    }

    private async Task CheckAsync()
    {
        Busy = true;
        available = null;
        OnPropertyChanged(nameof(LatestVersion));
        OnPropertyChanged(nameof(ReleaseUrl));
        StatusText = "正在从 GitHub 检查更新…";
        try
        {
            available = await service.CheckAsync(CancellationToken.None);
            OnPropertyChanged(nameof(LatestVersion));
            OnPropertyChanged(nameof(ReleaseUrl));
            StatusText = available is null
                ? $"当前已是最新版本（{CurrentVersion}）。"
                : $"发现新版本 {available.Version}，可下载并安装。";
        }
        catch (Exception exception)
        {
            StatusText = $"检查更新失败：{exception.Message}";
            Log.Warning(exception, "Failed to check GitHub release updates");
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task DownloadAndInstallAsync()
    {
        if (available is not { } update) return;
        Busy = true;
        DownloadPercent = 0;
        StatusText = $"正在下载 {update.Version}…";
        try
        {
            string installer = await service.DownloadAsync(update,
                new Progress<int>(value => DownloadPercent = value), CancellationToken.None);
            StatusText = "下载完成，SHA-256 校验通过。";
            MessageBoxResult confirmation = MessageBox.Show(
                $"即将运行 {update.Version} 安装程序。安装时聆界会退出，当前设置已保存。是否继续？",
                "聆界更新", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirmation != MessageBoxResult.Yes) return;
            Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = installer,
                WorkingDirectory = Path.GetDirectoryName(installer)!,
                UseShellExecute = true
            });
            if (process is null) throw new InvalidOperationException("无法启动安装程序。");
            InstallLaunched?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            StatusText = $"下载安装失败：{exception.Message}";
            Log.Warning(exception, "Failed to download or start ListenSphere update");
        }
        finally
        {
            Busy = false;
        }
    }
}
