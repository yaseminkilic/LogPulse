using Serilog.Core;
using Serilog.Events;

namespace LogPulse.Client.Logging;

/// <summary>
/// Adds the current server CorrelationId from <see cref="ClientCorrelationAccessor"/> to every client
/// log event as a <c>CorrelationId</c> property. This way client logs meet the server logs of the
/// request that triggered them under the <b>same</b> CorrelationId (the link that enables the
/// "related logs" trail in the admin viewer).
/// <para>
/// Uses <see cref="LogEvent.AddPropertyIfAbsent"/>: if a log call has explicitly set the CorrelationId
/// via <c>LogContext.PushProperty</c>, that value is preserved (deterministic matching); otherwise the
/// ambient value is applied.
/// </para>
/// </summary>
public sealed class CorrelationEnricher : ILogEventEnricher
{
    private readonly ClientCorrelationAccessor _accessor;

    public CorrelationEnricher(ClientCorrelationAccessor accessor) => _accessor = accessor;

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var id = _accessor.Current;
        if (!string.IsNullOrEmpty(id))
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("CorrelationId", id));
    }
}
