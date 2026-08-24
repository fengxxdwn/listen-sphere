using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using Serilog.Core;
using Serilog.Events;

namespace ListenSphere.Diagnostics;

/// <summary>
/// Maintains a bounded privacy-safe event log and exports it with runtime statistics.
/// </summary>
public sealed class DiagnosticArchiveService :
    IDiagnosticsExporter,
    IDiagnosticEventSink,
    ILogEventSink,
    IDisposable
{
    private const int EventCapacity = 512;
    private const long LogFileLimit = 5 * 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly string[] ForbiddenPropertyTerms =
        ["pcm", "payload", "key", "secret", "certificate", "fingerprint"];
    private readonly ConcurrentQueue<DiagnosticEvent> events = new();
    private readonly object fileGate = new();
    private readonly string? logDirectory;
    private StreamWriter? logWriter;
    private DateOnly logDate;
    private int logSequence;
    private DiagnosticRuntimeSnapshot? runtimeSnapshot;
    private int eventCount;
    private bool disposed;

    public DiagnosticArchiveService(string? logDirectory = null)
    {
        this.logDirectory = logDirectory;
    }

    public void Record(
        DiagnosticSeverity severity,
        string eventName,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        AddEvent(new DiagnosticEvent(
            DateTimeOffset.UtcNow,
            severity,
            eventName,
            FilterProperties(properties)));
    }

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        var properties = logEvent.Properties.ToDictionary(
            property => property.Key,
            property => (object?)property.Value.ToString());
        properties["messageTemplate"] = logEvent.MessageTemplate.Text;
        if (logEvent.Exception is not null)
        {
            properties["exceptionType"] = logEvent.Exception.GetType().Name;
        }

        AddEvent(new DiagnosticEvent(
            logEvent.Timestamp,
            MapSeverity(logEvent.Level),
            "application.log",
            FilterProperties(properties)));
    }

    private void AddEvent(DiagnosticEvent diagnosticEvent)
    {
        events.Enqueue(diagnosticEvent);
        int count = Interlocked.Increment(ref eventCount);
        while (count > EventCapacity && events.TryDequeue(out _))
        {
            count = Interlocked.Decrement(ref eventCount);
        }

        Persist(diagnosticEvent);
    }

    public void UpdateRuntimeSnapshot(DiagnosticRuntimeSnapshot snapshot) =>
        Volatile.Write(ref runtimeSnapshot, snapshot);

    public async ValueTask<string> ExportAsync(
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        string archivePath = Path.Combine(
            destinationDirectory,
            $"ListenSphere-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        await using FileStream stream = new(
            archivePath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            8192,
            FileOptions.Asynchronous);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        await WriteJsonEntryAsync(
            archive,
            "runtime.json",
            Volatile.Read(ref runtimeSnapshot),
            cancellationToken).ConfigureAwait(false);
        await WriteJsonEntryAsync(
            archive,
            "events.json",
            events.ToArray(),
            cancellationToken).ConfigureAwait(false);
        AddLogEntries(archive);
        ZipArchiveEntry notice = archive.CreateEntry("privacy.txt", CompressionLevel.Fastest);
        await using Stream noticeStream = notice.Open();
        await using var writer = new StreamWriter(noticeStream);
        await writer.WriteAsync(
            "ListenSphere diagnostics never include PCM audio, session keys, certificates, " +
            "certificate fingerprints, or trusted-device records.").ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        return archivePath;
    }

    public void Dispose()
    {
        lock (fileGate)
        {
            if (disposed)
            {
                return;
            }

            logWriter?.Dispose();
            logWriter = null;
            disposed = true;
        }
    }

    private void Persist(DiagnosticEvent diagnosticEvent)
    {
        if (string.IsNullOrWhiteSpace(logDirectory))
        {
            return;
        }

        try
        {
            lock (fileGate)
            {
                if (disposed)
                {
                    return;
                }

                DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
                if (logWriter is null || today != logDate ||
                    logWriter.BaseStream.Length >= LogFileLimit)
                {
                    logWriter?.Dispose();
                    if (today != logDate)
                    {
                        logSequence = 0;
                    }
                    else
                    {
                        logSequence++;
                    }

                    logDate = today;
                    Directory.CreateDirectory(logDirectory);
                    string path = Path.Combine(
                        logDirectory,
                        $"events-{today:yyyyMMdd}-{logSequence:D2}.jsonl");
                    logWriter = new StreamWriter(new FileStream(
                        path,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.Read,
                        4096,
                        FileOptions.SequentialScan))
                    {
                        AutoFlush = true
                    };
                }

                logWriter.WriteLine(JsonSerializer.Serialize(diagnosticEvent, SerializerOptions));
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceError(
                "ListenSphere persistent diagnostic logging failed: {0}",
                exception.Message);
        }
    }

    private void AddLogEntries(ZipArchive archive)
    {
        if (string.IsNullOrWhiteSpace(logDirectory) || !Directory.Exists(logDirectory))
        {
            return;
        }

        lock (fileGate)
        {
            logWriter?.Flush();
            foreach (string path in Directory
                .EnumerateFiles(logDirectory, "*.jsonl")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(3))
            {
                ZipArchiveEntry entry = archive.CreateEntry(
                    $"logs/{Path.GetFileName(path)}",
                    CompressionLevel.Fastest);
                using var source = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using Stream destination = entry.Open();
                source.CopyTo(destination);
            }
        }
    }

    private static IReadOnlyDictionary<string, object?> FilterProperties(
        IReadOnlyDictionary<string, object?>? properties) =>
        (properties ?? new Dictionary<string, object?>())
        .Where(property => !ForbiddenPropertyTerms.Any(term =>
            property.Key.Contains(term, StringComparison.OrdinalIgnoreCase)))
        .ToDictionary(property => property.Key, property => property.Value);

    private static DiagnosticSeverity MapSeverity(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose or LogEventLevel.Debug => DiagnosticSeverity.Debug,
        LogEventLevel.Information => DiagnosticSeverity.Information,
        LogEventLevel.Warning => DiagnosticSeverity.Warning,
        LogEventLevel.Error => DiagnosticSeverity.Error,
        _ => DiagnosticSeverity.Critical
    };

    private static async Task WriteJsonEntryAsync<T>(
        ZipArchive archive,
        string name,
        T value,
        CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        await using Stream entryStream = entry.Open();
        await JsonSerializer.SerializeAsync(
            entryStream,
            value,
            SerializerOptions,
            cancellationToken).ConfigureAwait(false);
    }
}
