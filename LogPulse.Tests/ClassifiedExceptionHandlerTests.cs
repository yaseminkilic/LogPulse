using System.Text.Json;
using LogPulse.Logging;
using LogPulse.Middleware;
using LogPulse.Shared.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LogPulse.Tests;

/// <summary>
/// <see cref="ClassifiedExceptionHandler"/> (.NET 8 <c>IExceptionHandler</c>) black-box tests:
/// correct status code + rich ProblemDetails generation based on classification, API/page distinction
/// (<c>IsApiRequest</c>), cancellation swallowing and technical detail leak check in Development/Production.
/// <c>TryHandleAsync</c> is called directly (the UseExceptionHandler pipeline is the framework's responsibility).
/// </summary>
public class ClassifiedExceptionHandlerTests
{
    private sealed record Result(DefaultHttpContext Ctx, string Body, bool Handled)
    {
        public int Status => Ctx.Response.StatusCode;
        public JsonElement Json => JsonDocument.Parse(Body).RootElement;
    }

    private static async Task<Result> Run(
        Exception exception,
        string path = "/api/orders",
        string? accept = null,
        string? xRequestedWith = null,
        bool isDevelopment = false,
        bool clientAborted = false,
        string? correlationId = "fixed-corr")
    {
        var pipeline = new LogPipeline(Options.Create(new LogIngestOptions()), NullLogger<LogPipeline>.Instance);
        var env = new FakeHostEnvironment(isDevelopment ? "Development" : "Production");
        var handler = new ClassifiedExceptionHandler(pipeline, env);

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Request.Method = "GET";
        if (accept is not null) ctx.Request.Headers.Accept = accept;
        if (xRequestedWith is not null) ctx.Request.Headers.XRequestedWith = xRequestedWith;
        if (correlationId is not null) ctx.Items["CorrelationId"] = correlationId;
        ctx.Response.Body = new MemoryStream();
        if (clientAborted)
        {
            var cts = new CancellationTokenSource();
            cts.Cancel();
            ctx.RequestAborted = cts.Token;
        }

        var handled = await handler.TryHandleAsync(ctx, exception, CancellationToken.None);

        ctx.Response.Body.Position = 0;
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        return new Result(ctx, body, handled);
    }

    [Fact]
    public async Task UnhandledException_Api_Writes500ProblemDetails()
    {
        var r = await Run(new InvalidOperationException("iç detay sızmamalı"));

        Assert.True(r.Handled);
        Assert.Equal(500, r.Status);
        Assert.Equal("application/problem+json", r.Ctx.Response.ContentType);
        Assert.Equal(ErrorCodes.Unhandled, r.Json.GetProperty("errorCode").GetString());
        Assert.Equal((int)ErrorSeverity.Error, r.Json.GetProperty("severity").GetInt32());
        Assert.True(r.Json.GetProperty("notify").GetBoolean());
        Assert.Equal("fixed-corr", r.Json.GetProperty("correlationId").GetString());
        Assert.Equal("Beklenmeyen bir hata oluştu.", r.Json.GetProperty("userMessage").GetString());
    }

    [Fact]
    public async Task ValidationException_Api_Writes400_WithValidationErrors()
    {
        var errors = new Dictionary<string, string[]> { ["Email"] = new[] { "Zorunlu." } };
        var r = await Run(new ValidationException(errors));

        Assert.Equal(400, r.Status);
        Assert.Equal(ErrorCodes.Validation, r.Json.GetProperty("errorCode").GetString());
        Assert.True(r.Json.TryGetProperty("validationErrors", out var ve));
        Assert.Equal("Zorunlu.", ve.GetProperty("Email")[0].GetString());
    }

    [Fact]
    public async Task BusinessException_Api_Writes422_WithDomainCode()
    {
        var r = await Run(new BusinessException("STOCK_INSUFFICIENT", "Yeterli stok yok."));

        Assert.Equal(422, r.Status);
        Assert.Equal("STOCK_INSUFFICIENT", r.Json.GetProperty("errorCode").GetString());
        Assert.Equal("Yeterli stok yok.", r.Json.GetProperty("userMessage").GetString());
    }

    [Fact]
    public async Task PageRequest_RedirectsToErrorPage()
    {
        // Browser navigation (text/html), non-/api path → redirect to /Error.
        var r = await Run(new InvalidOperationException("x"), path: "/dashboard", accept: "text/html");

        Assert.Equal(302, r.Status);
        Assert.Equal("/Error", r.Ctx.Response.Headers.Location);
        Assert.Equal("", r.Body);
    }

    [Fact]
    public async Task XhrRequest_TreatedAsApi_EvenWithHtmlAccept()
    {
        var r = await Run(new InvalidOperationException("x"),
            path: "/dashboard", accept: "text/html", xRequestedWith: "XMLHttpRequest");

        Assert.Equal(500, r.Status);
        Assert.Equal("application/problem+json", r.Ctx.Response.ContentType);
    }

    [Fact]
    public async Task PageRequest_EmptyAccept_RedirectsToErrorPage_NotJson()
    {
        // Page path + no Accept: real navigation always carries text/html, whereas an empty Accept
        // is ambiguous → assume browser navigation → /Error (old behavior returned raw JSON).
        var r = await Run(new InvalidOperationException("x"), path: "/dashboard", accept: null);

        Assert.Equal(302, r.Status);
        Assert.Equal("/Error", r.Ctx.Response.Headers.Location);
        Assert.Equal("", r.Body);
    }

    [Fact]
    public async Task PageRequest_WildcardAccept_RedirectsToErrorPage()
    {
        // fetch() default "*/*": neither JSON nor text/html → no explicit data signal → treat as page.
        var r = await Run(new InvalidOperationException("x"), path: "/dashboard", accept: "*/*");

        Assert.Equal(302, r.Status);
        Assert.Equal("/Error", r.Ctx.Response.Headers.Location);
    }

    [Fact]
    public async Task PagePath_JsonAccept_TreatedAsApi()
    {
        // Even on a page path, if the client explicitly requests JSON, return rich ProblemDetails.
        var r = await Run(new InvalidOperationException("x"), path: "/dashboard", accept: "application/json");

        Assert.Equal(500, r.Status);
        Assert.Equal("application/problem+json", r.Ctx.Response.ContentType);
    }

    [Fact]
    public async Task ServerSideCancellation_Swallowed_Sets499_NoBody()
    {
        // Server-side cancellation (client still connected): consistent 499, nothing shown to the user.
        var r = await Run(new OperationCanceledException(), clientAborted: false);

        Assert.True(r.Handled);
        Assert.Equal(499, r.Status);
        Assert.Equal("", r.Body);
    }

    [Fact]
    public async Task ClientAbortedRequest_Swallowed_NoResponseWrite()
    {
        // Client actually disconnected → don't attempt to write; the body stays empty.
        var r = await Run(new OperationCanceledException(), clientAborted: true);

        Assert.True(r.Handled);
        Assert.Equal("", r.Body);
    }

    [Fact]
    public async Task DetailExtension_OnlyInDevelopment()
    {
        var dev = await Run(new InvalidOperationException("gizli iz"), isDevelopment: true);
        Assert.True(dev.Json.TryGetProperty("detail", out var detail));
        Assert.Contains("gizli iz", detail.GetString());

        var prod = await Run(new InvalidOperationException("gizli iz"), isDevelopment: false);
        Assert.False(prod.Json.TryGetProperty("detail", out _)); // does not leak in Production
    }

    [Fact]
    public async Task MissingCorrelationId_StillProducesValidProblemDetails()
    {
        // If CorrelationId is absent in Items, a fallback Guid is generated; the field must still be populated.
        var r = await Run(new InvalidOperationException("x"), correlationId: null);

        Assert.Equal(500, r.Status);
        var corr = r.Json.GetProperty("correlationId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(corr));
    }

    [Fact]
    public async Task LogAndResponse_ShareSameCorrelationId_WhenItemsMissing()
    {
        // Even when Items is empty, the log record and response must carry the SAME id (double-fallback-Guid fix).
        var pipeline = new LogPipeline(Options.Create(new LogIngestOptions()), NullLogger<LogPipeline>.Instance);
        var handler = new ClassifiedExceptionHandler(pipeline, new FakeHostEnvironment("Production"));

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/orders";
        ctx.Request.Method = "GET";
        ctx.Response.Body = new MemoryStream();
        // Items["CorrelationId"] is DELIBERATELY left unset → triggers the fallback path.

        await handler.TryHandleAsync(ctx, new InvalidOperationException("boom"), CancellationToken.None);

        ctx.Response.Body.Position = 0;
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        var responseId = JsonDocument.Parse(body).RootElement.GetProperty("correlationId").GetString();

        // 500 → Critical category → the record is written to the persistence channel.
        Assert.True(pipeline.Reader.TryRead(out var record));
        Assert.False(string.IsNullOrWhiteSpace(responseId));
        Assert.Equal(record!.CorrelationId, responseId); // resolved from a single source → equal
    }
}
