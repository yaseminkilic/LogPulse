using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Serilog;
using Serilog.Events;

namespace LogPulse.Client.Logging;

/// <summary>
/// Client Serilog setup: <c>Async</c> (non-blocking queue) → <c>Http</c> (batch POST → API ingest).
/// <para>
/// WASM constraint: there is no real file system → the <c>Http</c> sink's durable (file-buffered) mode cannot be used;
/// only the in-memory batch is available. If the tab closes, unsent logs in the queue may be lost.
/// </para>
/// </summary>
public static class ClientLogging
{
    public static void ConfigureSerilog(WebAssemblyHostBuilder builder, ClientCorrelationAccessor correlationAccessor)
    {
        // The sink's own HttpClient cannot resolve a relative URI → build an absolute address.
        var ingestUri = $"{builder.HostEnvironment.BaseAddress}ingest/logs";

        var minLevel = builder.HostEnvironment.IsDevelopment()
            ? LogEventLevel.Debug
            : LogEventLevel.Information;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(minLevel)
            // Tone down framework noise.
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.Components", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            // Stamp every event with the request's server CorrelationId → client and server
            // logs meet on the same "related logs" trail in the admin viewer.
            .Enrich.With(new CorrelationEnricher(correlationAccessor))
            // Async: the UI must never block; under heavy load a few logs may be dropped (monitor drops).
            .WriteTo.Async(a => a.Http(
                requestUri: ingestUri,
                queueLimitBytes: 10 * 1024 * 1024,        // bound memory
                logEventsInBatchLimit: 50,                 // events per batch
                period: TimeSpan.FromSeconds(2)),          // send period
                bufferSize: 10_000,
                blockWhenFull: false)
            .CreateLogger();

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger, dispose: true);
    }
}
