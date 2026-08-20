using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Reliant.Application.Abstractions;
using Reliant.Application.Observability;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;
using System.Text.Json;
using System.Diagnostics;

namespace Reliant.Worker.Handlers;

public class NotificationHandlerService(
    IServiceProvider serviceProvider,
    ILogger<NotificationHandlerService> logger) : BackgroundService
{
    private const string QueueName = "reliant-notification";
    private const int Concurrency = 5;
    private const int VisibilityTimeoutSeconds = 35;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Notification Handler started");

        using var semaphore = new SemaphoreSlim(Concurrency, Concurrency);

        while (!stoppingToken.IsCancellationRequested)
        {
            await semaphore.WaitAsync(stoppingToken);

            _ = Task.Run(async () =>
            {
                var telemetryStartedAt = Stopwatch.GetTimestamp();
                var telemetryActive = false;
                var telemetryResult = "failure";
                try
                {
                    using var scope = serviceProvider.CreateScope();
                    var queueAdapter = scope.ServiceProvider.GetRequiredService<IQueueAdapter>();
                    var inboxRepo = scope.ServiceProvider.GetRequiredService<IInboxRepository>();
                    var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                    var queueUrl = await queueAdapter.GetOrCreateQueueAsync(QueueName, stoppingToken);
                    var message = await queueAdapter.ReceiveAsync(queueUrl, VisibilityTimeoutSeconds, stoppingToken);

                    if (message is null) return;

                    telemetryActive = true;
                    ReliantTelemetry.ChangeWorkerInflight(
                        "notification",
                        1);
                    using var consumerActivity =
                        ReliantTelemetry.StartQueueConsumer(
                            message,
                            "notification",
                            "notification");
                    using var logScope = logger.BeginScope(
                        new Dictionary<string, object?>
                        {
                            ["CorrelationId"] = message.CorrelationId,
                            ["CausationId"] = message.CausationId,
                            ["MessageId"] = message.MessageId,
                            ["SqsPhysicalMessageId"] =
                                message.PhysicalMessageId,
                            ["MessageType"] = message.MessageType
                        });

                    logger.LogInformation("Notification message {MessageId}", message.MessageId);

                    var existing = await inboxRepo.GetByMessageIdAsync(message.MessageId, stoppingToken);
                    if (existing is { Status: InboxStatus.Processed })
                    {
                        await queueAdapter.DeleteAsync(queueUrl, message.ReceiptHandle, stoppingToken);
                        telemetryResult = "success";
                        consumerActivity?.SetStatus(ActivityStatusCode.Ok);
                        return;
                    }

                    try
                    {
                        var payload = JsonDocument.Parse(message.Payload);
                        var organizationId = payload.RootElement.GetProperty("organizationId").GetGuid();

                        TenantFilterAccessor.SetOrganizationId(organizationId);

                        logger.LogInformation("Sending notification for message {MessageId}", message.MessageId);
                        using var deliveryActivity =
                            ReliantTelemetry.StartActivity(
                                "notification deliver",
                                ActivityKind.Client);
                        deliveryActivity?.SetTag(
                            "reliant.notification.channel",
                            "sandbox_webhook");

                        var inboxMessage = new InboxMessage
                        {
                            Id = Guid.NewGuid(),
                            MessageId = message.MessageId,
                            OrganizationId = organizationId,
                            MessageType = message.MessageType,
                            HandlerName = "NotificationHandler",
                            HandlerVersion = "1.0",
                            ProcessedAt = DateTime.UtcNow,
                            Status = InboxStatus.Processed
                        };
                        await inboxRepo.AddAsync(inboxMessage, stoppingToken);
                        await unitOfWork.SaveChangesAsync(stoppingToken);
                        await queueAdapter.DeleteAsync(queueUrl, message.ReceiptHandle, stoppingToken);
                        telemetryResult = "success";
                        deliveryActivity?.SetStatus(ActivityStatusCode.Ok);
                        consumerActivity?.SetStatus(ActivityStatusCode.Ok);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Notification error for {MessageId}", message.MessageId);
                    }
                    finally
                    {
                        TenantFilterAccessor.Clear();
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Notification handler task error");
                }
                finally
                {
                    if (telemetryActive)
                    {
                        ReliantTelemetry.ChangeWorkerInflight(
                            "notification",
                            -1);
                        ReliantTelemetry.RecordWorkerRun(
                            "notification",
                            telemetryResult,
                            Stopwatch.GetElapsedTime(
                                telemetryStartedAt));
                    }
                    semaphore.Release();
                }
            }, stoppingToken);

            try { await Task.Delay(100, stoppingToken); } catch (OperationCanceledException) { }
        }

        logger.LogInformation("Notification Handler stopped");
    }
}
