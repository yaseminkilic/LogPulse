using System.Text.Json;
using LogPulse.Errors;
using LogPulse.Logging;
using LogPulse.Shared.Errors;
using LogPulse.Shared.Logging;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace LogPulse.Middleware;

/// <summary>
/// Single global error boundary — implemented as a .NET 8 <see cref="IExceptionHandler"/>. <c>app.UseExceptionHandler()</c>
/// passes exceptions caught in the pipeline here (replacing the hand-written try/catch middleware).
/// It cleanly separates three responsibilities:
/// <list type="bullet">
///   <item><description><b>Record:</b> sends every exception to <see cref="LogPipeline"/> at its classified level (single path; no extra <c>_logger.LogError</c> → no double logging).</description></item>
///   <item><description><b>Recover:</b> produces an appropriate status code without dropping the request.</description></item>
///   <item><description><b>Notify:</b> carries the decision to the client as <b>data</b> — puts <c>errorCode/severity/notify/userMessage/correlationId</c> into ProblemDetails; the client decides whether to display it.</description></item>
/// </list>
/// <para>
/// <b>Single-path note:</b> the framework's <c>ExceptionHandlerMiddleware</c> also logs the exception at Error level;
/// this duplicate is suppressed via the <c>Microsoft.AspNetCore.Diagnostics</c> Serilog override in
/// <c>appsettings.json</c> → observability flows through a single path (<see cref="LogPipeline"/>).
/// </para>
/// </summary>
public sealed class ClassifiedExceptionHandler : IExceptionHandler
{
    private readonly LogPipeline _pipeline;
    private readonly IHostEnvironment _env;

    public ClassifiedExceptionHandler(LogPipeline pipeline, IHostEnvironment env)
    {
        _pipeline = pipeline;
        _env = env;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        // Resolve the CorrelationId ONCE; use the same value in both the log record and the response.
        var correlationId = ResolveCorrelationId(context);

        // 1) Swallow cancelled requests: not an error, not noise, show nothing to the user.
        if (context.RequestAborted.IsCancellationRequested || exception is OperationCanceledException)
        {
            var cancelClass = ExceptionClassifier.Classify(new OperationCanceledException());
            await _pipeline.EnqueueAsync(BuildRecord(context, exception, cancelClass, correlationId));

            // If the client is still connected (server-side cancellation), return a consistent 499; if it
            // actually disconnected, don't try to write (no connection). The client interceptor treats 499 as silent.
            if (!context.RequestAborted.IsCancellationRequested && !context.Response.HasStarted)
                context.Response.StatusCode = cancelClass.StatusCode; // 499
            return true; // swallowed → handled
        }

        var classification = ExceptionClassifier.Classify(exception);

        // 2) Single logging path: the handler writes only to the pipeline.
        await _pipeline.EnqueueAsync(BuildRecord(context, exception, classification, correlationId));

        // 3) If the response has already started, we can't intervene; treat as handled (no re-throw).
        if (context.Response.HasStarted)
            return true;

        // 4) API request → rich ProblemDetails; page request → /Error.
        if (IsApiRequest(context))
            await WriteProblemDetailsAsync(context, exception, classification, correlationId);
        else
            context.Response.Redirect("/Error");

        return true;
    }

    /// <summary>
    /// Reads the request's CorrelationId from <see cref="HttpContext.Items"/>; if absent (e.g. CorrelationIdMiddleware
    /// is disabled), generates a new one. Resolved in a single place so the log and the response never carry different ids.
    /// </summary>
    private static string ResolveCorrelationId(HttpContext context) =>
        context.Items[CorrelationIdMiddleware.ItemsKey] as string ?? Guid.NewGuid().ToString("N");

    /// <summary>The resolved CorrelationId is passed in by the caller (so the log and the response share the same id).</summary>
    private static LogRecord BuildRecord(HttpContext context, Exception ex, ErrorClassification c, string correlationId)
    {
        // 500s are critical, app-breaking errors → tag with a category (always persist).
        var category = c.StatusCode == 500 ? LogCategories.Critical : null;

        return new LogRecord(
            Timestamp: DateTimeOffset.UtcNow,
            Level: c.LogLevel,
            Message: $"{c.ErrorCode}: {ex.Message} [{context.Request.Method} {context.Request.Path}]",
            Exception: c.LogLevel >= LogLevel.Error ? ex.ToString() : ex.Message,
            CorrelationId: correlationId,
            Category: category,
            Source: "Server",
            PropertiesJson: null);
    }

    private async Task WriteProblemDetailsAsync(HttpContext context, Exception ex, ErrorClassification c, string correlationId)
    {
        var problem = new ProblemDetails
        {
            Status = c.StatusCode,
            Title = c.UserMessage,
            Type = $"https://httpstatuses.io/{c.StatusCode}",
            Instance = context.Request.Path
        };

        // Decision fields that the client interceptor will read.
        problem.Extensions["errorCode"] = c.ErrorCode;
        problem.Extensions["severity"] = (int)c.Severity;
        problem.Extensions["notify"] = c.Notify;
        problem.Extensions["userMessage"] = c.UserMessage;
        problem.Extensions["correlationId"] = correlationId;

        // Per-field validation errors (if any).
        if (ex is ValidationException ve && ve.Errors.Count > 0)
            problem.Extensions["validationErrors"] = ve.Errors;

        // Technical detail ONLY in Development (prevent information leakage).
        if (_env.IsDevelopment())
            problem.Extensions["detail"] = ex.ToString();

        context.Response.StatusCode = c.StatusCode;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(problem, ProblemJsonOptions));
    }

    private static readonly JsonSerializerOptions ProblemJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Is this an API/XHR request (rich ProblemDetails), or a browser navigation (/Error redirect)?
    /// <para>
    /// The decision relies on <b>positive signals</b>; when ambiguous, browser navigation is assumed:
    /// <list type="number">
    ///   <item><description>If the path is under <c>/api</c> or <c>/ingest</c> it is definitely an API
    ///   (all data endpoints in this app live there).</description></item>
    ///   <item><description>Paths outside <c>/api</c> are Blazor <b>page routes</b>; they count as API only
    ///   with an explicit data signal: an XHR/fetch marker or the client requesting <c>application/json</c>.</description></item>
    ///   <item><description>Otherwise (<c>text/html</c>, <c>*/*</c> or an <b>empty</b> Accept) it is treated as a
    ///   browser navigation and redirected to <c>/Error</c>.</description></item>
    /// </list>
    /// The previous version, via <c>!accept.Contains("text/html")</c>, treated an <b>empty</b> Accept as API → a page
    /// navigation could see raw JSON instead of an error page. Since real navigations always carry <c>text/html</c>,
    /// resolving the ambiguous case toward the page is safe and breaks no real data endpoint.
    /// </para>
    /// </summary>
    private static bool IsApiRequest(HttpContext context)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/api") || path.StartsWithSegments("/ingest"))
            return true;

        // Path outside /api → page route. Promote to API only with an explicit data signal.
        if (context.Request.Headers.XRequestedWith == "XMLHttpRequest")
            return true;

        var accept = context.Request.Headers.Accept.ToString();
        return accept.Contains("application/json", StringComparison.OrdinalIgnoreCase);
    }
}
