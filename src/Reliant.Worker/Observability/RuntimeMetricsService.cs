using Microsoft.EntityFrameworkCore;
using Reliant.Application.Abstractions;
using Reliant.Application.Observability;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;

namespace Reliant.Worker.Observability;

/// <summary>
/// Periodically projects durable backlog state and broker depth into bounded
/// observable gauges. Failure to collect a snapshot is logged and never affects
/// message processing.
/// </summary>
public sealed class RuntimeMetricsService(
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    ILogger<RuntimeMetricsService> logger) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(
        Math.Max(
            5,
            configuration.GetValue<int?>(
                "Telemetry:RuntimeMetricsIntervalSeconds") ?? 15));
    private readonly string _processingQueue =
        configuration["Queue:QueueName"] ?? "reliant-processing";
    private const string NotificationQueue = "reliant-notification";

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CollectAsync(stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Runtime metric snapshot collection failed");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task CollectAsync(CancellationToken cancellationToken)
    {
        using var activity = ReliantTelemetry.StartActivity(
            "runtime metrics collect");
        using var scope = serviceProvider.CreateScope();
        var database = scope.ServiceProvider
            .GetRequiredService<ReliantDbContext>();
        var queue = scope.ServiceProvider
            .GetRequiredService<IQueueAdapter>();
        var now = DateTime.UtcNow;

        var outboxPending = await database.OutboxMessages
            .IgnoreQueryFilters()
            .AsNoTracking()
            .LongCountAsync(
                x => x.Status == OutboxStatus.Pending,
                cancellationToken);
        var retryPending = await database.Contributions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .LongCountAsync(
                x => x.State == ContributionState.RetryPending,
                cancellationToken);
        var retryOldest = await database.Contributions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.State == ContributionState.RetryPending)
            .Select(x => (DateTime?)x.UpdatedAt)
            .MinAsync(cancellationToken);
        var deadLetterPending = await database.DeadLetterRecords
            .IgnoreQueryFilters()
            .AsNoTracking()
            .LongCountAsync(
                x => x.Status == DeadLetterStatus.Pending,
                cancellationToken);
        var reconciliationPending = await database
            .ReconciliationRecords
            .IgnoreQueryFilters()
            .AsNoTracking()
            .LongCountAsync(
                x => x.ResolvedAt == null,
                cancellationToken);
        var reconciliationOldest = await database
            .ReconciliationRecords
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.ResolvedAt == null)
            .Select(x => (DateTime?)x.CreatedAt)
            .MinAsync(cancellationToken);

        var processingDepth = await ReadQueueDepthAsync(
            queue,
            _processingQueue,
            cancellationToken);
        var notificationDepth = await ReadQueueDepthAsync(
            queue,
            NotificationQueue,
            cancellationToken);

        ReliantTelemetry.SetRuntimeSnapshot(
            new RuntimeMetricSnapshot(
                processingDepth,
                notificationDepth,
                null,
                null,
                outboxPending,
                retryPending,
                AgeSeconds(now, retryOldest),
                deadLetterPending,
                reconciliationPending,
                AgeSeconds(now, reconciliationOldest)));
        activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
    }

    private static async Task<long> ReadQueueDepthAsync(
        IQueueAdapter queue,
        string queueName,
        CancellationToken cancellationToken)
    {
        var queueUrl = await queue.GetOrCreateQueueAsync(
            queueName,
            cancellationToken);
        var snapshot = await queue.GetMetricsAsync(
            queueUrl,
            cancellationToken);
        return snapshot is null
            ? 0
            : snapshot.VisibleMessages +
                snapshot.InFlightMessages +
                snapshot.DelayedMessages;
    }

    private static double AgeSeconds(
        DateTime now,
        DateTime? timestamp)
        => timestamp.HasValue
            ? Math.Max(0, (now - timestamp.Value).TotalSeconds)
            : 0;
}
