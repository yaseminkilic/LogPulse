using LogPulse.Logging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LogPulse.Tests;

/// <summary>
/// <see cref="SqliteLogStore"/> integration tests: write→query cycle over a real (temporary file) SQLite,
/// filters, ordering, take limit, category list, and UTC storage.
/// Each test uses an isolated database file; <see cref="DisposeAsync"/> cleans it up.
/// </summary>
public sealed class SqliteLogStoreTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"logdemo_test_{Guid.NewGuid():N}.db");
    private SqliteLogStore _store = null!;

    public async Task InitializeAsync()
    {
        // Pooling=False → so the file can be deleted without being locked at the end of the test.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Logs"] = $"Data Source={_dbPath};Pooling=False"
            })
            .Build();

        _store = new SqliteLogStore(config, NullLogger<SqliteLogStore>.Instance);
        await _store.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private static LogRecord Record(
        LogLevel level, string message, string source = "Server",
        string? category = null, string? correlationId = null,
        string? exception = null, DateTimeOffset? ts = null) =>
        new(
            Timestamp: ts ?? DateTimeOffset.UtcNow,
            Level: level,
            Message: message,
            Exception: exception,
            CorrelationId: correlationId,
            Category: category,
            Source: source,
            PropertiesJson: null);

    [Fact]
    public async Task SaveAndQuery_RoundTripsAllFields()
    {
        var ts = new DateTimeOffset(2026, 6, 22, 10, 30, 0, TimeSpan.FromHours(3)); // +03:00
        await _store.SaveBatchAsync(new[]
        {
            Record(LogLevel.Error, "patladı", "Hub", "Critical", "corr-1", "System.Exception: boom", ts)
        });

        var rows = await _store.QueryAsync(new LogQuery());

        var row = Assert.Single(rows);
        Assert.Equal((int)LogLevel.Error, row.Level);
        Assert.Equal("Error", row.LevelName);
        Assert.Equal("patladı", row.Message);
        Assert.Equal("Hub", row.Source);
        Assert.Equal("Critical", row.Category);
        Assert.Equal("corr-1", row.CorrelationId);
        Assert.Contains("boom", row.Exception);
        // Stored as UTC (+03:00 → 07:30Z).
        Assert.Equal(ts.ToUniversalTime(), row.Timestamp.ToUniversalTime());
    }

    [Fact]
    public async Task EmptyBatch_IsNoOp()
    {
        await _store.SaveBatchAsync(Array.Empty<LogRecord>());

        Assert.Empty(await _store.QueryAsync(new LogQuery()));
    }

    [Fact]
    public async Task Query_OrdersNewestFirst()
    {
        await _store.SaveBatchAsync(new[]
        {
            Record(LogLevel.Warning, "birinci"),
            Record(LogLevel.Warning, "ikinci"),
            Record(LogLevel.Warning, "üçüncü")
        });

        var rows = await _store.QueryAsync(new LogQuery());

        // The most recently inserted (largest Id) comes first.
        Assert.Equal("üçüncü", rows[0].Message);
        Assert.Equal("birinci", rows[^1].Message);
    }

    [Fact]
    public async Task Query_FiltersByMinLevel()
    {
        await _store.SaveBatchAsync(new[]
        {
            Record(LogLevel.Warning, "uyarı"),
            Record(LogLevel.Error, "hata"),
            Record(LogLevel.Critical, "kritik")
        });

        var rows = await _store.QueryAsync(new LogQuery(MinLevel: LogLevel.Error));

        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, r => r.Message == "uyarı");
    }

    [Fact]
    public async Task Query_FiltersByCategoryAndCorrelationAndSource()
    {
        await _store.SaveBatchAsync(new[]
        {
            Record(LogLevel.Warning, "veri", "Server", "DataAccess", "corr-A"),
            Record(LogLevel.Warning, "hub",  "Hub",    "HubConnection", "corr-B"),
            Record(LogLevel.Warning, "diğer","Server", null, "corr-A")
        });

        Assert.Single(await _store.QueryAsync(new LogQuery(Category: "DataAccess")));
        Assert.Single(await _store.QueryAsync(new LogQuery(Source: "Hub")));
        Assert.Equal(2, (await _store.QueryAsync(new LogQuery(CorrelationId: "corr-A"))).Count);
    }

    [Fact]
    public async Task Query_SearchMatchesMessageOrException()
    {
        await _store.SaveBatchAsync(new[]
        {
            Record(LogLevel.Error, "sipariş kaydedilemedi", exception: "TimeoutException"),
            Record(LogLevel.Error, "başka şey", exception: "NullReferenceException")
        });

        Assert.Single(await _store.QueryAsync(new LogQuery(Search: "sipariş")));
        Assert.Single(await _store.QueryAsync(new LogQuery(Search: "Timeout"))); // matches in the exception
    }

    [Fact]
    public async Task Query_TakeIsClampedAndHonored()
    {
        var many = Enumerable.Range(0, 10).Select(i => Record(LogLevel.Warning, $"m{i}")).ToArray();
        await _store.SaveBatchAsync(many);

        Assert.Equal(3, (await _store.QueryAsync(new LogQuery(Take: 3))).Count);
        // Take <= 0 → clamped to at least 1 (prevents a negative LIMIT).
        Assert.Single(await _store.QueryAsync(new LogQuery(Take: 0)));
    }

    [Fact]
    public async Task GetRecent_ReturnsNewestFirst()
    {
        await _store.SaveBatchAsync(new[]
        {
            Record(LogLevel.Warning, "eski"),
            Record(LogLevel.Warning, "yeni")
        });

        var recent = await _store.GetRecentAsync(10);

        Assert.Equal("yeni", recent[0].Message);
    }

    [Fact]
    public async Task GetCategories_ReturnsDistinctNonNullSorted()
    {
        await _store.SaveBatchAsync(new[]
        {
            Record(LogLevel.Warning, "a", category: "DataAccess"),
            Record(LogLevel.Warning, "b", category: "Critical"),
            Record(LogLevel.Warning, "c", category: "DataAccess"),
            Record(LogLevel.Warning, "d", category: null)
        });

        var categories = await _store.GetCategoriesAsync();

        Assert.Equal(new[] { "Critical", "DataAccess" }, categories); // distinct + sorted, excluding null
    }
}
