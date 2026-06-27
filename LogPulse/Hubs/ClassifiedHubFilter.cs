using System.Text.Json;
using LogPulse.Errors;
using LogPulse.Logging;
using LogPulse.Shared.Errors;
using LogPulse.Shared.Logging;
using Microsoft.AspNetCore.SignalR;

namespace LogPulse.Hubs;

/// <summary>
/// The SignalR equivalent of the HTTP middleware. Runs hub method exceptions through the <b>same</b>
/// <see cref="ExceptionClassifier"/>, logs them on a single path (<see cref="LogPipeline"/>),
/// and carries the decision to the client as a JSON <see cref="ApiError"/> inside a <see cref="HubException"/>.
/// This way, hub exceptions not covered by the HTTP middleware also avoid creating dialog spam.
/// <para>
/// Cancellation flows through the same path: in classification, <see cref="OperationCanceledException"/>
/// falls into <see cref="ErrorSeverity.Silent"/> / <c>Notify=false</c>, is logged at <c>Debug</c> level,
/// and reaches the client as a <b>silent</b> ApiError → nothing is shown to the user.
/// (The old behavior re-threw the raw OCE; on the client this turned into a generic <see cref="HubException"/>
/// and could mistakenly trigger an "unexpected error" dialog.)
/// </para>
/// </summary>
public sealed class ClassifiedHubFilter : IHubFilter
{
    private readonly LogPipeline _pipeline;

    public ClassifiedHubFilter(LogPipeline pipeline) => _pipeline = pipeline;

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        try
        {
            return await next(invocationContext);
        }
        catch (Exception ex)
        {
            // Single path: classify, log, carry the decision inside a HubException. Every exception,
            // including cancellation, goes through the same route; the distinction is made in the
            // classification's severity/notify fields.
            throw await HandleAsync(
                ex,
                invocationContext.Hub.GetType().Name,
                invocationContext.HubMethodName,
                ResolveCorrelationId(invocationContext));
        }
    }

    /// <summary>
    /// Resolves the connection's CorrelationId. When establishing the connection (the negotiate request),
    /// the client adds the <see cref="CorrelationConstants.HeaderName"/> header; this header can be read
    /// throughout the connection's lifetime via <see cref="HubCallerContext.GetHttpContext"/> → all hub
    /// errors on this connection are stamped with the same (connection-scoped) id. If the header is absent, a new id is generated.
    /// <para>Note: since SignalR does not support per-call metadata, the id is <b>connection</b>-scoped
    /// rather than <b>call</b>-scoped (a known trade-off).</para>
    /// </summary>
    internal static string ResolveCorrelationId(HubInvocationContext invocationContext)
    {
        var fromHeader = invocationContext.Context.GetHttpContext()?
            .Request.Headers[CorrelationConstants.HeaderName].ToString();

        return string.IsNullOrWhiteSpace(fromHeader) ? Guid.NewGuid().ToString("N") : fromHeader;
    }

    /// <summary>
    /// Classifies the exception, writes it to the single logging path, and produces the
    /// <see cref="HubException"/> to be thrown to the client. The CorrelationId is resolved and passed in by the caller;
    /// the log record and the <see cref="ApiError"/> sent to the client share the same id (the same guarantee as on the HTTP side).
    /// </summary>
    internal async ValueTask<HubException> HandleAsync(
        Exception ex, string hubName, string methodName, string correlationId)
    {
        var classification = ExceptionClassifier.Classify(ex);

        await _pipeline.EnqueueAsync(BuildRecord(ex, classification, hubName, methodName, correlationId));

        return new HubException(JsonSerializer.Serialize(BuildApiError(classification, correlationId), JsonOpts));
    }

    /// <summary>The resolved CorrelationId is passed in by the caller (so the log and the response share the same id).</summary>
    internal static LogRecord BuildRecord(
        Exception ex, ErrorClassification c, string hubName, string methodName, string correlationId) =>
        new(
            Timestamp: DateTimeOffset.UtcNow,
            Level: c.LogLevel,
            Message: $"{c.ErrorCode}: {ex.Message} [Hub {hubName}.{methodName}]",
            Exception: c.LogLevel >= LogLevel.Error ? ex.ToString() : ex.Message,
            CorrelationId: correlationId,
            // 500s are critical, app-breaking errors → tag with a category (always persist).
            Category: c.StatusCode == 500 ? LogCategories.Critical : null,
            Source: "Hub",
            PropertiesJson: null);

    /// <summary>The decision payload that the client interceptor will read, embedded in HubException.Message.</summary>
    internal static ApiError BuildApiError(ErrorClassification c, string correlationId) =>
        new()
        {
            ErrorCode = c.ErrorCode,
            Severity = c.Severity,
            Notify = c.Notify,
            UserMessage = c.UserMessage,
            CorrelationId = correlationId,
            StatusCode = c.StatusCode
        };

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
}
