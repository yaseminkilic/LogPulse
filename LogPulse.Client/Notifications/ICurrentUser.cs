namespace LogPulse.Client.Notifications;

/// <summary>
/// Minimal user context for role-based notifications. In a real application it is
/// fed from <c>AuthenticationStateProvider</c>; in the demo it can be toggled manually.
/// </summary>
public interface ICurrentUser
{
    /// <summary>Admins are shown technical detail + correlationId; regular users get a plain message.</summary>
    bool IsAdmin { get; }
}

/// <summary>Demo-purpose user context that can be changed at runtime.</summary>
public sealed class CurrentUser : ICurrentUser
{
    public bool IsAdmin { get; set; }
}
