namespace LogPulse.Shared.Logging;

/// <summary>
/// Category tags that carry the definition of an "important log". When logging, the client
/// stamps the <c>EventCategory</c> property with one of these constants; in addition to the level,
/// the server persistence filter treats these categories as "always store".
///
/// User definition: (1) critical errors that lock up the app, (2) errors during data
/// fetch/save, (3) hub connection loss (critical for the data stream).
/// </summary>
public static class LogCategories
{
    public const string PropertyName = "EventCategory";

    /// <summary>Critical, unrecoverable error that locks up the app.</summary>
    public const string Critical = "Critical";

    /// <summary>Error encountered during data fetch or save.</summary>
    public const string DataAccess = "DataAccess";

    /// <summary>SignalR hub connection state (loss/reconnection).</summary>
    public const string HubConnection = "HubConnection";
}
