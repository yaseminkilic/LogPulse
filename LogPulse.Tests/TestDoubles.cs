using LogPulse.Client.Notifications;
using LogPulse.Shared.Errors;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace LogPulse.Tests;

/// <summary>
/// A <see cref="TimeProvider"/> whose time can be advanced manually. Makes the dedup/rate-limit/eviction
/// tests deterministic without a real <c>Task.Delay</c>.
/// </summary>
internal sealed class ControllableTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public ControllableTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>A fake host environment for controlling the environment name (Development/Production).</summary>
internal sealed class FakeHostEnvironment : IHostEnvironment
{
    public FakeHostEnvironment(string environmentName) => EnvironmentName = environmentName;

    public string EnvironmentName { get; set; }
    public string ApplicationName { get; set; } = "LogPulse.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

/// <summary>A fake notification service that captures the shown errors/successes (for assertions).</summary>
internal sealed class CapturingNotifier : INotificationService
{
    public List<ApiError> Errors { get; } = new();
    public List<string> Successes { get; } = new();

    public void Notify(ApiError error) => Errors.Add(error);
    public void Success(string message) => Successes.Add(message);
}

/// <summary>
/// A fake inner handler placed at the bottom of the <see cref="DelegatingHandler"/> chain that returns
/// a fixed response (or throws).
/// </summary>
internal sealed class StubInnerHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public StubInnerHandler(HttpResponseMessage response) => _responder = _ => response;
    public StubInnerHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_responder(request));
    }
}

/// <summary>A fake inner handler that always throws the given exception (network failure scenario).</summary>
internal sealed class ThrowingInnerHandler : HttpMessageHandler
{
    private readonly Exception _toThrow;
    public ThrowingInnerHandler(Exception toThrow) => _toThrow = toThrow;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => throw _toThrow;
}
