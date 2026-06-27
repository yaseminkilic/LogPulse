using System.Net;
using System.Text;
using LogPulse.Client.Notifications;
using LogPulse.Shared.Errors;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LogPulse.Tests;

/// <summary>
/// Black-box tests of the client HTTP interceptor: it runs the response returned from a fake inner
/// handler through the real <see cref="ProblemDetailsHandler.SendAsync"/> flow and verifies the
/// <see cref="ApiError"/> handed to <see cref="INotificationService"/>.
/// This way the private <c>ParseProblem</c>/<c>Fallback</c> logic is tested through the public surface.
/// </summary>
public class ProblemDetailsHandlerTests
{
    private static async Task<(HttpResponseMessage resp, CapturingNotifier notifier)> Run(
        HttpResponseMessage inner, string url = "http://localhost/api/orders")
    {
        var notifier = new CapturingNotifier();
        var handler = new ProblemDetailsHandler(notifier, NullLogger<ProblemDetailsHandler>.Instance)
        {
            InnerHandler = new StubInnerHandler(inner)
        };
        using var invoker = new HttpMessageInvoker(handler);
        var resp = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), CancellationToken.None);
        return (resp, notifier);
    }

    private static HttpResponseMessage ProblemResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/problem+json") };

    [Fact]
    public async Task SuccessfulResponse_DoesNotNotify()
    {
        var (_, notifier) = await Run(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });

        Assert.Empty(notifier.Errors);
    }

    [Fact]
    public async Task Status499_IsSilent_NoNotification()
    {
        var (_, notifier) = await Run(new HttpResponseMessage((HttpStatusCode)499));

        Assert.Empty(notifier.Errors);
    }

    [Fact]
    public async Task RichProblemDetails_FullyParsed()
    {
        const string json = """
        {
          "status": 422,
          "title": "Yeterli stok yok.",
          "errorCode": "STOCK_INSUFFICIENT",
          "severity": 2,
          "notify": true,
          "userMessage": "Yeterli stok yok.",
          "correlationId": "corr-abc"
        }
        """;

        var (_, notifier) = await Run(ProblemResponse((HttpStatusCode)422, json));

        var err = Assert.Single(notifier.Errors);
        Assert.Equal("STOCK_INSUFFICIENT", err.ErrorCode);
        Assert.Equal(ErrorSeverity.Warning, err.Severity);
        Assert.True(err.Notify);
        Assert.Equal("Yeterli stok yok.", err.UserMessage);
        Assert.Equal("corr-abc", err.CorrelationId);
        Assert.Equal(422, err.StatusCode);
    }

    [Fact]
    public async Task NotifyFalse_StillForwardedToService_FlagPreserved()
    {
        // ProblemDetailsHandler does not suppress; the NotificationService applies the notify=false decision.
        const string json = """{ "errorCode": "X", "severity": 2, "notify": false, "userMessage": "m" }""";

        var (_, notifier) = await Run(ProblemResponse(HttpStatusCode.BadRequest, json));

        var err = Assert.Single(notifier.Errors);
        Assert.False(err.Notify);
    }

    [Fact]
    public async Task ValidationErrors_ParsedIntoDictionary()
    {
        const string json = """
        {
          "status": 400,
          "errorCode": "VALIDATION",
          "severity": 2,
          "userMessage": "Doğrulama hatası.",
          "validationErrors": { "Email": ["Zorunlu."], "Age": ["Pozitif olmalı.", "Sayı olmalı."] }
        }
        """;

        var (_, notifier) = await Run(ProblemResponse(HttpStatusCode.BadRequest, json));

        var err = Assert.Single(notifier.Errors);
        Assert.NotNull(err.ValidationErrors);
        Assert.Equal(2, err.ValidationErrors!["Age"].Length);
        Assert.Equal("Zorunlu.", err.ValidationErrors["Email"][0]);
    }

    [Fact]
    public async Task MissingSeverity_DefaultsToError()
    {
        const string json = """{ "errorCode": "X", "userMessage": "m" }""";

        var (_, notifier) = await Run(ProblemResponse(HttpStatusCode.InternalServerError, json));

        var err = Assert.Single(notifier.Errors);
        Assert.Equal(ErrorSeverity.Error, err.Severity);
    }

    [Theory]
    [InlineData(401, "UNAUTHORIZED", ErrorSeverity.Warning)]
    [InlineData(403, "FORBIDDEN", ErrorSeverity.Warning)]
    [InlineData(404, "NOT_FOUND", ErrorSeverity.Warning)]
    [InlineData(500, "UNHANDLED", ErrorSeverity.Error)]
    public async Task NoBody_FallsBackByStatus(int status, string expectedCode, ErrorSeverity expectedSeverity)
    {
        // No body/JSON → a fallback should be produced based on the status.
        var (_, notifier) = await Run(new HttpResponseMessage((HttpStatusCode)status));

        var err = Assert.Single(notifier.Errors);
        Assert.Equal(expectedCode, err.ErrorCode);
        Assert.Equal(expectedSeverity, err.Severity);
    }

    [Fact]
    public async Task MalformedJson_FallsBackToStatus()
    {
        // application/json but a malformed body → parsing blows up, is swallowed, and the fallback kicks in.
        var resp = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{ bozuk json", Encoding.UTF8, "application/json")
        };

        var (_, notifier) = await Run(resp);

        var err = Assert.Single(notifier.Errors);
        Assert.Equal(ErrorCodes.Unhandled, err.ErrorCode);
        Assert.Equal(ErrorSeverity.Error, err.Severity);
    }

    [Fact]
    public async Task NetworkFailure_NotifiesNetworkError_AndRethrows()
    {
        var notifier = new CapturingNotifier();
        var handler = new ProblemDetailsHandler(notifier, NullLogger<ProblemDetailsHandler>.Instance)
        {
            InnerHandler = new ThrowingInnerHandler(new HttpRequestException("bağlantı yok"))
        };
        using var invoker = new HttpMessageInvoker(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/x"), CancellationToken.None));

        var err = Assert.Single(notifier.Errors);
        Assert.Equal("NETWORK", err.ErrorCode);
        Assert.Equal(ErrorSeverity.Error, err.Severity);
    }

    [Fact]
    public async Task CancelledRequest_IsSilent_Rethrows_NoNotification()
    {
        var notifier = new CapturingNotifier();
        var handler = new ProblemDetailsHandler(notifier, NullLogger<ProblemDetailsHandler>.Instance)
        {
            InnerHandler = new StubInnerHandler(new HttpResponseMessage(HttpStatusCode.OK))
        };
        using var invoker = new HttpMessageInvoker(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/x"), cts.Token));

        Assert.Empty(notifier.Errors);
    }
}
