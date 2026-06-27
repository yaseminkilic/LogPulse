namespace LogPulse.Shared.Errors;

/// <summary>
/// An expected business rule violation. This is not a "bug"; it is shown to the user with a
/// plain message and logged at <b>Information</b> level (it does not pollute SQLite).
/// </summary>
public class BusinessException : Exception
{
    /// <summary>Machine-readable code used in classification (e.g. "STOCK_INSUFFICIENT").</summary>
    public string Code { get; }

    /// <summary>A safe message that can be shown directly to the user.</summary>
    public string UserMessage { get; }

    public BusinessException(string code, string userMessage, Exception? inner = null)
        : base(userMessage, inner)
    {
        Code = string.IsNullOrWhiteSpace(code) ? "BUSINESS_RULE" : code;
        UserMessage = userMessage;
    }
}
