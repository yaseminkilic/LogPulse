using LogPulse.Logging;
using LogPulse.Middleware;
using LogPulse.Shared.Errors;
using LogPulse.Shared.Logging;

namespace LogPulse.Demo;

/// <summary>
/// Demo endpoints that trigger each classification branch over HTTP. Removed in production;
/// they exist here to demonstrate the client interceptor + NotificationService behavior.
/// </summary>
public static class DemoEndpoints
{
    public static IEndpointRouteBuilder MapDemoEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/demo");

        g.MapGet("/ok", () => Results.Ok(new { message = "Her şey yolunda." }));

        // To view the persisted "important" logs (demo/admin).
        g.MapGet("/logs", async (ILogStore store) => Results.Ok(await store.GetRecentAsync(50)));

        g.MapGet("/validation", () =>
        {
            throw new ValidationException("Form geçersiz.", new Dictionary<string, string[]>
            {
                ["Email"] = new[] { "Geçerli bir e-posta girin." },
                ["Age"] = new[] { "Yaş 18'den büyük olmalı." }
            });
        });

        g.MapGet("/business", () =>
        {
            throw new BusinessException("STOCK_INSUFFICIENT", "Stok yetersiz, sipariş tamamlanamadı.");
        });

        g.MapGet("/forbidden", () =>
        {
            throw new UnauthorizedAccessException();
        });

        g.MapGet("/cancelled", (CancellationToken ct) =>
        {
            throw new OperationCanceledException();
        });

        // "Error while fetching/saving data" scenario — unexpected → 500, critical category.
        g.MapGet("/unhandled", () =>
        {
            throw new InvalidOperationException("Veri kaydedilirken beklenmedik hata (demo).");
        });

        // Multi-step end-to-end scenario: leaves several server traces under a single request
        // (= a single CorrelationId) and then blows up. Clicking a row in /admin/logs, the "related logs"
        // panel shows these steps + the middleware's 500 + the client's trace on a single timeline.
        g.MapGet("/order", async (HttpContext context, LogPipeline pipeline) =>
        {
            var correlationId = context.Items[CorrelationIdMiddleware.ItemsKey] as string;

            LogRecord Step(LogLevel level, string message, string? category) => new(
                Timestamp: DateTimeOffset.UtcNow,
                Level: level,
                Message: message,
                Exception: null,
                CorrelationId: correlationId,
                Category: category,
                Source: "Server",
                PropertiesJson: null);

            // DataAccess category: always persisted regardless of level (keep the trace visible).
            await pipeline.EnqueueAsync(Step(LogLevel.Information, "Sipariş isteği alındı (ürün=SKU-42, adet=3).", LogCategories.DataAccess));
            await pipeline.EnqueueAsync(Step(LogLevel.Information, "Stok sorgulanıyor: SKU-42.", LogCategories.DataAccess));
            await pipeline.EnqueueAsync(Step(LogLevel.Warning, "Stok yetersiz (mevcut=1, istenen=3); backorder deneniyor.", null));

            // Unexpected error → ClassifiedExceptionHandler logs this as a 500 (Critical) under the same
            // CorrelationId; the client also adds its trace from the ProblemDetails.
            throw new InvalidOperationException("Ödeme sağlayıcısı zaman aşımına uğradı (demo).");
        });

        // Real DB CRUD crash: a single REAL data access error. An invalid quantity (0)
        // violates the table's CHECK(Quantity > 0) constraint → a real SqliteException.
        // NO manual logging: the error flows to the middleware, is persisted as a 500
        // (Critical) under the request's CorrelationId; the client's ProblemDetailsHandler adds its trace →
        // a Server + Client linked trace from a single error in /admin/logs.
        g.MapGet("/crud-crash", async (DemoOrderRepository orders, CancellationToken ct) =>
        {
            await orders.EnsureCreatedAsync(ct);
            // Invalid data during Create (the C of CRUD) → the DB rejects it, the real error flows up.
            await orders.CreateAsync(sku: "SKU-42", customer: "Acme A.Ş.", quantity: 0, ct);
            return Results.Ok(); // unreachable: the previous line throws SqliteException
        });

        return app;
    }
}
