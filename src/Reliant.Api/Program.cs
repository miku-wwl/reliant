using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Reliant.Application;
using Reliant.Application.Observability;
using Reliant.Infrastructure;
using Reliant.Infrastructure.Observability;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    options.UseUtcTimestamp = true;
});
builder.AddReliantObservability("Reliant.Api");

builder.Services.AddOpenApi();
builder.Services.AddControllers();

builder.Services.AddReliantApplication();
builder.Services.AddReliantInfrastructure(builder.Configuration);
builder.Services.AddHealthChecks()
    .AddCheck<PostgreSqlReadinessHealthCheck>(
        "postgresql",
        tags: ["ready"]);

builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("org-limit", opt =>
    {
        opt.PermitLimit = 100;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 0;
    });
    options.OnRejected = (context, ct) =>
    {
        context.HttpContext.Response.Headers["Retry-After"] = "60";
        ReliantTelemetry.RecordApiRateLimitRejection(
            context.HttpContext.Request.Path);
        return ValueTask.CompletedTask;
    };
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseMiddleware<Reliant.Api.Middleware.ExceptionHandlingMiddleware>();
app.UseMiddleware<Reliant.Api.Middleware.TenantMiddleware>();
app.UseRateLimiter();
app.MapControllers();
app.MapHealthChecks(
    "/health/live",
    new HealthCheckOptions
    {
        Predicate = _ => false
    });
app.MapHealthChecks(
    "/health/ready",
    new HealthCheckOptions
    {
        Predicate = registration =>
            registration.Tags.Contains("ready")
    });
app.MapGet(
    "/version",
    (DeploymentInfo deployment) => Results.Ok(deployment));

app.Run();

/// <summary>
/// Entry point for integration tests (WebApplicationFactory&lt;Program&gt;).
/// </summary>
public partial class Program
{
}
