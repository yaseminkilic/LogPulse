using LogPulse.Components;
using LogPulse.Demo;
using LogPulse.Hubs;
using LogPulse.Logging;
using LogPulse.Middleware;
using Microsoft.AspNetCore.SignalR;
using Radzen;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ---- Serilog (server observability) ----
builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

// ---- Services ----
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
    
builder.Services.AddRadzenComponents();

// Logging path: single entry point + SQLite persistence worker.
builder.Services.Configure<LogIngestOptions>(builder.Configuration.GetSection(LogIngestOptions.SectionName));
builder.Services.AddSingleton<LogPipeline>();
builder.Services.AddSingleton<ILogStore, SqliteLogStore>();
builder.Services.AddHostedService<LogPersistenceWorker>();

// Demo: real SQLite CRUD repository (crud-crash scenario). Removed in production.
builder.Services.AddSingleton<LogPulse.Demo.DemoOrderRepository>();

// Hub filter that applies the same classification as the HTTP side, for SignalR.
builder.Services.AddSingleton<ClassifiedHubFilter>();
builder.Services.AddSignalR(o => o.AddFilter<ClassifiedHubFilter>());

// Global error boundary: .NET 8 IExceptionHandler + UseExceptionHandler (instead of hand-written middleware).
// AddProblemDetails is required for the parameterless UseExceptionHandler; since the handler writes every
// case itself (returns true), the default ProblemDetails path is never taken.
builder.Services.AddExceptionHandler<ClassifiedExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();

// ---- Middleware order (CRITICAL) ----
// CorrelationId OUTERMOST: so the exception handler can always read Items["CorrelationId"].
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

// ---- Endpoints ----
app.MapLogIngest();        // client Serilog.Sinks.Http batches
app.MapAdminEndpoints();   // admin log viewer data endpoints
app.MapDemoEndpoints();    // demo endpoints that trigger the classification branches
app.MapHub<NotificationHub>("/hubs/notifications");

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(LogPulse.Client._Imports).Assembly);

app.Run();
