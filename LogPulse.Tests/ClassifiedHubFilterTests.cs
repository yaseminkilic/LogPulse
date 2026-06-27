using System.Text.Json;
using LogPulse.Hubs;
using LogPulse.Logging;
using LogPulse.Shared.Errors;
using LogPulse.Shared.Logging;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LogPulse.Tests;

/// <summary>
/// <see cref="ClassifiedHubFilter"/> — verifies that SignalR hub exceptions pass through the <b>same</b>
/// <see cref="ExceptionClassifier"/> as HTTP, are logged via a single pipeline, and are carried to the
/// client as <see cref="ApiError"/> JSON inside a <see cref="HubException"/>.
/// In particular, it tests that cancellation (OperationCanceledException) flows silently and that the
/// log and the response share the same CorrelationId.
/// </summary>
public class ClassifiedHubFilterTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static ClassifiedHubFilter Filter(out LogPipeline pipeline)
    {
        pipeline = new LogPipeline(Options.Create(new LogIngestOptions()), NullLogger<LogPipeline>.Instance);
        return new ClassifiedHubFilter(pipeline);
    }

    private static ApiError Parse(HubException ex) =>
        JsonSerializer.Deserialize<ApiError>(ex.Message, JsonOpts)!;

    [Fact]
    public async Task Cancellation_FlowsSilently_NotAsError()
    {
        var filter = Filter(out var pipeline);

        var hubEx = await filter.HandleAsync(new OperationCanceledException(), "NotificationHub", "Ping", "conn-corr");
        var error = Parse(hubEx);

        // Client interceptor: Silent / Notify=false → nothing is shown to the user.
        Assert.Equal(ErrorCodes.RequestCancelled, error.ErrorCode);
        Assert.Equal(ErrorSeverity.Silent, error.Severity);
        Assert.False(error.Notify);
        Assert.Equal(499, error.StatusCode);

        // Debug level + no category → does not enter the persistence queue (doesn't pollute SQLite).
        Assert.False(pipeline.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Unhandled_ProducesError_EnqueuesCriticalRecord_SharingCorrelationId()
    {
        var filter = Filter(out var pipeline);

        var hubEx = await filter.HandleAsync(
            new InvalidOperationException("boom"), "NotificationHub", "Ping", "conn-corr-123");
        var error = Parse(hubEx);

        Assert.Equal(ErrorCodes.Unhandled, error.ErrorCode);
        Assert.Equal(ErrorSeverity.Error, error.Severity);
        Assert.True(error.Notify);
        Assert.Equal(500, error.StatusCode);

        // 500 → Critical category → always persisted.
        Assert.True(pipeline.Reader.TryRead(out var record));
        Assert.Equal(LogLevel.Error, record.Level);
        Assert.Equal(LogCategories.Critical, record.Category);
        Assert.Equal("Hub", record.Source);
        Assert.Contains("[Hub NotificationHub.Ping]", record.Message);

        // The resolved connection id is carried verbatim to both the log record and the response sent to the client.
        Assert.Equal("conn-corr-123", record.CorrelationId);
        Assert.Equal("conn-corr-123", error.CorrelationId);
    }

    [Fact]
    public async Task BusinessException_MapsToWarning_WithUserMessage()
    {
        var filter = Filter(out _);

        var hubEx = await filter.HandleAsync(
            new BusinessException("STOCK_INSUFFICIENT", "Yeterli stok yok."), "NotificationHub", "PlaceOrder", "c1");
        var error = Parse(hubEx);

        Assert.Equal("STOCK_INSUFFICIENT", error.ErrorCode);
        Assert.Equal(ErrorSeverity.Warning, error.Severity);
        Assert.True(error.Notify);
        Assert.Equal("Yeterli stok yok.", error.UserMessage);
        Assert.Equal(422, error.StatusCode);
    }
}
