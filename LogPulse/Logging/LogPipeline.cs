using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace LogPulse.Logging;

/// <summary>
/// The <b>single</b> log entry point on the server (fan-out hub). Both client batches and the
/// server middleware/hub filter write here. Internally it dispatches to two sinks:
/// <list type="number">
///   <item><description>Observability: echoes the event to Serilog (console, etc.).</description></item>
///   <item><description>Persistence: places "important" events on the channel → <see cref="LogPersistenceWorker"/> batch-writes to SQLite.</description></item>
/// </list>
/// There is no "double logging": the caller makes a single <see cref="EnqueueAsync"/> call;
/// dispatch to multiple sinks happens intentionally from one pipeline.
/// </summary>
public sealed class LogPipeline
{
    private readonly Channel<LogRecord> _channel;
    private readonly LogIngestOptions _options;
    private readonly ILogger<LogPipeline> _logger;

    public LogPipeline(IOptions<LogIngestOptions> options, ILogger<LogPipeline> logger)
    {
        _options = options.Value;
        _logger = logger;
        _channel = Channel.CreateBounded<LogRecord>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            // Never block the UI/request path: if full, drop the oldest record.
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    }

    internal ChannelReader<LogRecord> Reader => _channel.Reader;

    /// <summary>Submits an event to the pipeline. Non-blocking; if the persistence queue is full, the record is silently dropped.</summary>
    public ValueTask EnqueueAsync(LogRecord record)
    {
        // 1) Observability — everything flows to the console/Serilog.
        _logger.Log(record.Level,
            "[{Source}] {Message} (CorrelationId={CorrelationId}, Category={Category})",
            record.Source, record.Message, record.CorrelationId, record.Category);

        // 2) Persistence — only important events go to the queue.
        if (IsImportant(record))
            _channel.Writer.TryWrite(record); // bounded + DropOldest → never blocks

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The "important log" decision: the level threshold OR an always-persist category
    /// (critical error / data access / hub connection).
    /// </summary>
    public bool IsImportant(LogRecord r) =>
        r.Level >= _options.PersistMinimumLevel ||
        (r.Category is not null && _options.AlwaysPersistCategories.Contains(r.Category));
}
