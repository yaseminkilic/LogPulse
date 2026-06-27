namespace LogPulse.Logging;

/// <summary>Filter parameters for the admin log viewer.</summary>
public sealed record LogQuery(
    int Take = 100,
    LogLevel? MinLevel = null,
    string? Category = null,
    string? Source = null,
    string? Search = null,
    string? CorrelationId = null);
