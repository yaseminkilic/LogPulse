using Microsoft.Data.Sqlite;

namespace LogPulse.Demo;

/// <summary>
/// A minimal demo repository that performs real SQLite CRUD. The goal: to demonstrate how a single
/// <b>real</b> data access error (a constraint violation → <see cref="SqliteException"/>) naturally
/// reaches <c>ClassifiedExceptionHandler</c> and is persisted under the request's CorrelationId,
/// with the client adding its trace under the same id. Removed in production.
/// </summary>
public sealed class DemoOrderRepository
{
    private readonly string _connectionString;

    public DemoOrderRepository(IConfiguration config)
        => _connectionString = config.GetConnectionString("DemoOrders") ?? "Data Source=demo-orders.db";

    public async Task EnsureCreatedAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        // Quantity > 0 CHECK constraint: an invalid quantity is rejected with a real DB error.
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Orders (
                Id       INTEGER PRIMARY KEY AUTOINCREMENT,
                Sku      TEXT    NOT NULL,
                Customer TEXT    NOT NULL,
                Quantity INTEGER NOT NULL CHECK(Quantity > 0),
                Status   TEXT    NOT NULL DEFAULT 'Open'
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Adds a new order. If invalid data is rejected by a DB constraint, throws
    /// <see cref="SqliteException"/> (no compensation — the real error flows up).</summary>
    public async Task<long> CreateAsync(string sku, string customer, int quantity, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Orders (Sku, Customer, Quantity) VALUES ($sku, $customer, $qty);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$sku", sku);
        cmd.Parameters.AddWithValue("$customer", customer);
        cmd.Parameters.AddWithValue("$qty", quantity);

        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
