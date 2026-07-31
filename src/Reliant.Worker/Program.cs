using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Reliant.Application;
using Reliant.Infrastructure;
using Reliant.Infrastructure.Persistence;
using Reliant.Worker.Handlers;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddReliantApplication();
builder.Services.AddReliantInfrastructure(builder.Configuration);

builder.Services.AddHostedService<OutboxPublisherService>();
builder.Services.AddHostedService<ProcessingHandlerService>();
builder.Services.AddHostedService<NotificationHandlerService>();
builder.Services.AddHostedService<ReconciliationHandlerService>();
builder.Services.AddHostedService<ScheduledMaintenanceHandlerService>();

var host = builder.Build();
host.Run();
