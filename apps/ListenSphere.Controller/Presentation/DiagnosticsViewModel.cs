using System.IO;
using System.Diagnostics;
using ListenSphere.Diagnostics;
using Serilog;

namespace ListenSphere.Controller.Presentation;

public sealed class DiagnosticsViewModel : ObservableViewModel
{
    private readonly IDiagnosticsExporter exporter;
    private readonly IDiagnosticEventSink events;
    private string statusText =
        "诊断包不包含音频、会话密钥、证书或可信设备记录。";

    public DiagnosticsViewModel(
        IDiagnosticsExporter exporter,
        IDiagnosticEventSink events)
    {
        this.exporter = exporter;
        this.events = events;
        ExportCommand = new AsyncRelayCommand(ExportAsync);
    }

    public AsyncRelayCommand ExportCommand { get; }

    public string StatusText
    {
        get => statusText;
        private set => SetField(ref statusText, value);
    }

    private async Task ExportAsync()
    {
        try
        {
            string destination = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "ListenSphere Diagnostics");
            events.Record(
                DiagnosticSeverity.Information,
                "diagnostics.export.requested");
            string archive = await exporter.ExportAsync(
                destination,
                CancellationToken.None);
            StatusText = $"诊断包已导出：{archive}";
            Process.Start(new ProcessStartInfo
            {
                FileName = destination,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            StatusText = $"导出诊断失败：{exception.Message}";
            Log.Error(exception, "Failed to export diagnostics");
        }
    }
}