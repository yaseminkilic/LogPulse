using LogPulse.Client.Notifications;
using LogPulse.Shared.Errors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Radzen;
using Xunit;
using AppNotifier = LogPulse.Client.Notifications.NotificationService;

namespace LogPulse.Tests;

/// <summary>
/// Client rules that cut off dialog/toast spam: severity→channel, dedup, coalesce ("+N"),
/// rate limit, and role-based content. Because the Error/modal path depends on the JS runtime of
/// Radzen <c>DialogService</c>, these tests deliberately observe the toast (Info/Warning) channel;
/// the dedup/throttle/role logic is fully observable on this channel.
/// </summary>
public class NotificationServiceTests
{
    private static readonly NotificationOptions FastDedup = new()
    {
        DedupWindow = TimeSpan.FromSeconds(5),
        RateLimitPerSecond = 5
    };

    private static (AppNotifier svc, Radzen.NotificationService toasts) Create(
        bool isAdmin = false, NotificationOptions? options = null, TimeProvider? clock = null)
    {
        var toasts = new Radzen.NotificationService();
        var dialogs = new DialogService(null, null);
        var user = new CurrentUser { IsAdmin = isAdmin };
        var svc = new AppNotifier(
            toasts, dialogs, user,
            Options.Create(options ?? FastDedup),
            NullLogger<AppNotifier>.Instance,
            clock ?? TimeProvider.System);
        return (svc, toasts);
    }

    private static ApiError Warning(string code = "VALIDATION", string message = "Geçersiz girdi.",
        string? correlationId = null) =>
        new()
        {
            ErrorCode = code,
            Severity = ErrorSeverity.Warning,
            Notify = true,
            UserMessage = message,
            CorrelationId = correlationId
        };

    [Fact]
    public void NotifyFalse_ShowsNothing()
    {
        var (svc, toasts) = Create();

        svc.Notify(new ApiError { Notify = false, Severity = ErrorSeverity.Warning, UserMessage = "x" });

        Assert.Empty(toasts.Messages);
    }

    [Fact]
    public void SilentSeverity_ShowsNothing()
    {
        var (svc, toasts) = Create();

        svc.Notify(new ApiError { Notify = true, Severity = ErrorSeverity.Silent, UserMessage = "x" });

        Assert.Empty(toasts.Messages);
    }

    [Fact]
    public void Warning_ShowsSingleToast_WithSeverityAndMessage()
    {
        var (svc, toasts) = Create();

        svc.Notify(Warning(message: "E-posta zorunludur."));

        var msg = Assert.Single(toasts.Messages);
        Assert.Equal(NotificationSeverity.Warning, msg.Severity);
        Assert.Contains("E-posta zorunludur.", Convert.ToString(msg.Detail));
    }

    [Fact]
    public void Dedup_SameCodeAndMessage_ShownOnceWithinWindow()
    {
        var (svc, toasts) = Create();

        svc.Notify(Warning());
        svc.Notify(Warning()); // same errorCode+message, within the window → suppressed

        Assert.Single(toasts.Messages);
    }

    [Fact]
    public void Dedup_DifferentCode_NotMerged()
    {
        var (svc, toasts) = Create();

        svc.Notify(Warning(code: "VALIDATION"));
        svc.Notify(Warning(code: "FORBIDDEN", message: "Yetki yok."));

        Assert.Equal(2, toasts.Messages.Count);
    }

    [Fact]
    public void RateLimit_SuppressesBeyondLimitWithinOneSecond()
    {
        var (svc, toasts) = Create(options: new NotificationOptions
        {
            DedupWindow = TimeSpan.FromSeconds(5),
            RateLimitPerSecond = 2
        });

        // 5 different codes → dedup does not merge them; rate limit cuts off at 2.
        for (int i = 0; i < 5; i++)
            svc.Notify(Warning(code: $"CODE_{i}", message: $"mesaj {i}"));

        Assert.Equal(2, toasts.Messages.Count);
    }

    [Fact]
    public void Coalesce_AppendsSuppressedCountOnNextShow()
    {
        var clock = new ControllableTimeProvider(DateTimeOffset.UnixEpoch);
        var (svc, toasts) = Create(clock: clock, options: new NotificationOptions
        {
            DedupWindow = TimeSpan.FromMilliseconds(40),
            RateLimitPerSecond = 50
        });

        svc.Notify(Warning());                       // 1st show (t=0)
        svc.Notify(Warning());                       // within the window → suppressed, counted (1)
        clock.Advance(TimeSpan.FromMilliseconds(120)); // let the dedup window elapse
        svc.Notify(Warning());                       // shown again, should append "+1 benzer"

        Assert.Equal(2, toasts.Messages.Count);
        Assert.Contains("+1 benzer", Convert.ToString(toasts.Messages[^1].Detail));
    }

    [Fact]
    public void RoleBased_Admin_SeesCorrelationId()
    {
        var (svc, toasts) = Create(isAdmin: true);

        svc.Notify(Warning(correlationId: "corr-123"));

        var msg = Assert.Single(toasts.Messages);
        Assert.Contains("corr-123", Convert.ToString(msg.Detail));
    }

    [Fact]
    public void RoleBased_NormalUser_DoesNotSeeCorrelationId()
    {
        var (svc, toasts) = Create(isAdmin: false);

        svc.Notify(Warning(correlationId: "corr-123"));

        var msg = Assert.Single(toasts.Messages);
        Assert.DoesNotContain("corr-123", Convert.ToString(msg.Detail));
        Assert.DoesNotContain("CorrelationId", Convert.ToString(msg.Detail));
    }

    // ---- Eviction: the dedup dictionary must not grow without bound (memory leak fix) ----

    [Fact]
    public void Eviction_RemovesEntriesPastRetention()
    {
        var clock = new ControllableTimeProvider(DateTimeOffset.UnixEpoch);
        var (svc, _) = Create(clock: clock, options: new NotificationOptions
        {
            DedupWindow = TimeSpan.FromMilliseconds(10),
            DedupRetention = TimeSpan.FromMilliseconds(40),
            RateLimitPerSecond = 50
        });

        svc.Notify(Warning(code: "A"));
        svc.Notify(Warning(code: "B"));
        Assert.Equal(2, svc.TrackedDedupKeyCount);

        clock.Advance(TimeSpan.FromMilliseconds(120)); // let retention (40ms) elapse

        // A new notification triggers pruning → A and B are dropped, only C remains.
        svc.Notify(Warning(code: "C"));

        Assert.Equal(1, svc.TrackedDedupKeyCount);
    }

    [Fact]
    public void Eviction_EnforcesHardCap_OnDistinctKeyFlood()
    {
        var (svc, _) = Create(options: new NotificationOptions
        {
            DedupWindow = TimeSpan.FromSeconds(5),
            DedupRetention = TimeSpan.FromMinutes(10), // time-based eviction disabled
            RateLimitPerSecond = 1000,
            MaxDedupEntries = 5
        });

        for (int i = 0; i < 50; i++)
            svc.Notify(Warning(code: $"CODE_{i}", message: $"mesaj {i}"));

        // Even though 50 unique keys are seen, the dictionary stays at the hard cap.
        Assert.Equal(5, svc.TrackedDedupKeyCount);
    }

    [Fact]
    public void Eviction_RetentionFlooredToDedupWindow_DoesNotBreakDedup()
    {
        // Even if Retention < DedupWindow is given, an entry within the window must not be dropped early
        // and break dedup: retention is raised to at least DedupWindow.
        var clock = new ControllableTimeProvider(DateTimeOffset.UnixEpoch);
        var (svc, toasts) = Create(clock: clock, options: new NotificationOptions
        {
            DedupWindow = TimeSpan.FromMilliseconds(150),
            DedupRetention = TimeSpan.FromMilliseconds(10), // deliberately very small
            RateLimitPerSecond = 50
        });

        svc.Notify(Warning(code: "A"));
        clock.Advance(TimeSpan.FromMilliseconds(60)); // greater than DedupRetention but smaller than DedupWindow
        svc.Notify(Warning(code: "A")); // still within the window → should be suppressed

        Assert.Single(toasts.Messages);
    }
}
