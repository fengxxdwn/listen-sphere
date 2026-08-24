namespace ListenSphere.Diagnostics;

public enum DiagnosticSeverity
{
    Debug,
    Information,
    Warning,
    Error,
    Critical
}

/// <summary>
/// A structured diagnostic event. Properties must never contain PCM data, pairing
/// secrets, certificates, or unfiltered personally identifying information.
/// </summary>
public sealed record DiagnosticEvent(
    DateTimeOffset Timestamp,
    DiagnosticSeverity Severity,
    string EventName,
    IReadOnlyDictionary<string, object?> Properties);

/// <summary>Exports privacy-filtered diagnostics without audio or secrets.</summary>
public interface IDiagnosticsExporter
{
    ValueTask<string> ExportAsync(string destinationDirectory, CancellationToken cancellationToken);
}

/// <summary>Accepts privacy-filtered structured events for the in-memory diagnostic log.</summary>
public interface IDiagnosticEventSink
{
    void Record(
        DiagnosticSeverity severity,
        string eventName,
        IReadOnlyDictionary<string, object?>? properties = null);
}

public sealed record DiagnosticRuntimeSnapshot(
    DateTimeOffset CapturedAt,
    string ApplicationVersion,
    string OperatingSystem,
    string ProcessArchitecture,
    long UptimeSeconds,
    long DatagramsReceived,
    long InvalidDatagrams,
    long AuthenticationFailures,
    long NetworkConcealmentFrames,
    long EstimatedLostDatagrams,
    long LateDatagrams,
    long NetworkOutputOverflows,
    int NetworkQueueDepth,
    int ActiveAudioSessions,
    int JitterBufferedFrames,
    long MixedFrames,
    long MixerUnderflows,
    long MixerOverflows,
    long ClippedSamples,
    long PlaybackFrames,
    long PlaybackUnderruns,
    long PlaybackOverflows,
    long DriftCorrections,
    int PlaybackBufferedMilliseconds,
    double EstimatedClockDriftPpm);
