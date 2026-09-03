using System.IO;
using System.Collections.ObjectModel;
using System.Text.Json;
using ListenSphere.Configuration;
using ListenSphere.Controller.Services;

namespace ListenSphere.Controller.Presentation;

public sealed class SceneViewModel : ObservableViewModel
{
    private readonly ISceneService sceneService;
    private readonly ISceneSerializationService serializationService;
    private readonly IFileDialogService fileDialogService;
    private readonly LocalSessionsViewModel localSessions;
    private readonly ControllerNetworkViewModel network;
    private readonly Func<Task<bool>> persist;
    private SceneSettings? selectedScene;
    private string newSceneName = string.Empty;
    private string statusText = "保存当前声道、输出设备和主音量，随时一键恢复。";

    public SceneViewModel(
        ISceneService sceneService,
        ISceneSerializationService serializationService,
        IFileDialogService fileDialogService,
        LocalSessionsViewModel localSessions,
        ControllerNetworkViewModel network,
        Func<Task<bool>> persist)
    {
        this.sceneService = sceneService;
        this.serializationService = serializationService;
        this.fileDialogService = fileDialogService;
        this.localSessions = localSessions;
        this.network = network;
        this.persist = persist;
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => SelectedScene is not null);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => SelectedScene is not null);
        RenameCommand = new AsyncRelayCommand(
            RenameAsync,
            () => SelectedScene is not null && !string.IsNullOrWhiteSpace(NewSceneName));
        DuplicateCommand = new AsyncRelayCommand(
            DuplicateAsync,
            () => SelectedScene is not null);
        ImportCommand = new AsyncRelayCommand(ImportAsync);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => SelectedScene is not null);
    }

    public ObservableCollection<SceneSettings> Scenes { get; } = [];
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand ApplyCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand RenameCommand { get; }
    public AsyncRelayCommand DuplicateCommand { get; }
    public AsyncRelayCommand ImportCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }

    public string StatusText
    {
        get => statusText;
        private set => SetField(ref statusText, value);
    }

    public string NewSceneName
    {
        get => newSceneName;
        set
        {
            if (SetField(ref newSceneName, value))
            {
                RenameCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public SceneSettings? SelectedScene
    {
        get => selectedScene;
        set
        {
            if (!SetField(ref selectedScene, value))
            {
                return;
            }

            ApplyCommand.RaiseCanExecuteChanged();
            DeleteCommand.RaiseCanExecuteChanged();
            RenameCommand.RaiseCanExecuteChanged();
            DuplicateCommand.RaiseCanExecuteChanged();
            ExportCommand.RaiseCanExecuteChanged();
        }
    }

    public void Initialize(IEnumerable<SceneSettings> scenes)
    {
        Scenes.Clear();
        foreach (SceneSettings scene in scenes.OrderBy(scene => scene.Name))
        {
            Scenes.Add(scene);
        }
    }

    private async Task SaveAsync()
    {
        string name = string.IsNullOrWhiteSpace(NewSceneName)
            ? $"场景 {DateTime.Now:MM-dd HH:mm}"
            : NewSceneName.Trim();
        SceneSettings scene = sceneService.Save(
            Scenes,
            name,
            localSessions,
            network);
        SelectedScene = scene;
        NewSceneName = string.Empty;
        if (await persist())
        {
            StatusText = $"已保存场景“{scene.Name}”，共 {scene.Channels.Count} 个声道。";
        }
    }

    private async Task ApplyAsync()
    {
        if (SelectedScene is not { } scene)
        {
            return;
        }

        await sceneService.ApplyAsync(scene, localSessions, network);
        if (await persist())
        {
            StatusText = $"已恢复场景“{scene.Name}”。未运行的应用将在下次保存时更新。";
        }
    }

    private async Task DeleteAsync()
    {
        if (SelectedScene is not { } scene)
        {
            return;
        }

        sceneService.Delete(Scenes, scene);
        SelectedScene = null;
        if (await persist())
        {
            StatusText = $"已删除场景“{scene.Name}”。";
        }
    }

    private async Task RenameAsync()
    {
        if (SelectedScene is not { } scene || string.IsNullOrWhiteSpace(NewSceneName))
        {
            return;
        }

        string name = NewSceneName.Trim();
        SceneSettings? renamed = sceneService.Rename(Scenes, scene, name);
        if (renamed is null)
        {
            StatusText = $"已有名为“{name}”的预设。";
            return;
        }

        SelectedScene = renamed;
        NewSceneName = string.Empty;
        if (await persist())
        {
            StatusText = $"预设已重命名为“{name}”。";
        }
    }

    private async Task DuplicateAsync()
    {
        if (SelectedScene is not { } scene)
        {
            return;
        }

        string baseName = string.IsNullOrWhiteSpace(NewSceneName)
            ? $"{scene.Name} 副本"
            : NewSceneName.Trim();
        SceneSettings copy = sceneService.Duplicate(Scenes, scene, baseName);
        SelectedScene = copy;
        NewSceneName = string.Empty;
        if (await persist())
        {
            StatusText = $"已复制预设“{scene.Name}”为“{copy.Name}”。";
        }
    }

    private async Task ImportAsync()
    {
        string? path = fileDialogService.SelectSceneImportFile();
        if (path is null)
        {
            return;
        }

        try
        {
            SceneSettings imported = await serializationService.ReadAsync(
                path,
                CancellationToken.None);
            SceneSettings normalized = sceneService.NormalizeImported(imported, Scenes);
            Scenes.Add(normalized);
            SelectedScene = normalized;
            if (await persist())
            {
                StatusText = $"已导入预设“{normalized.Name}”。";
            }
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or InvalidDataException or NotSupportedException)
        {
            StatusText = $"导入预设失败：{exception.Message}";
        }
    }

    private async Task ExportAsync()
    {
        if (SelectedScene is not { } scene)
        {
            return;
        }

        string suggestedName = $"{sceneService.SanitizeFileName(scene.Name)}.json";
        string? path = fileDialogService.SelectSceneExportFile(suggestedName);
        if (path is null)
        {
            return;
        }

        try
        {
            await serializationService.WriteAsync(path, scene, CancellationToken.None);
            StatusText = $"已导出预设“{scene.Name}”。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText = $"导出预设失败：{exception.Message}";
        }
    }
}