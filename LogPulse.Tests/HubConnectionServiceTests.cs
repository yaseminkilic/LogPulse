using System.Text.Json;
using LogPulse.Client.Notifications;
using LogPulse.Shared.Errors;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace LogPulse.Tests;

/// <summary>
/// <see cref="HubConnectionService.ParseHubError"/> — extracts the <see cref="ApiError"/> JSON that the
/// server's <c>ClassifiedHubFilter</c> embeds in the <see cref="HubException"/> message.
/// Since SignalR prepends a "HubException: " prefix to the message, the parser starts from the first '{'.
/// </summary>
public class HubConnectionServiceTests
{
    private static string Serialize(ApiError e) =>
        JsonSerializer.Serialize(e, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public void ParsesEmbeddedApiErrorJson()
    {
        var payload = Serialize(new ApiError
        {
            ErrorCode = "STOCK_INSUFFICIENT",
            Severity = ErrorSeverity.Warning,
            Notify = true,
            UserMessage = "Yeterli stok yok.",
            CorrelationId = "corr-1",
            StatusCode = 422
        });

        var result = HubConnectionService.ParseHubError(new HubException(payload));

        Assert.Equal("STOCK_INSUFFICIENT", result.ErrorCode);
        Assert.Equal(ErrorSeverity.Warning, result.Severity);
        Assert.Equal("Yeterli stok yok.", result.UserMessage);
        Assert.Equal("corr-1", result.CorrelationId);
        Assert.Equal(422, result.StatusCode);
    }

    [Fact]
    public void ParsesJson_EvenWithSignalRPrefix()
    {
        // SignalR typically delivers it in the form "HubException: {json}".
        var payload = "HubException: " + Serialize(new ApiError
        {
            ErrorCode = ErrorCodes.Unhandled,
            Severity = ErrorSeverity.Error,
            UserMessage = "Beklenmeyen bir hata oluştu."
        });

        var result = HubConnectionService.ParseHubError(new HubException(payload));

        Assert.Equal(ErrorCodes.Unhandled, result.ErrorCode);
        Assert.Equal(ErrorSeverity.Error, result.Severity);
    }

    [Fact]
    public void NonJsonMessage_FallsBackToHubUnhandled()
    {
        var result = HubConnectionService.ParseHubError(new HubException("düz metin hata, JSON yok"));

        Assert.Equal(ErrorCodes.HubUnhandled, result.ErrorCode);
        Assert.Equal(ErrorSeverity.Error, result.Severity);
        Assert.Equal("Canlı işlem sırasında bir hata oluştu.", result.UserMessage);
    }

    [Fact]
    public void MalformedJson_FallsBackToHubUnhandled()
    {
        var result = HubConnectionService.ParseHubError(new HubException("hata: { bozuk json"));

        Assert.Equal(ErrorCodes.HubUnhandled, result.ErrorCode);
        Assert.Equal(ErrorSeverity.Error, result.Severity);
    }
}
