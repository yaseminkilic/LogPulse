using LogPulse.Shared.Logging;

namespace LogPulse.Client.Logging;

/// <summary>
/// Delegating handler that assigns a flow-local CorrelationId to every outgoing request and carries it
/// to the server. Replaces the old "capture from the response" model; because that model wrote to a
/// shared field, ids overwrote each other on concurrent requests.
/// <list type="number">
///   <item><description>Determines an id at the start of the request: the id header if the request already
///   carries one, otherwise the id of the active flow scope (so nested calls land on the same trail), and failing that a new Guid.</description></item>
///   <item><description>Writes to <see cref="ClientCorrelationAccessor"/> (AsyncLocal) → client logs within the
///   request flow are enriched with this id.</description></item>
///   <item><description>Sends it as the <c>X-Correlation-Id</c> request header → since the server's
///   <c>CorrelationIdMiddleware</c> prefers the incoming header, client+server logs share the same id end to end.</description></item>
/// </list>
/// Registered <b>outermost</b> in the handler chain: the id is established before the other downstream handlers
/// and the response interpreter run.
/// <para>
/// The method is deliberately <c>async</c>: the <see cref="AsyncLocal{T}"/> assignment is scoped to this call's
/// logical async flow → it does not leak into the calling component, and each concurrent request made from the
/// same parent sees its own id. (In a non-async handler the assignment would leak synchronously into the parent
/// and concurrent calls would overwrite each other.)
/// </para>
/// </summary>
public sealed class CorrelationScopeHandler : DelegatingHandler
{
    private readonly ClientCorrelationAccessor _accessor;

    public CorrelationScopeHandler(ClientCorrelationAccessor accessor) => _accessor = accessor;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // 1) Determine the id (priority: explicit header > active scope > new Guid).
        string id;
        if (request.Headers.TryGetValues(CorrelationConstants.HeaderName, out var existing)
            && !string.IsNullOrWhiteSpace(existing.FirstOrDefault()))
        {
            id = existing.First()!;
        }
        else
        {
            id = _accessor.Current ?? Guid.NewGuid().ToString("N");
        }

        // 2) Flow-local scope: client logs in this request flow are stamped with the same id.
        _accessor.Current = id;

        // 3) Carry it to the server (leave it alone if already present).
        if (!request.Headers.Contains(CorrelationConstants.HeaderName))
            request.Headers.TryAddWithoutValidation(CorrelationConstants.HeaderName, id);

        return await base.SendAsync(request, cancellationToken);
    }
}
