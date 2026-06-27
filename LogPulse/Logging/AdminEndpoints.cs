namespace LogPulse.Logging;

/// <summary>
/// Data endpoints for the admin log viewer. In a real application they must be
/// protected with <c>.RequireAuthorization("Admin")</c> (left open here for demo purposes).
/// </summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin");
        // g.RequireAuthorization("Admin");  // ← enable in production

        g.MapGet("/logs", async (
            ILogStore store,
            int? take,
            int? minLevel,
            string? category,
            string? source,
            string? search,
            string? correlationId,
            CancellationToken ct) =>
        {
            var query = new LogQuery(
                Take: take ?? 100,
                MinLevel: minLevel is { } m ? (LogLevel)m : null,
                Category: category,
                Source: source,
                Search: search,
                CorrelationId: correlationId);

            return Results.Ok(await store.QueryAsync(query, ct));
        });

        g.MapGet("/logs/categories", async (ILogStore store, CancellationToken ct) =>
            Results.Ok(await store.GetCategoriesAsync(ct)));

        return app;
    }
}
