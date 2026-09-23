namespace Keepmark.Core.Logging;

// Verbosity levels for Settings → Log. Persisted choice filters what is stored/shown.
public enum XbsLogLevel
{
    Error = 0,
    Warning = 1,
    Info = 2,
    Debug = 3,
    Verbose = 4
}

public sealed record LogEntry(DateTimeOffset Timestamp, XbsLogLevel Level, string Tag, string Message);

public interface IXbsLogger
{
    void Log(XbsLogLevel level, string tag, string message);
    IReadOnlyList<LogEntry> Entries { get; }
    void Clear();

    XbsLogLevel MinLevel { get; set; }
}

public static class LoggerExtensions
{
    public static void Error(this IXbsLogger log, string tag, string message) => log.Log(XbsLogLevel.Error, tag, message);
    public static void Warn(this IXbsLogger log, string tag, string message) => log.Log(XbsLogLevel.Warning, tag, message);
    public static void Info(this IXbsLogger log, string tag, string message) => log.Log(XbsLogLevel.Info, tag, message);
    public static void Debug(this IXbsLogger log, string tag, string message) => log.Log(XbsLogLevel.Debug, tag, message);
    public static void Verbose(this IXbsLogger log, string tag, string message) => log.Log(XbsLogLevel.Verbose, tag, message);
}

/// <summary>
/// Central redaction gate. Sync IDs are truncated, passwords/keys/blobs never logged.
/// Route all log arguments through here.
/// </summary>
public static class LogSanitizer
{
    public static string SyncId(string? syncId)
    {
        if (string.IsNullOrEmpty(syncId) || syncId.Length <= 8)
        {
            return "…";
        }

        return $"{syncId[..4]}…{syncId[^2..]}";
    }

    public static string PathFor(string template, string? syncId)
    {
        if (string.IsNullOrEmpty(syncId))
        {
            return template;
        }

        // Redact both "{id}" placeholders and raw ID occurrences (IDs travel in URL paths).
        return template
            .Replace("{id}", SyncId(syncId), StringComparison.Ordinal)
            .Replace(syncId, SyncId(syncId), StringComparison.Ordinal);
    }
}

/// <summary>Bounded in-memory ring buffer. The MAUI layer persists entries into the encrypted DB.</summary>
public sealed class InMemoryLogger(int capacity = 1000) : IXbsLogger
{
    private readonly Queue<LogEntry> _entries = new();
    private readonly object _gate = new();

    public XbsLogLevel MinLevel { get; set; } = XbsLogLevel.Info;

    public void Log(XbsLogLevel level, string tag, string message)
    {
        if (level > MinLevel)
        {
            return;
        }

        lock (_gate)
        {
            _entries.Enqueue(new LogEntry(DateTimeOffset.UtcNow, level, tag, message));
            while (_entries.Count > capacity)
            {
                _entries.Dequeue();
            }
        }
    }

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToList();
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }
}
