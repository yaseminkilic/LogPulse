namespace LogPulse.Client.Logging;

/// <summary>
/// Holds the current client CorrelationId as <b>flow-local</b> state (<see cref="AsyncLocal{T}"/>):
/// each logical async flow sees its own value → concurrent/interleaved requests do not overwrite each
/// other's id (this eliminates the race on a single shared field).
/// <para>
/// <see cref="CorrelationScopeHandler"/> generates an id at the start of every outgoing request, writes
/// it here, and carries the same id to the server via the <c>X-Correlation-Id</c> header; <see cref="CorrelationEnricher"/>
/// stamps this value onto client log events within the request flow. When no flow is active the value is
/// <c>null</c> (no stale/incorrect id is borrowed).
/// </para>
/// </summary>
public sealed class ClientCorrelationAccessor
{
    private readonly AsyncLocal<string?> _current = new();

    public string? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
