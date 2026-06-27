using LogPulse.Client.Logging;
using LogPulse.Client.Notifications;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Radzen;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// ---- Correlation: ambient holder for the id read from the response header ----
// Since Serilog is configured before the DI container is built, we create the instance manually and
// give the same reference to both the enricher (startup) and DI (handler).
var correlationAccessor = new ClientCorrelationAccessor();
builder.Services.AddSingleton(correlationAccessor);

// ---- Serilog: Async → Http batch sink (the UI is not blocked) ----
ClientLogging.ConfigureSerilog(builder, correlationAccessor);

// ---- Radzen (toast + modal) ----
builder.Services.AddRadzenComponents();

// ---- Notification pipeline ----
builder.Services.AddSingleton(TimeProvider.System); // clock for NotificationService dedup/rate-limit
builder.Services.Configure<NotificationOptions>(_ => { /* defaults; override if desired */ });
builder.Services.AddSingleton<ICurrentUser>(new CurrentUser { IsAdmin = false });
builder.Services.AddScoped<INotificationService, LogPulse.Client.Notifications.NotificationService>();
builder.Services.AddScoped<HubConnectionService>();

// ---- HttpClient + handler chain (WASM-compatible via IHttpClientFactory) ----
// Order matters: CorrelationScopeHandler OUTERMOST → the id is established and carried to the server before
// ProblemDetailsHandler and the other downstream handlers run; this way the in-request logs and the response
// interpreter see the same CorrelationId.
builder.Services.AddScoped<CorrelationScopeHandler>();
builder.Services.AddScoped<ProblemDetailsHandler>();
builder.Services.AddHttpClient("api", c => c.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress))
    .AddHttpMessageHandler<CorrelationScopeHandler>()
    .AddHttpMessageHandler<ProblemDetailsHandler>();

// The HttpClient that components inject directly is the interpreted "api" client.
builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("api"));

await builder.Build().RunAsync();
