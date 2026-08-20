using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Reliant.Application;
using Reliant.Infrastructure;
using Reliant.Infrastructure.Observability;
using Reliant.Worker.Handlers;
using Reliant.Worker.Observability;
using Reliant.Worker.Scheduling;

namespace Reliant.Worker;

public static class WorkerProgram
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(
            builder.Configuration["Worker:HealthUrl"] ??
            "http://0.0.0.0:8081");
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
            options.UseUtcTimestamp = true;
        });
        builder.AddReliantObservability("Reliant.Worker");

        builder.Services.AddReliantApplication();
        builder.Services.AddReliantInfrastructure(
            builder.Configuration);
        builder.Services.AddHealthChecks()
            .AddCheck<PostgreSqlReadinessHealthCheck>(
                "postgresql",
                tags: ["ready"])
            .AddCheck<QueueReadinessHealthCheck>(
                "sqs",
                tags: ["ready"]);

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<IRetryScheduler,
            RetrySchedulerService>();
        builder.Services.AddSingleton<OperationalHistoryTelemetry>();
        builder.Services.AddSingleton<
            IOperationalHistoryCleanupFaultInjector,
            NoopOperationalHistoryCleanupFaultInjector>();
        builder.Services.AddScoped<OperationalHistoryCleanupService>();

        builder.Services.AddHostedService<OutboxPublisherService>();
        builder.Services.AddHostedService<ProcessingHandlerService>();
        builder.Services.AddHostedService<NotificationHandlerService>();
        builder.Services.AddHostedService<ReconciliationHandlerService>();
        builder.Services.AddHostedService<
            ScheduledMaintenanceHandlerService>();
        builder.Services.AddHostedService<RuntimeMetricsService>();

        var app = builder.Build();
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
    }
}
