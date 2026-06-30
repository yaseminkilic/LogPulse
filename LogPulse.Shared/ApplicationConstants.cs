
namespace LogPulse.Shared;

/// <summary>
/// Correlation constants shared by the client and server. This is the single source of truth
/// for the header name: the server writes this header on the response, the client reads it by the same name.
/// </summary>
public static class ApplicationConstants
{
    /// <summary>HTTP header that carries the CorrelationId on the request/response.</summary>
    public const string ThemeDark = "dark";
    public const string ThemeLight = "software";
}
