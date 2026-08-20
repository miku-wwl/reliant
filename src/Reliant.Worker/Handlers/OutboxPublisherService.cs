using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Reliant.Application.Abstractions;
using Reliant.Application.Observability;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Observability;
using Reliant.Infrastructure.Persistence;
using System.Diagnostics;

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
                    var started = Stopwatch.GetTimestamp();
                    var result = "failure";
                    ReliantTelemetry.ChangeWorkerInflight(
                        "outbox",
                        1);
                    using var activity = ReliantTelemetry.StartActivity(
                        "outbox publish",
                        ActivityKind.Producer,
                        message.TraceParent,
                        message.TraceState);
                    activity?.SetTag("reliant.outbox_message_id", message.Id);
                    activity?.SetTag("messaging.message.type", message.MessageType);
                    activity?.SetTag("reliant.correlation_id", message.CorrelationId);
                    activity?.SetTag("reliant.causation_id", message.CausationId);
                    activity?.AddBaggage(
                        "reliant.correlation_id",
                        message.CorrelationId);
                    using var logScope = logger.BeginScope(
                        new Dictionary<string, object?>
                        {
                            ["CorrelationId"] = message.CorrelationId,
                            ["CausationId"] = message.CausationId,
                            ["MessageId"] = message.Id,
                            ["MessageType"] = message.MessageType
                        });
                    try
                    {
                        var queueName = message.MessageType switch
                        {
                            "ContributionSucceeded" => "reliant-notification",
                            _ => _defaultProcessingQueue
                        };

                        var deployment = scope.ServiceProvider
                            .GetService<DeploymentInfo>();
                        await queuePublisher.PublishAsync(
                            queueName,
                            message.MessageType,
                            message.Payload,
                            message.Id.ToString(),
                            new QueueMessageTelemetryContext(
                                message.CorrelationId,
                                message.CausationId,
                                Activity.Current?.Id ?? message.TraceParent,
                                Activity.Current?.TraceStateString ??
                                    message.TraceState,
                                deployment?.Version),
                            stoppingToken);
                        await outboxRepo.MarkAsSentAsync(message.Id, stoppingToken);
                        _brokerFailureStreak = 0;
                        result = "success";
                        activity?.SetStatus(ActivityStatusCode.Ok);
                        logger.LogInformation("Outbox message {MessageId} sent to {Queue}", message.Id, queueName);
                    }
                    catch (OperationCanceledException)
                        when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (QueuePublishException ex)
                    {
                        activity?.SetStatus(
                            ActivityStatusCode.Error,
                            ex.ErrorCategory.ToString());
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
                        activity?.SetStatus(
                            ActivityStatusCode.Error,
                            ex.GetType().Name);
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
                        ReliantTelemetry.ChangeWorkerInflight(
                            "outbox",
                            -1);
                        ReliantTelemetry.RecordWorkerRun(
                            "outbox",
                            result,
                            Stopwatch.GetElapsedTime(started));
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
