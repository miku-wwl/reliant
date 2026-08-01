using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Reliant.Application.Abstractions;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;

namespace Reliant.Worker.Handlers;

public class OutboxPublisherService(
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    ILogger<OutboxPublisherService> logger) : BackgroundService
{
    private const int BatchSize = 50;
    private const int MaxSendAttempts = 10;
    private readonly string _defaultProcessingQueue = configuration["Queue:QueueName"] ?? "reliant-processing";
    private readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(
        configuration.GetValue<int?>("Worker:Outbox:IntervalMs") ?? 2000);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Outbox Publisher started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                var outboxRepo = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
                var queuePublisher = scope.ServiceProvider.GetRequiredService<IQueueMessagePublisher>();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                var pendingMessages = await outboxRepo.GetPendingAsync(BatchSize, stoppingToken);

                foreach (var message in pendingMessages)
                {
                    TenantFilterAccessor.SetOrganizationId(message.OrganizationId);
                    try
                    {
                        var queueName = message.MessageType switch
                        {
                            "ContributionSucceeded" => "reliant-notification",
                            _ => _defaultProcessingQueue
                        };

                        await queuePublisher.PublishAsync(queueName, message.MessageType, message.Payload, message.Id.ToString(), stoppingToken);
                        await outboxRepo.MarkAsSentAsync(message.Id, stoppingToken);
                        logger.LogInformation("Outbox message {MessageId} sent to {Queue}", message.Id, queueName);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to send outbox message {MessageId}", message.Id);
                        if (message.SendCount >= MaxSendAttempts - 1)
                        {
                            await outboxRepo.MarkAsFailedAsync(message.Id, stoppingToken);
                            logger.LogError("Outbox message {MessageId} marked as failed after {Attempts} attempts", message.Id, MaxSendAttempts);
                        }
                    }
                    finally
                    {
                        TenantFilterAccessor.Clear();
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox publisher error");
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException) { }
        }

        logger.LogInformation("Outbox Publisher stopped");
    }
}
