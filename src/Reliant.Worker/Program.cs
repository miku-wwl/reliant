using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Reliant.Application;
using Reliant.Infrastructure;
using Reliant.Infrastructure.Persistence;
using Reliant.Worker.Handlers;
using Reliant.Worker.Scheduling;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddReliantApplication();
builder.Services.AddReliantInfrastructure(builder.Configuration);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IRetryScheduler, RetrySchedulerService>();
builder.Services.AddSingleton<OperationalHistoryTelemetry>();
builder.Services.AddSingleton<
    IOperationalHistoryCleanupFaultInjector,
    NoopOperationalHistoryCleanupFaultInjector>();
builder.Services.AddScoped<OperationalHistoryCleanupService>();

builder.Services.AddHostedService<OutboxPublisherService>();
builder.Services.AddHostedService<ProcessingHandlerService>();
builder.Services.AddHostedService<NotificationHandlerService>();
builder.Services.AddHostedService<ReconciliationHandlerService>();
builder.Services.AddHostedService<ScheduledMaintenanceHandlerService>();

var host = builder.Build();
host.Run();
