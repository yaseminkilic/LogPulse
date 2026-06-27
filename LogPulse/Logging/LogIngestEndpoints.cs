using System.Text.Json;
using LogPulse.Shared.Logging;

namespace LogPulse.Logging;

/// <summary>
/// Endpoint that receives the client's <c>Serilog.Sinks.Http</c> batches.
/// Normalizes each incoming event and hands it to <see cref="LogPipeline"/> (single pipeline).
/// </summary>
/// <remarks>
/// <b>Important:</b> the default batch formatter of <c>Serilog.Sinks.Http</c> sends the body as a plain
/// JSON <b>array</b>: <c>[ {...}, {...} ]</c> — not <c>{ "events": [...] }</c>.
/// This endpoint accepts both shapes (an array or an <c>events</c> wrapper), so it won't break even if the
/// formatter changes.
/// </remarks>
public static class LogIngestEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapLogIngest(this IEndpointRouteBuilder app, string pattern = "/ingest/logs")
    {
        app.MapPost(pattern, async (HttpRequest request, LogPipeline pipeline, CancellationToken ct) =>
        {
            using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);

            foreach (var record in ParseBatch(doc.RootElement))
                await pipeline.EnqueueAsync(record);

            // Serilog.Sinks.Http expects a 2xx; otherwise it holds the batch back (retry).
            return Results.Accepted();
        })
        .AllowAnonymous()   // the logging flow must be independent of authentication
        .WithName("IngestClientLogs");

        return app;
    }

    /// <summary>
    /// Converts the incoming body into a normalized list of <see cref="LogRecord"/>.
    /// Accepts both a plain JSON array (the default <c>Serilog.Sinks.Http</c> formatter) and
    /// a <c>{ "events": [...] }</c> wrapper; returns an empty list for an unrecognized body.
    /// </summary>
    internal static IReadOnlyList<LogRecord> ParseBatch(JsonElement root)
    {
        IEnumerable<JsonElement> elements = root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray(),
            JsonValueKind.Object when root.TryGetProperty("events", out var evs)
                && evs.ValueKind == JsonValueKind.Array => evs.EnumerateArray(),
            _ => Array.Empty<JsonElement>()
        };

        var records = new List<LogRecord>();
        foreach (var el in elements)
        {
            var ev = el.Deserialize<LogEventDto>(JsonOpts);
            if (ev is not null)
                records.Add(ToRecord(ev));
        }
        return records;
    }

    private static LogRecord ToRecord(LogEventDto ev)
    {
        string? correlationId = ReadString(ev.Properties, "CorrelationId");
        string? category = ReadString(ev.Properties, LogCategories.PropertyName);
        string? propsJson = ev.Properties is { Count: > 0 }
            ? JsonSerializer.Serialize(ev.Properties)
            : null;

        return new LogRecord(
            Timestamp: ev.Timestamp,
            Level: MapLevel(ev.Level),
            Message: ev.RenderedMessage ?? ev.MessageTemplate ?? "",
            Exception: ev.Exception,
            CorrelationId: correlationId,
            Category: category,
            Source: "Client",
            PropertiesJson: propsJson);
    }

    private static string? ReadString(IReadOnlyDictionary<string, JsonElement>? props, string key)
    {
        if (props is null || !props.TryGetValue(key, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
    }

    /// <summary>Converts a Serilog level string to a Microsoft <see cref="LogLevel"/>.</summary>
    private static LogLevel MapLevel(string? serilogLevel) => serilogLevel switch
    {
        "Verbose" => LogLevel.Trace,
        "Debug" => LogLevel.Debug,
        "Information" => LogLevel.Information,
        "Warning" => LogLevel.Warning,
        "Error" => LogLevel.Error,
        "Fatal" => LogLevel.Critical,
        _ => LogLevel.Information
    };
}
