namespace LogPulse.Client.Notifications;

/// <summary>
/// Behavior parameters for the notification pipeline. (Do not confuse these with the logging batch
/// settings: these concern dialog/toast spam, those concern log delivery.)
/// The initial values were chosen as "middle of the road"; tune them based on observation.
/// </summary>
public sealed class NotificationOptions
{
    /// <summary>
    /// The same (errorCode+message) is not shown again within this window; repeats arriving during this
    /// period are counted and merged on the first display after the window as "(+N similar)" (coalesce).
    /// So a single window governs both dedup and the coalesce flush. Default: 5 s.
    /// </summary>
    public TimeSpan DedupWindow { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum notifications that can be shown per second. Excess is suppressed if exceeded. Default: 5.</summary>
    public int RateLimitPerSecond { get; set; } = 5;

    /// <summary>
    /// If a dedup entry is not seen again for this duration it is dropped from memory (leak prevention).
    /// Must be kept larger than <see cref="DedupWindow"/> so the coalesce ("+N") flush is not broken;
    /// if set smaller it is automatically raised to <see cref="DedupWindow"/>. Default: 1 min.
    /// </summary>
    public TimeSpan DedupRetention { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Hard upper bound on the number of unique tracked dedup keys. If exceeded even within retention,
    /// the oldest entries are dropped → the dictionary never grows unbounded. Default: 500.
    /// </summary>
    public int MaxDedupEntries { get; set; } = 500;

    /// <summary>Toast display duration (ms). Default: 4 s.</summary>
    public double ToastDurationMs { get; set; } = 4000;
}
