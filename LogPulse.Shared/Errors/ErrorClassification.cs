using Microsoft.Extensions.Logging;

namespace LogPulse.Shared.Errors;

/// <summary>
/// Collects an exception's decision across three axes in one place:
/// <list type="bullet">
///   <item><description><b>Logging:</b> <see cref="LogLevel"/></description></item>
///   <item><description><b>HTTP response:</b> <see cref="StatusCode"/>, <see cref="ErrorCode"/></description></item>
///   <item><description><b>Notification:</b> <see cref="Severity"/>, <see cref="Notify"/>, <see cref="UserMessage"/></description></item>
/// </list>
/// The same type is used both in the HTTP middleware and in the SignalR hub filter.
/// </summary>
public sealed record ErrorClassification(
    int StatusCode,
    string ErrorCode,
    ErrorSeverity Severity,
    bool Notify,
    string UserMessage,
    LogLevel LogLevel);
