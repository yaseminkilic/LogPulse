using LogPulse.Shared.Logging;

namespace LogPulse.Logging;

/// <summary>Persistent store for important logs (SQLite).</summary>
public interface ILogStore
{
    /// <summary>Prepares the table (idempotent).</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Writes a batch of important log records in a single transaction.</summary>
    Task SaveBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken ct = default);

    /// <summary>Returns the most recent N records for the admin panel.</summary>
    Task<IReadOnlyList<LogRecord>> GetRecentAsync(int take = 100, CancellationToken ct = default);

    /// <summary>Filtered query for the admin viewer (newest → oldest).</summary>
    Task<IReadOnlyList<LogRecordDto>> QueryAsync(LogQuery query, CancellationToken ct = default);

    /// <summary>Available categories for the filter dropdown.</summary>
    Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken ct = default);
}
