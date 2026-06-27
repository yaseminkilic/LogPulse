using System.Text.Json;
using LogPulse.Shared.Errors;
using LogPulse.Shared.Logging;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace LogPulse.Client.Notifications;

public enum HubStatus { Disconnected, Connecting, Connected, Reconnecting }

/// <summary>
/// SignalR client wrapper. Separates two concerns:
/// <list type="bullet">
///   <item><description><b>Connection status</b> (Reconnecting/Closed) is <c>not an error</c> → it is handled with
///   a thin banner at the top rather than a dialog (<see cref="StatusChanged"/>). A drop also flows to the server
///   as an "important log" (category <see cref="LogCategories.HubConnection"/>).</description></item>
///   <item><description><b>Hub method errors</b> (<see cref="HubException"/>) carry the <see cref="ApiError"/> JSON
///   produced by the server's <c>ClassifiedHubFilter</c> → they enter the same <see cref="INotificationService"/>.</description></item>
/// </list>
/// </summary>
public sealed class HubConnectionService : IAsyncDisposable
{
    private readonly INotificationService _notifications;
    private readonly ILogger<HubConnectionService> _logger;
    private HubConnection? _connection;

    public HubConnectionService(INotificationService notifications, ILogger<HubConnectionService> logger)
    {
        _notifications = notifications;
        _logger = logger;
    }

    public HubStatus Status { get; private set; } = HubStatus.Disconnected;

    /// <summary>
    /// The correlation id for this connection. Generated when the connection is established and carried to the
    /// server in the negotiate request; the server hub filter reads it and logs all hub errors on this connection
    /// with the same id. Since SignalR does not support per-call metadata, the id is <b>connection</b> scoped
    /// (sufficient for the admin trail).
    /// </summary>
    public string? CorrelationId { get; private set; }

    /// <summary>Event the UI listens to in order to refresh the banner.</summary>
    public event Action? StatusChanged;

    public async Task StartAsync(string hubUrl)
    {
        if (_connection is not null) return;

        // Connection-scoped correlation id: added to the negotiate request as X-Correlation-Id.
        // The server hub filter (ClassifiedHubFilter) reads it from Context.GetHttpContext() → hub errors
        // on this connection are linked to the client trail.
        CorrelationId = Guid.NewGuid().ToString("N");

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
                options.Headers[CorrelationConstants.HeaderName] = CorrelationId)
            .WithAutomaticReconnect()   // default retries at 0,2,10,30 s
            .Build();

        // Connection status events — NOT an error, banner + important log.
        _connection.Reconnecting += error =>
        {
            SetStatus(HubStatus.Reconnecting);
            LogConnection("SignalR yeniden bağlanıyor", error);
            return Task.CompletedTask;
        };

        _connection.Reconnected += _ =>
        {
            SetStatus(HubStatus.Connected);
            _logger.LogInformation("SignalR yeniden bağlandı.");
            return Task.CompletedTask;
        };

        _connection.Closed += error =>
        {
            SetStatus(HubStatus.Disconnected);
            LogConnection("SignalR bağlantısı kapandı", error);
            return Task.CompletedTask;
        };

        SetStatus(HubStatus.Connecting);
        try
        {
            await _connection.StartAsync();
            SetStatus(HubStatus.Connected);
        }
        catch (Exception ex)
        {
            SetStatus(HubStatus.Disconnected);
            LogConnection("SignalR ilk bağlantı kurulamadı", ex);
        }
    }

    /// <summary>Invokes the hub method; converts a <see cref="HubException"/> into a classified notification.</summary>
    public async Task<T?> InvokeAsync<T>(string method, params object?[] args)
    {
        if (_connection is null || _connection.State != HubConnectionState.Connected)
        {
            _notifications.Notify(new ApiError
            {
                ErrorCode = ErrorCodes.HubDisconnected,
                Severity = ErrorSeverity.Warning,
                UserMessage = "Canlı bağlantı yok, işlem şu an yapılamıyor."
            });
            return default;
        }

        try
        {
            return await _connection.InvokeCoreAsync<T>(method, args);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not an error (local cancellation token / connection drop). A server-side cancel
            // already arrives as a Silent ApiError; this path catches the client-originated/raw cancel →
            // show nothing to the user, do not leak the raw OCE to the component.
            _logger.LogDebug("Hub çağrısı iptal edildi: {Method}", method);
            return default;
        }
        catch (HubException ex)
        {
            _notifications.Notify(ParseHubError(ex));
            return default;
        }
    }

    internal static ApiError ParseHubError(HubException ex)
    {
        // ClassifiedHubFilter sends the message as a JSON ApiError.
        var msg = ex.Message;
        var jsonStart = msg.IndexOf('{');
        if (jsonStart >= 0)
        {
            try
            {
                var json = msg[jsonStart..];
                var error = JsonSerializer.Deserialize<ApiError>(json,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (error is not null) return error;
            }
            catch { /* fall back if it is not JSON */ }
        }

        return new ApiError
        {
            ErrorCode = ErrorCodes.HubUnhandled,
            Severity = ErrorSeverity.Error,
            UserMessage = "Canlı işlem sırasında bir hata oluştu."
        };
    }

    private void LogConnection(string message, Exception? error)
    {
        // A drop matters for the data flow → stamped with a category, persisted on the server.
        using (_logger.BeginScope(new Dictionary<string, object>
               {
                   [LogCategories.PropertyName] = LogCategories.HubConnection
               }))
        {
            if (error is not null)
                _logger.LogWarning(error, "{Message}", message);
            else
                _logger.LogWarning("{Message}", message);
        }
    }

    private void SetStatus(HubStatus status)
    {
        Status = status;
        StatusChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}
