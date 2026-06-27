namespace LogPulse.Shared.Errors;

/// <summary>
/// The common error payload that the client interceptor (from both the HTTP ProblemDetails
/// and the SignalR HubException paths) reads and passes to <c>INotificationService</c>.
/// ProblemDetails extension fields and the HubException.Message JSON deserialize into this shape.
/// </summary>
public sealed class ApiError
{
    /// <summary>Machine-readable code (dedup key). See <see cref="ErrorCodes"/>.</summary>
    public string ErrorCode { get; set; } = ErrorCodes.Unhandled;

    /// <summary>Determines the notification channel (Silent/Info/Warning/Error).</summary>
    public ErrorSeverity Severity { get; set; } = ErrorSeverity.Error;

    /// <summary>Whether the server recommends showing this error to the user (whitelist).</summary>
    public bool Notify { get; set; } = true;

    /// <summary>A safe message that can be shown to the user.</summary>
    public string UserMessage { get; set; } = "Beklenmeyen bir hata oluştu.";

    /// <summary>Log and request correlation; shown to the admin.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>HTTP status code (if any).</summary>
    public int? StatusCode { get; set; }

    /// <summary>Field-level validation errors (populated only in the VALIDATION case).</summary>
    public IDictionary<string, string[]>? ValidationErrors { get; set; }
}
