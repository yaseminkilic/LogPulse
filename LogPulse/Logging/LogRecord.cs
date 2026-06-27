namespace LogPulse.Logging;

/// <summary>
/// The single normalized log record within the server. Both batches coming from the client
/// (HTTP ingest) and the server middleware convert to the same shape → a single pipeline processes them.
/// </summary>
public sealed record LogRecord(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Message,
    string? Exception,
    string? CorrelationId,
    string? Category,
    string Source,
    string? PropertiesJson);
