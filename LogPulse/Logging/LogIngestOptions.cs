using LogPulse.Shared.Logging;

namespace LogPulse.Logging;

/// <summary>
/// Persistence (SQLite) policy for the server logging pipeline. Bound from appsettings.
/// </summary>
public sealed class LogIngestOptions
{
    public const string SectionName = "LogIngest";

    /// <summary>Every log at this level and above is written to SQLite. Default: Warning.</summary>
    public LogLevel PersistMinimumLevel { get; set; } = LogLevel.Warning;

    /// <summary>
    /// Categories considered "always persist" regardless of level.
    /// User definition: critical errors, data access errors, hub connection loss.
    /// </summary>
    public string[] AlwaysPersistCategories { get; set; } =
    {
        LogCategories.Critical,
        LogCategories.DataAccess,
        LogCategories.HubConnection
    };

    /// <summary>Upper bound of the in-memory queue; if exceeded, the oldest record is dropped (the UI is not blocked).</summary>
    public int QueueCapacity { get; set; } = 10_000;

    /// <summary>Maximum number of records processed in a single transaction when writing to SQLite.</summary>
    public int PersistBatchSize { get; set; } = 100;
}
