namespace LogPulse.Shared.Logging;

/// <summary>
/// A read-only log row returned to the admin log viewer. Mapped from the server's internal
/// <c>LogRecord</c> (the client never sees the internal type).
/// </summary>
public sealed class LogRecordDto
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Microsoft <c>LogLevel</c> numeric value (Trace=0 … Critical=5).</summary>
    public int Level { get; set; }

    /// <summary>Level name for display ("Warning", "Error" …).</summary>
    public string LevelName { get; set; } = "";

    public string Message { get; set; } = "";
    public string? Exception { get; set; }
    public string? CorrelationId { get; set; }
    public string? Category { get; set; }

    /// <summary>"Client" | "Server" | "Hub".</summary>
    public string Source { get; set; } = "";

    /// <summary>The raw JSON of the enrichment properties (if any).</summary>
    public string? PropertiesJson { get; set; }
}
