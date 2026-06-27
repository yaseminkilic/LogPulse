namespace LogPulse.Shared.Errors;

/// <summary>
/// An input validation error. Carries a field-level error list. It is an expected condition;
/// it is logged at <b>Information</b> level and shown to the user as a warning.
/// </summary>
/// <remarks>
/// It is intentionally a separate type from <c>System.ComponentModel.DataAnnotations.ValidationException</c>:
/// it carries a field-level dictionary and matches cleanly in the classification switch.
/// </remarks>
public class ValidationException : Exception
{
    /// <summary>Field name → error messages for that field.</summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public ValidationException(string message, IReadOnlyDictionary<string, string[]>? errors = null)
        : base(message)
    {
        Errors = errors ?? new Dictionary<string, string[]>();
    }

    public ValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("Bir veya daha fazla doğrulama hatası oluştu.")
    {
        Errors = errors;
    }
}
