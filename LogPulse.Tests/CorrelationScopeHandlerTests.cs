using System.Net;
using LogPulse.Client.Logging;
using LogPulse.Shared.Logging;
using Xunit;

namespace LogPulse.Tests;

/// <summary>
/// Client-originated correlation: <see cref="CorrelationScopeHandler"/> assigns a flow-local id to
/// each request, carries it to the server via <c>X-Correlation-Id</c>, and scopes the
/// <see cref="ClientCorrelationAccessor"/> (AsyncLocal) without leaking it to the caller. Verifies that
/// the old shared-field race has been resolved.
/// </summary>
public class CorrelationScopeHandlerTests
{
    private static CorrelationScopeHandler Handler(
        ClientCorrelationAccessor accessor,
        Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(accessor) { InnerHandler = new StubInnerHandler(responder) };

    private static HttpRequestMessage Request() => new(HttpMethod.Get, "http://localhost/api/orders");

    [Fact]
    public async Task GeneratesId_AddsHeader_AndScopesDuringSend()
    {
        var accessor = new ClientCorrelationAccessor();
        string? headerAtSend = null;
        string? currentAtSend = null;

        var handler = Handler(accessor, req =>
        {
            headerAtSend = req.Headers.TryGetValues(CorrelationConstants.HeaderName, out var v) ? v.First() : null;
            currentAtSend = accessor.Current; // the id visible in the flow at send time
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(Request(), CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(headerAtSend));     // header was added
        Assert.Equal(headerAtSend, currentAtSend);                 // header and accessor have the same id
    }

    [Fact]
    public async Task DoesNotLeakScopeToCaller()
    {
        var accessor = new ClientCorrelationAccessor();
        var handler = Handler(accessor, _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(Request(), CancellationToken.None);

        // async handler → the AsyncLocal assignment does not leak to the caller (the test flow).
        Assert.Null(accessor.Current);
    }

    [Fact]
    public async Task ReusesExistingScope_WhenAlreadySet()
    {
        var accessor = new ClientCorrelationAccessor();
        string? headerAtSend = null;
        var handler = Handler(accessor, req =>
        {
            headerAtSend = req.Headers.TryGetValues(CorrelationConstants.HeaderName, out var v) ? v.First() : null;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        accessor.Current = "preset-scope"; // e.g. a nested call / a scope opened higher up
        using var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(Request(), CancellationToken.None);

        Assert.Equal("preset-scope", headerAtSend); // does not generate a new Guid, continues the existing trace
    }

    [Fact]
    public async Task RespectsExplicitRequestHeader()
    {
        var accessor = new ClientCorrelationAccessor();
        string? headerAtSend = null;
        string? currentAtSend = null;
        var handler = Handler(accessor, req =>
        {
            headerAtSend = req.Headers.GetValues(CorrelationConstants.HeaderName).First();
            currentAtSend = accessor.Current;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var req = Request();
        req.Headers.TryAddWithoutValidation(CorrelationConstants.HeaderName, "explicit-id");
        using var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(req, CancellationToken.None);

        Assert.Equal("explicit-id", headerAtSend);     // explicit header is preserved
        Assert.Equal("explicit-id", currentAtSend);    // the accessor aligns with it too
    }

    [Fact]
    public async Task ConcurrentFlows_DoNotShareCorrelationId()
    {
        // AsyncLocal isolation: two separate async flows over the same accessor each see their own id.
        var accessor = new ClientCorrelationAccessor();

        async Task<string?> Flow(string id)
        {
            accessor.Current = id;
            await Task.Yield();      // force interleaving
            await Task.Delay(5);
            return accessor.Current; // should still be its own id
        }

        var results = await Task.WhenAll(Flow("A"), Flow("B"));

        Assert.Contains("A", results);
        Assert.Contains("B", results);
        Assert.Null(accessor.Current); // no flow leaked into the test context
    }
}
