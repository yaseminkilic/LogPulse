using Microsoft.Extensions.Options;

namespace LogPulse.Logging;

/// <summary>
/// Background consumer that drains the <see cref="LogPipeline"/> channel. Writes important records
/// to SQLite in batches (persistence decoupled from the request path, non-blocking).
/// </summary>
public sealed class LogPersistenceWorker : BackgroundService
{
    private readonly LogPipeline _pipeline;
    private readonly ILogStore _store;
    private readonly LogIngestOptions _options;
    private readonly ILogger<LogPersistenceWorker> _logger;

    public LogPersistenceWorker(
        LogPipeline pipeline,
        ILogStore store,
        IOptions<LogIngestOptions> options,
        ILogger<LogPersistenceWorker> logger)
    {
        _pipeline = pipeline;
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _store.InitializeAsync(stoppingToken);

        var reader = _pipeline.Reader;
        var buffer = new List<LogRecord>(_options.PersistBatchSize);

        try
        {
            while (await reader.WaitToReadAsync(stoppingToken))
            {
                // Collect what's ready up to the batch limit, write in a single transaction.
                while (buffer.Count < _options.PersistBatchSize && reader.TryRead(out var record))
                    buffer.Add(record);

                if (buffer.Count == 0) continue;

                try
                {
                    await _store.SaveBatchAsync(buffer, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // A persistence failure must not take the app down; make the drop visible.
                    _logger.LogError(ex, "{Count} log kaydı SQLite'a yazılamadı, batch düşürüldü.", buffer.Count);
                }
                finally
                {
                    buffer.Clear();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
