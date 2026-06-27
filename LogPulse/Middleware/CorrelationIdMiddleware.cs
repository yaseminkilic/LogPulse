using LogPulse.Shared.Logging;
using Serilog.Context;

namespace LogPulse.Middleware;

/// <summary>
/// Assigns a CorrelationId to each request and makes it accessible in <b>three</b> places:
/// <list type="number">
///   <item><description><see cref="HttpContext.Items"/> — order-independent (the primary source of truth).</description></item>
///   <item><description>Response header — client/request trace.</description></item>
///   <item><description>Serilog <see cref="LogContext"/> — log enrichment (only within this scope).</description></item>
/// </list>
/// <para>
/// Important: <c>HttpContext</c> is the same reference throughout the entire pipeline; therefore
/// the value written to <see cref="HttpContext.Items"/> is read safely from the outer
/// <c>ClassifiedExceptionHandler</c> as well — it avoids the <c>AsyncLocal</c> ordering pitfall.
/// </para>
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = CorrelationConstants.HeaderName;
    public const string ItemsKey = "CorrelationId";

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var incoming)
                            && !string.IsNullOrWhiteSpace(incoming)
            ? incoming.ToString()
            : Guid.NewGuid().ToString("N");

        // (1) Order-independent source.
        context.Items[ItemsKey] = correlationId;

        // (2) Reflect back to the client (before the response starts).
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        // (3) AsyncLocal scope for Serilog enrichment only.
        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }
}
