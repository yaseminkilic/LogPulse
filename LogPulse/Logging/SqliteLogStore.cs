using LogPulse.Shared.Logging;
using Microsoft.Data.Sqlite;

namespace LogPulse.Logging;

/// <summary>
/// The SQLite implementation of <see cref="ILogStore"/>. With WAL mode it is resilient to
/// concurrent reads/writes. The connection string comes from appsettings (default: a local file).
/// </summary>
public sealed class SqliteLogStore : ILogStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteLogStore> _logger;

    public SqliteLogStore(IConfiguration config, ILogger<SqliteLogStore> logger)
    {
        _connectionString = config.GetConnectionString("Logs")
                            ?? "Data Source=logs.db";
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Logs (
                Id            INTEGER PRIMARY KEY AUTOINCREMENT,
                Timestamp     TEXT    NOT NULL,
                Level         INTEGER NOT NULL,
                Message       TEXT    NOT NULL,
                Exception     TEXT    NULL,
                CorrelationId TEXT    NULL,
                Category      TEXT    NULL,
                Source        TEXT    NOT NULL,
                Properties    TEXT    NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Logs_Timestamp ON Logs(Timestamp);
            CREATE INDEX IF NOT EXISTS IX_Logs_CorrelationId ON Logs(CorrelationId);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken ct = default)
    {
        if (records.Count == 0) return;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO Logs (Timestamp, Level, Message, Exception, CorrelationId, Category, Source, Properties)
            VALUES ($ts, $level, $msg, $ex, $corr, $cat, $src, $props);
            """;

        var pTs = cmd.CreateParameter(); pTs.ParameterName = "$ts"; cmd.Parameters.Add(pTs);
        var pLevel = cmd.CreateParameter(); pLevel.ParameterName = "$level"; cmd.Parameters.Add(pLevel);
        var pMsg = cmd.CreateParameter(); pMsg.ParameterName = "$msg"; cmd.Parameters.Add(pMsg);
        var pEx = cmd.CreateParameter(); pEx.ParameterName = "$ex"; cmd.Parameters.Add(pEx);
        var pCorr = cmd.CreateParameter(); pCorr.ParameterName = "$corr"; cmd.Parameters.Add(pCorr);
        var pCat = cmd.CreateParameter(); pCat.ParameterName = "$cat"; cmd.Parameters.Add(pCat);
        var pSrc = cmd.CreateParameter(); pSrc.ParameterName = "$src"; cmd.Parameters.Add(pSrc);
        var pProps = cmd.CreateParameter(); pProps.ParameterName = "$props"; cmd.Parameters.Add(pProps);

        foreach (var r in records)
        {
            // Always store as UTC (cross-source consistency + correct ordering). Converted to local for display.
            pTs.Value = r.Timestamp.ToUniversalTime().ToString("O");
            pLevel.Value = (int)r.Level;
            pMsg.Value = r.Message;
            pEx.Value = (object?)r.Exception ?? DBNull.Value;
            pCorr.Value = (object?)r.CorrelationId ?? DBNull.Value;
            pCat.Value = (object?)r.Category ?? DBNull.Value;
            pSrc.Value = r.Source;
            pProps.Value = (object?)r.PropertiesJson ?? DBNull.Value;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        _logger.LogDebug("{Count} log kaydı SQLite'a yazıldı.", records.Count);
    }

    public async Task<IReadOnlyList<LogRecordDto>> QueryAsync(LogQuery query, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        var sql = new System.Text.StringBuilder("""
            SELECT Id, Timestamp, Level, Message, Exception, CorrelationId, Category, Source, Properties
            FROM Logs WHERE 1=1
            """);

        if (query.MinLevel is { } lvl)
        {
            sql.Append(" AND Level >= $minLevel");
            cmd.Parameters.AddWithValue("$minLevel", (int)lvl);
        }
        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            sql.Append(" AND Category = $cat");
            cmd.Parameters.AddWithValue("$cat", query.Category);
        }
        if (!string.IsNullOrWhiteSpace(query.Source))
        {
            sql.Append(" AND Source = $src");
            cmd.Parameters.AddWithValue("$src", query.Source);
        }
        if (!string.IsNullOrWhiteSpace(query.CorrelationId))
        {
            sql.Append(" AND CorrelationId = $corr");
            cmd.Parameters.AddWithValue("$corr", query.CorrelationId);
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            sql.Append(" AND (Message LIKE $q OR Exception LIKE $q)");
            cmd.Parameters.AddWithValue("$q", $"%{query.Search}%");
        }

        sql.Append(" ORDER BY Id DESC LIMIT $take");
        cmd.Parameters.AddWithValue("$take", Math.Clamp(query.Take, 1, 1000));
        cmd.CommandText = sql.ToString();

        var list = new List<LogRecordDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var level = reader.GetInt32(2);
            list.Add(new LogRecordDto
            {
                Id = reader.GetInt64(0),
                Timestamp = DateTimeOffset.Parse(reader.GetString(1)),
                Level = level,
                LevelName = LevelName(level),
                Message = reader.GetString(3),
                Exception = reader.IsDBNull(4) ? null : reader.GetString(4),
                CorrelationId = reader.IsDBNull(5) ? null : reader.GetString(5),
                Category = reader.IsDBNull(6) ? null : reader.GetString(6),
                Source = reader.GetString(7),
                PropertiesJson = reader.IsDBNull(8) ? null : reader.GetString(8)
            });
        }
        return list;
    }

    public async Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT Category FROM Logs WHERE Category IS NOT NULL ORDER BY Category;";

        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(reader.GetString(0));
        return list;
    }

    private static string LevelName(int level) => level switch
    {
        0 => "Trace",
        1 => "Debug",
        2 => "Information",
        3 => "Warning",
        4 => "Error",
        5 => "Critical",
        _ => level.ToString()
    };

    public async Task<IReadOnlyList<LogRecord>> GetRecentAsync(int take = 100, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Timestamp, Level, Message, Exception, CorrelationId, Category, Source, Properties
            FROM Logs ORDER BY Id DESC LIMIT $take;
            """;
        var p = cmd.CreateParameter(); p.ParameterName = "$take"; p.Value = take; cmd.Parameters.Add(p);

        var list = new List<LogRecord>(take);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new LogRecord(
                Timestamp: DateTimeOffset.Parse(reader.GetString(0)),
                Level: (LogLevel)reader.GetInt32(1),
                Message: reader.GetString(2),
                Exception: reader.IsDBNull(3) ? null : reader.GetString(3),
                CorrelationId: reader.IsDBNull(4) ? null : reader.GetString(4),
                Category: reader.IsDBNull(5) ? null : reader.GetString(5),
                Source: reader.GetString(6),
                PropertiesJson: reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return list;
    }
}
