using System.Net;
using System.Text.Json;
using LogPulse.Shared.Errors;
using Microsoft.Extensions.Logging;
using Serilog.Context;

namespace LogPulse.Client.Notifications;

/// <summary>
/// HTTP response interpreter (<see cref="DelegatingHandler"/>). Reads the rich ProblemDetails from failed
/// responses and hands them to <see cref="INotificationService"/> — so that instead of "a dialog for every 500"
/// the server's <c>severity/notify</c> decision is applied.
/// It does not swallow the response; the calling code can continue its normal flow.
/// </summary>
public sealed class ProblemDetailsHandler : DelegatingHandler
{
    private readonly INotificationService _notifications;
    private readonly ILogger<ProblemDetailsHandler> _logger;

    public ProblemDetailsHandler(INotificationService notifications, ILogger<ProblemDetailsHandler> logger)
    {
        _notifications = notifications;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // User/cancellation: silent. Show no notification.
            throw;
        }
        catch (Exception ex)
        {
            // Network error: the server could not be reached → report a generic error.
            _logger.LogError(ex, "HTTP isteği başarısız: {Url}", request.RequestUri);
            _notifications.Notify(new ApiError
            {
                ErrorCode = "NETWORK",
                Severity = ErrorSeverity.Error,
                Notify = true,
                UserMessage = "Sunucuya ulaşılamadı. Bağlantınızı kontrol edin."
            });
            throw;
        }

        if (!response.IsSuccessStatusCode)
            await InterpretAsync(response, cancellationToken);

        return response;
    }

    private async Task InterpretAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // 499 = client cancellation; silent.
        if ((int)response.StatusCode == 499) return;

        ApiError? error = null;
        var contentType = response.Content.Headers.ContentType?.MediaType;

        if (contentType is "application/problem+json" or "application/json")
        {
            try
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                error = ParseProblem(json, response.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ProblemDetails ayrıştırılamadı.");
            }
        }

        // If there is no ProblemDetails, produce a generic error based on the status.
        error ??= Fallback(response.StatusCode);

        // Client-side trail: persist the error under the SAME CorrelationId as the server record
        // so it appears end to end (Client + Server) in the /admin/logs "related logs" panel.
        // We push the CorrelationId explicitly (without relying on the enricher, for deterministic matching).
        // Only Warning+ is persisted (server severity filter); Info/Silent should not make noise.
        if (error.Severity >= ErrorSeverity.Warning)
        {
            using (LogContext.PushProperty("CorrelationId", error.CorrelationId))
            {
                _logger.Log(ToLogLevel(error.Severity),
                    "İstemci sunucu hatasını aldı ve gösterdi: {ErrorCode} — {UserMessage} [HTTP {Status}]",
                    error.ErrorCode, error.UserMessage, error.StatusCode);
            }
        }

        _notifications.Notify(error);
    }

    /// <summary>Converts the notification-axis <see cref="ErrorSeverity"/> into a logging level.</summary>
    private static LogLevel ToLogLevel(ErrorSeverity severity) => severity switch
    {
        ErrorSeverity.Error => LogLevel.Error,
        ErrorSeverity.Warning => LogLevel.Warning,
        ErrorSeverity.Info => LogLevel.Information,
        _ => LogLevel.Debug
    };

    private static ApiError ParseProblem(string json, HttpStatusCode status)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var error = new ApiError
        {
            StatusCode = (int)status,
            ErrorCode = GetString(root, "errorCode") ?? ErrorCodes.Unhandled,
            UserMessage = GetString(root, "userMessage")
                          ?? GetString(root, "title")
                          ?? "Beklenmeyen bir hata oluştu.",
            CorrelationId = GetString(root, "correlationId"),
            Notify = !root.TryGetProperty("notify", out var n) || n.GetBoolean(),
            Severity = root.TryGetProperty("severity", out var s) && s.TryGetInt32(out var sv)
                ? (ErrorSeverity)sv
                : ErrorSeverity.Error
        };

        if (root.TryGetProperty("validationErrors", out var ve) && ve.ValueKind == JsonValueKind.Object)
        {
            var dict = new Dictionary<string, string[]>();
            foreach (var prop in ve.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Array)
                    dict[prop.Name] = prop.Value.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            }
            error.ValidationErrors = dict;
        }

        return error;
    }

    private static ApiError Fallback(HttpStatusCode status) => (int)status switch
    {
        401 => new ApiError { ErrorCode = "UNAUTHORIZED", Severity = ErrorSeverity.Warning, UserMessage = "Oturum açmanız gerekiyor." },
        403 => new ApiError { ErrorCode = ErrorCodes.Forbidden, Severity = ErrorSeverity.Warning, UserMessage = "Bu işlem için yetkiniz yok." },
        404 => new ApiError { ErrorCode = "NOT_FOUND", Severity = ErrorSeverity.Warning, UserMessage = "Kayıt bulunamadı." },
        _ => new ApiError { ErrorCode = ErrorCodes.Unhandled, Severity = ErrorSeverity.Error, UserMessage = "Beklenmeyen bir hata oluştu." }
    };

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
}
