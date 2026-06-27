using LogPulse.Shared.Errors;

namespace LogPulse.Errors;

/// <summary>
/// Single source of truth: converts an exception into an <see cref="ErrorClassification"/>.
/// Both the HTTP <c>ClassifiedExceptionHandler</c> and the SignalR <c>ClassifiedHubFilter</c> use this,
/// so the two channels never diverge.
/// </summary>
public static class ExceptionClassifier
{
    public static ErrorClassification Classify(Exception ex) => ex switch
    {
        // A cancelled request is not an error: silently, at the lowest level.
        OperationCanceledException => new ErrorClassification(
            StatusCode: 499,
            ErrorCode: ErrorCodes.RequestCancelled,
            Severity: ErrorSeverity.Silent,
            Notify: false,
            UserMessage: "",
            LogLevel: LogLevel.Debug),

        // Validation: expected, a warning to the user, logged at Information level.
        ValidationException ve => new ErrorClassification(
            StatusCode: 400,
            ErrorCode: ErrorCodes.Validation,
            Severity: ErrorSeverity.Warning,
            Notify: true,
            UserMessage: ve.Message,
            LogLevel: LogLevel.Information),

        // Business rule violation: expected, a warning to the user, at Information level.
        BusinessException be => new ErrorClassification(
            StatusCode: 422,
            ErrorCode: be.Code,
            Severity: ErrorSeverity.Warning,
            Notify: true,
            UserMessage: be.UserMessage,
            LogLevel: LogLevel.Information),

        // Authorization: a warning to the user, at Warning level.
        UnauthorizedAccessException => new ErrorClassification(
            StatusCode: 403,
            ErrorCode: ErrorCodes.Forbidden,
            Severity: ErrorSeverity.Warning,
            Notify: true,
            UserMessage: "Bu işlem için yetkiniz yok.",
            LogLevel: LogLevel.Warning),

        // Everything else: an unexpected bug. At Error level, with a generic message to the user.
        _ => new ErrorClassification(
            StatusCode: 500,
            ErrorCode: ErrorCodes.Unhandled,
            Severity: ErrorSeverity.Error,
            Notify: true,
            UserMessage: "Beklenmeyen bir hata oluştu.",
            LogLevel: LogLevel.Error)
    };
}
