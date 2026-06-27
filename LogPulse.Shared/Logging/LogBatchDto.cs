namespace LogPulse.Shared.Logging;

/// <summary>
/// The POST body of <c>Serilog.Sinks.Http</c>: <c>{ "events": [ ... ] }</c>.
/// The client sends this to the API in a single request when the batch fills up or the period elapses.
/// </summary>
public sealed class LogBatchDto
{
    public List<LogEventDto> Events { get; set; } = new();
}
