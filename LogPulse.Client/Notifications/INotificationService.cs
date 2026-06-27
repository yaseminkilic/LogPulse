using LogPulse.Shared.Errors;

namespace LogPulse.Client.Notifications;

/// <summary>
/// The <b>single</b> notification abstraction on the client. Both the HTTP ProblemDetails interpreter and
/// the SignalR HubException interpreter enter here. The rules it owns:
/// severity→channel mapping, dedup, throttle/coalesce, rate limit, role-based content.
/// </summary>
public interface INotificationService
{
    /// <summary>Runs the classified error from the server through the rules and shows it on the appropriate channel.</summary>
    void Notify(ApiError error);

    /// <summary>Successful-operation notice (subject to the rules, toast).</summary>
    void Success(string message);
}
