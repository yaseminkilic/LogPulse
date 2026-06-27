namespace LogPulse.Shared.Errors;

/// <summary>
/// The axis that lets the client decide <b>how</b> to show an error to the user.
/// It is independent of the logging level (LogLevel): an event may be logged but never shown to the user.
/// </summary>
public enum ErrorSeverity
{
    /// <summary>Show nothing to the user (e.g. a cancelled request).</summary>
    Silent = 0,

    /// <summary>Informational — a short, non-blocking toast.</summary>
    Info = 1,

    /// <summary>Warning — a short toast, but draws attention (validation, business rule).</summary>
    Warning = 2,

    /// <summary>Error — may require a user decision/confirmation; can escalate to a modal dialog.</summary>
    Error = 3
}
