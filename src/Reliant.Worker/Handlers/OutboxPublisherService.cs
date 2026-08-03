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
    private readonly int _retryBaseMs = Math.Max(
        100,
        configuration.GetValue<int?>(
            "Worker:Outbox:RetryBaseMs") ?? 1000);
    private readonly int _retryCapMs = Math.Max(
        100,
        configuration.GetValue<int?>(
            "Worker:Outbox:RetryCapMs") ?? 30000);
    private readonly int _retryJitterMs = Math.Max(
        0,
        configuration.GetValue<int?>(
            "Worker:Outbox:RetryJitterMs") ?? 250);
    private int _brokerFailureStreak;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Outbox Publisher started");

        while (!stoppingToken.IsCancellationRequested)
        {
            var nextLoopDelay = _pollInterval;
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
                        _brokerFailureStreak = 0;
                        logger.LogInformation("Outbox message {MessageId} sent to {Queue}", message.Id, queueName);
                    }
                    catch (OperationCanceledException)
                        when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (QueuePublishException ex)
                    {
                        var failureCount =
                            await outboxRepo.RecordSendFailureAsync(
                                message.Id,
                                stoppingToken);
                        var nextStreak =
                            _brokerFailureStreak == int.MaxValue
                                ? int.MaxValue
                                : _brokerFailureStreak + 1;
                        _brokerFailureStreak = Math.Max(
                            nextStreak,
                            failureCount);
                        var retryDelay = GetRetryDelay(
                            _brokerFailureStreak);

                        logger.LogWarning(
                            ex,
                            "Outbox publish failure {MessageId}: attempt={Attempt}, category={ErrorCategory}, transient={IsTransient}; retry in {DelayMs} ms",
                            message.Id,
                            failureCount,
                            ex.ErrorCategory,
                            ex.IsTransient,
                            (long)retryDelay.TotalMilliseconds);

                        if (!ex.IsTransient &&
                            failureCount >= MaxSendAttempts)
                        {
                            await outboxRepo.MarkAsFailedAsync(
                                message.Id,
                                stoppingToken);
                            _brokerFailureStreak = 0;
                            logger.LogError(
                                "Outbox message {MessageId} marked as failed after {Attempts} permanent failures",
                                message.Id,
                                failureCount);
                        }
                        else
                        {
                            nextLoopDelay = retryDelay;
                        }

                        // A broker outage affects the whole batch. Stop after one
                        // failed publish instead of hammering it once per message.
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Publishing may already have succeeded if this is a
                        // database state-update failure. Leave the message Pending;
                        // Inbox deduplication makes a later redelivery safe.
                        logger.LogError(
                            ex,
                            "Outbox message {MessageId} handling failed; delivery outcome may be unknown",
                            message.Id);
                        nextLoopDelay = GetRetryDelay(1);
                        break;
                    }
                    finally
                    {
                        TenantFilterAccessor.Clear();
                    }
                }
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox publisher error");
            }

            try
            {
                await Task.Delay(nextLoopDelay, stoppingToken);
            }
            catch (OperationCanceledException) { }
        }

        logger.LogInformation("Outbox Publisher stopped");
    }

    private TimeSpan GetRetryDelay(int failureStreak)
    {
        var exponent = Math.Min(
            Math.Max(0, failureStreak - 1),
            30);
        var exponentialDelay = _retryBaseMs *
            Math.Pow(2, exponent);
        var boundedDelay = Math.Min(
            Math.Max(_retryBaseMs, _retryCapMs),
            exponentialDelay);
        var jitter = _retryJitterMs == 0
            ? 0
            : Random.Shared.Next(0, _retryJitterMs + 1);
        // Apply jitter after capping the exponential component so sustained
        // outages do not synchronize every publisher exactly at the cap.
        var delayMs = boundedDelay + jitter;
        return TimeSpan.FromMilliseconds(delayMs);
    }
}
