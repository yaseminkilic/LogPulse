using Microsoft.AspNetCore.SignalR;

namespace LogPulse.Hubs;

/// <summary>
/// Data-stream / notification hub. Pushes live notifications to clients based on the database
/// connection and the user's authorization. Error handling is delegated to <see cref="ClassifiedHubFilter"/>
/// (the same classification logic as HTTP).
/// </summary>
public sealed class NotificationHub : Hub
{
    private readonly ILogger<NotificationHub> _logger;

    public NotificationHub(ILogger<NotificationHub> logger) => _logger = logger;

    /// <summary>A method, triggered by the client for demo purposes, that blows up inside the hub.</summary>
    public Task<string> Ping(bool throwError)
    {
        if (throwError)
            throw new InvalidOperationException("Hub metodu içinde beklenmedik hata (demo).");
        return Task.FromResult("pong");
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Hub bağlantısı kuruldu: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // A disconnect matters for the data stream → it's tracked. (The client handles it with a banner.)
        if (exception is not null)
            _logger.LogWarning(exception, "Hub bağlantısı hatayla koptu: {ConnectionId}", Context.ConnectionId);
        else
            _logger.LogInformation("Hub bağlantısı kapandı: {ConnectionId}", Context.ConnectionId);

        await base.OnDisconnectedAsync(exception);
    }
}
