using System.IO;
using System.Text.Json;
using ListenSphere.Configuration;
using Microsoft.Win32;

namespace ListenSphere.Controller.Services;

public interface IFileDialogService
{
    string? SelectSceneImportFile();
    string? SelectSceneExportFile(string suggestedFileName);
}

public sealed class WpfFileDialogService : IFileDialogService
{
    public string? SelectSceneImportFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入聆界调音预设",
            Filter = "聆界预设 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? SelectSceneExportFile(string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出聆界调音预设",
            Filter = "聆界预设 (*.json)|*.json",
            FileName = suggestedFileName,
            AddExtension = true,
            DefaultExt = ".json"
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}

public interface ISceneSerializationService
{
    Task<SceneSettings> ReadAsync(string path, CancellationToken cancellationToken);
    Task WriteAsync(string path, SceneSettings scene, CancellationToken cancellationToken);
}

public sealed class SceneSerializationService : ISceneSerializationService
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public async Task<SceneSettings> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SceneSettings>(
            stream,
            Options,
            cancellationToken) ?? throw new InvalidDataException("预设内容为空。");
    }

    public async Task WriteAsync(
        string path,
        SceneSettings scene,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(
            stream,
            scene,
            Options,
            cancellationToken);
    }
}