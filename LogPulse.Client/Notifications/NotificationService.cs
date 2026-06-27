using LogPulse.Shared.Errors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Radzen;

namespace LogPulse.Client.Notifications;

/// <summary>
/// Radzen-based implementation of <see cref="INotificationService"/>.
/// <list type="bullet">
///   <item><description><b>severity→channel:</b> Silent=don't show, Info/Warning=toast (Radzen <c>NotificationService</c>), Error=modal (Radzen <c>DialogService</c>).</description></item>
///   <item><description><b>dedup:</b> the same errorCode+message is not shown again within <c>DedupWindow</c>.</description></item>
///   <item><description><b>coalesce:</b> suppressed repeats are counted; "(+N)" is appended on the next display.</description></item>
///   <item><description><b>rate limit:</b> at most <c>RateLimitPerSecond</c> notifications per second; excess is suppressed.</description></item>
///   <item><description><b>role-based:</b> correlationId + technical detail for admins; a plain message for regular users.</description></item>
///   <item><description><b>eviction:</b> the dedup state does not grow unbounded — entries past retention and
///   the oldest entries above <c>MaxDedupEntries</c> are dropped (prevents a memory leak in a long WASM session).</description></item>
/// </list>
/// WASM is single-threaded; even so, state access is guarded with a <c>lock</c> (cheap, safe).
/// </summary>
public sealed class NotificationService : INotificationService
{
    private readonly Radzen.NotificationService _toasts;
    private readonly DialogService _dialogs;
    private readonly ICurrentUser _user;
    private readonly NotificationOptions _options;
    private readonly ILogger<NotificationService> _logger;
    private readonly TimeProvider _clock;

    private readonly object _gate = new();
    private readonly Dictionary<string, DedupState> _dedup = new();
    private readonly Queue<DateTime> _recentShows = new();

    public NotificationService(
        Radzen.NotificationService toasts,
        DialogService dialogs,
        ICurrentUser user,
        IOptions<NotificationOptions> options,
        ILogger<NotificationService> logger,
        TimeProvider clock)
    {
        _toasts = toasts;
        _dialogs = dialogs;
        _user = user;
        _options = options.Value;
        _logger = logger;
        _clock = clock;
    }

    public void Notify(ApiError error)
    {
        // 1) Server whitelist: Silent or notify=false → show nothing.
        if (!error.Notify || error.Severity == ErrorSeverity.Silent)
            return;

        var key = $"{error.ErrorCode}|{error.UserMessage}";
        int suppressed;

        lock (_gate)
        {
            // Injectable clock: so dedup/rate-limit/eviction timing is testable and free of
            // wall-clock-dependent flaky behavior (TimeProvider.System in production).
            var now = _clock.GetUtcNow().UtcDateTime;

            // 0) Leak prevention: drop dead entries past their retention period (so the dictionary doesn't grow unbounded).
            PruneExpiredDedup(now);

            // 2) Dedup + coalesce: if within the window, suppress and count.
            if (_dedup.TryGetValue(key, out var state) && now - state.LastShownAt < _options.DedupWindow)
            {
                state.SuppressedCount++;
                _dedup[key] = state;
                return;
            }

            // 3) Rate limit: if the number of displays in the last 1 s exceeds the limit, suppress.
            PruneRecentShows(now);
            if (_recentShows.Count >= _options.RateLimitPerSecond)
            {
                _logger.LogDebug("Bildirim rate limit'e takıldı, bastırıldı: {Key}", key);
                return;
            }

            // 4) Show: update state.
            suppressed = _dedup.TryGetValue(key, out var prev) ? prev.SuppressedCount : 0;
            _dedup[key] = new DedupState { LastShownAt = now, SuppressedCount = 0 };
            EnforceMaxDedupEntries(); // hard upper bound (drops the oldest entries; the fresh key just added is preserved)
            _recentShows.Enqueue(now);
        }

        Show(error, suppressed);
    }

    public void Success(string message)
    {
        _toasts.Notify(new NotificationMessage
        {
            Severity = NotificationSeverity.Success,
            Summary = "Başarılı",
            Detail = message,
            Duration = _options.ToastDurationMs
        });
    }

    private void Show(ApiError error, int suppressedCount)
    {
        var message = error.UserMessage;
        if (suppressedCount > 0)
            message += $" (+{suppressedCount} benzer)";

        // Role-based: admins see the technical detail.
        string? detail = _user.IsAdmin && error.CorrelationId is not null
            ? $"Kod: {error.ErrorCode} · CorrelationId: {error.CorrelationId}"
            : null;

        switch (error.Severity)
        {
            case ErrorSeverity.Info:
                Toast(NotificationSeverity.Info, "Bilgi", message, detail);
                break;

            case ErrorSeverity.Warning:
                Toast(NotificationSeverity.Warning, "Uyarı", message, detail);
                break;

            case ErrorSeverity.Error:
                // A case requiring a user decision/confirmation → blocking modal.
                _ = _dialogs.Alert(
                    detail is null ? message : $"{message}\n\n{detail}",
                    "Hata",
                    new AlertOptions { OkButtonText = "Tamam" });
                break;
        }
    }

    private void Toast(NotificationSeverity severity, string summary, string detail, string? extra)
    {
        _toasts.Notify(new NotificationMessage
        {
            Severity = severity,
            Summary = summary,
            Detail = extra is null ? detail : $"{detail} — {extra}",
            Duration = _options.ToastDurationMs
        });
    }

    private void PruneRecentShows(DateTime now)
    {
        while (_recentShows.Count > 0 && now - _recentShows.Peek() > TimeSpan.FromSeconds(1))
            _recentShows.Dequeue();
    }

    /// <summary>
    /// Removes dedup entries whose retention period has expired. Retention is kept at least as large as
    /// <see cref="NotificationOptions.DedupWindow"/>; otherwise entries within the active window would be dropped
    /// early and dedup would break.
    /// </summary>
    private void PruneExpiredDedup(DateTime now)
    {
        if (_dedup.Count == 0) return;

        var retention = _options.DedupRetention > _options.DedupWindow
            ? _options.DedupRetention
            : _options.DedupWindow;

        List<string>? expired = null;
        foreach (var kvp in _dedup)
        {
            if (now - kvp.Value.LastShownAt > retention)
                (expired ??= new()).Add(kvp.Key);
        }

        if (expired is null) return;
        foreach (var key in expired)
            _dedup.Remove(key);
    }

    /// <summary>
    /// Applies an absolute ceiling by dropping the oldest (LastShownAt) entries if
    /// <see cref="NotificationOptions.MaxDedupEntries"/> is exceeded. In a burst the dictionary stays bounded even
    /// before retention expires.
    /// </summary>
    private void EnforceMaxDedupEntries()
    {
        var max = _options.MaxDedupEntries;
        if (max <= 0 || _dedup.Count <= max) return;

        foreach (var key in _dedup
                     .OrderBy(kvp => kvp.Value.LastShownAt)
                     .Take(_dedup.Count - max)
                     .Select(kvp => kvp.Key)
                     .ToList())
        {
            _dedup.Remove(key);
        }
    }

    /// <summary>Test/diagnostics: the number of unique dedup keys currently tracked (to verify the memory bound).</summary>
    internal int TrackedDedupKeyCount
    {
        get { lock (_gate) return _dedup.Count; }
    }

    private struct DedupState
    {
        public DateTime LastShownAt;
        public int SuppressedCount;
    }
}
