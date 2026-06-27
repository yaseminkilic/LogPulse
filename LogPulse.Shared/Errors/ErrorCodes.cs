namespace LogPulse.Shared.Errors;

/// <summary>
/// Machine-readable error codes shared by the server and client.
/// The client interceptor inspects these codes for dedup and message selection.
/// </summary>
public static class ErrorCodes
{
    public const string RequestCancelled = "REQUEST_CANCELLED";
    public const string Validation = "VALIDATION";
    public const string Forbidden = "FORBIDDEN";
    public const string Unhandled = "UNHANDLED";

    // Codes belonging to the SignalR / connection axis
    public const string HubUnhandled = "HUB_UNHANDLED";
    public const string HubDisconnected = "HUB_DISCONNECTED";
}
