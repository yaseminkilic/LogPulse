using System.Text.Json;

namespace LogPulse.Shared.Logging;

/// <summary>
/// The transport shape of a single log event. The field names are intentionally
/// identical (PascalCase) to the output of <c>Serilog.Sinks.Http</c>'s
/// <c>NormalRenderedTextFormatter</c> so the server can deserialize without extra conversion.
/// </summary>
public sealed class LogEventDto
{
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>"Verbose" | "Debug" | "Information" | "Warning" | "Error" | "Fatal".</summary>
    public string Level { get; set; } = "Information";

    public string? MessageTemplate { get; set; }

    public string? RenderedMessage { get; set; }

    /// <summary>The string representation of the exception, if any.</summary>
    public string? Exception { get; set; }

    /// <summary>Properties added via enrichment (CorrelationId, SourceContext, EventCategory, etc.).</summary>
    public Dictionary<string, JsonElement>? Properties { get; set; }
}
