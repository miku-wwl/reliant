using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;

namespace Reliant.Worker.Scheduling;

public sealed class OperationalHistoryRetentionOptions
{
    public bool Enabled { get; init; }
    public int BatchSize { get; init; } = 500;
    public TimeSpan TransportRetention { get; init; } =
        TimeSpan.FromDays(30);
    public TimeSpan JobRetention { get; init; } =
        TimeSpan.FromDays(30);
    public TimeSpan ProviderHistoryRetention { get; init; } =
        TimeSpan.FromDays(90);
    public TimeSpan CleanupInterval { get; init; } =
        TimeSpan.FromHours(1);
    public long CapacityWarningRows { get; init; } = 1_000_000;
    public long CapacityWarningBytes { get; init; } =
        10L * 1024 * 1024 * 1024;
    public TimeSpan AlertCooldown { get; init; } =
        TimeSpan.FromHours(1);

    public static OperationalHistoryRetentionOptions From(
        IConfiguration configuration)
    {
        var section = configuration.GetSection(
            "Worker:Maintenance:Cleanup");
        return new OperationalHistoryRetentionOptions
        {
            Enabled = section.GetValue<bool?>("Enabled") ?? false,
            BatchSize = Math.Clamp(
                section.GetValue<int?>("BatchSize") ?? 500,
                1,
                5_000),
            TransportRetention = TimeSpan.FromDays(Math.Max(
                1,
                section.GetValue<int?>("TransportRetentionDays") ?? 30)),
            JobRetention = TimeSpan.FromDays(Math.Max(
                1,
                section.GetValue<int?>("JobRetentionDays") ?? 30)),
            ProviderHistoryRetention = TimeSpan.FromDays(Math.Max(
                1,
                section.GetValue<int?>("ProviderHistoryRetentionDays") ?? 90)),
            CleanupInterval = TimeSpan.FromMinutes(Math.Max(
                1,
                section.GetValue<int?>("IntervalMinutes") ?? 60)),
            CapacityWarningRows = Math.Max(
                1,
                section.GetValue<long?>("CapacityWarningRows") ??
                1_000_000),
            CapacityWarningBytes = Math.Max(
                1,
                section.GetValue<long?>("CapacityWarningBytes") ??
                10L * 1024 * 1024 * 1024),
            AlertCooldown = TimeSpan.FromMinutes(Math.Max(
                1,
                section.GetValue<int?>("AlertCooldownMinutes") ?? 60))
        };
    }
}

public sealed record OperationalCapacitySnapshot(
    long TotalOperationalRows,
    long EligibleRows,
    long ProtectedRows,
    long ArchiveRows,
    long AuditRows,
    long DatabaseBytes,
    DateTime? OldestEligibleAt,
    double OldestEligibleAgeSeconds,
    long EstimatedBatches,
    double EstimatedDrainSeconds,
    IReadOnlyDictionary<string, long> TableRows)
{
    public static OperationalCapacitySnapshot Empty { get; } = new(
        0,
        0,
        0,
        0,
        0,
        0,
        null,
        0,
        0,
        0,
        new Dictionary<string, long>());
}

public sealed record OperationalCleanupCategoryResult(
    string Category,
    long Scanned,
    long Deleted,
    long Archived);

public sealed record OperationalCleanupResult(
    bool LockAcquired,
    long Scanned,
    long Deleted,
    long Archived,
    long Skipped,
    TimeSpan Duration,
    IReadOnlyList<OperationalCleanupCategoryResult> Categories,
    OperationalCapacitySnapshot CapacityBefore,
    OperationalCapacitySnapshot CapacityAfter);

public sealed record OperationalCleanupTelemetrySnapshot(
    long Runs,
    long Scanned,
    long Deleted,
    long Archived,
    long Skipped,
    long Failures,
    long Alerts,
    double LastDurationMilliseconds,
    OperationalCapacitySnapshot LastCapacity);

public sealed class OperationalHistoryTelemetry
{
    public const string MeterName = "Reliant.OperationalHistory";

    private readonly object _gate = new();
    private readonly Meter _meter = new(MeterName, "1.0.0");
    private readonly Counter<long> _runCounter;
    private readonly Counter<long> _scanCounter;
    private readonly Counter<long> _deleteCounter;
    private readonly Counter<long> _archiveCounter;
    private readonly Counter<long> _skipCounter;
    private readonly Counter<long> _failureCounter;
    private readonly Counter<long> _alertCounter;
    private readonly Histogram<double> _duration;
    private readonly Dictionary<string, DateTimeOffset> _lastAlerts =
        new(StringComparer.Ordinal);
    private long _runs;
    private long _scanned;
    private long _deleted;
    private long _archived;
    private long _skipped;
    private long _failures;
    private long _alerts;
    private double _lastDurationMilliseconds;
    private OperationalCapacitySnapshot _lastCapacity =
        OperationalCapacitySnapshot.Empty;

    public OperationalHistoryTelemetry()
    {
        _runCounter = _meter.CreateCounter<long>(
            "reliant.cleanup.runs");
        _scanCounter = _meter.CreateCounter<long>(
            "reliant.cleanup.rows.scanned");
        _deleteCounter = _meter.CreateCounter<long>(
            "reliant.cleanup.rows.deleted");
        _archiveCounter = _meter.CreateCounter<long>(
            "reliant.cleanup.rows.archived");
        _skipCounter = _meter.CreateCounter<long>(
            "reliant.cleanup.rows.skipped");
        _failureCounter = _meter.CreateCounter<long>(
            "reliant.cleanup.failures");
        _alertCounter = _meter.CreateCounter<long>(
            "reliant.cleanup.alerts");
        _duration = _meter.CreateHistogram<double>(
            "reliant.cleanup.duration",
            "ms");
        _meter.CreateObservableGauge(
            "reliant.operational.rows",
            () => Snapshot.LastCapacity.TotalOperationalRows);
        _meter.CreateObservableGauge(
            "reliant.operational.eligible_rows",
            () => Snapshot.LastCapacity.EligibleRows);
        _meter.CreateObservableGauge(
            "reliant.operational.oldest_eligible_age",
            () => Snapshot.LastCapacity.OldestEligibleAgeSeconds,
            "s");
        _meter.CreateObservableGauge(
            "reliant.operational.database_size",
            () => Snapshot.LastCapacity.DatabaseBytes,
            "By");
        _meter.CreateObservableGauge(
            "reliant.operational.estimated_drain_time",
            () => Snapshot.LastCapacity.EstimatedDrainSeconds,
            "s");
    }

    public OperationalCleanupTelemetrySnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new OperationalCleanupTelemetrySnapshot(
                    _runs,
                    _scanned,
                    _deleted,
                    _archived,
                    _skipped,
                    _failures,
                    _alerts,
                    _lastDurationMilliseconds,
                    _lastCapacity);
            }
        }
    }

    public void ObserveCapacity(OperationalCapacitySnapshot capacity)
    {
        lock (_gate)
        {
            _lastCapacity = capacity;
        }
    }

    public void RecordCompleted(OperationalCleanupResult result)
    {
        lock (_gate)
        {
            _runs++;
            _scanned += result.Scanned;
            _deleted += result.Deleted;
            _archived += result.Archived;
            _skipped += result.Skipped;
            _lastDurationMilliseconds =
                result.Duration.TotalMilliseconds;
            _lastCapacity = result.CapacityAfter;
        }

        _runCounter.Add(1);
        foreach (var category in result.Categories)
        {
            var tag = new KeyValuePair<string, object?>(
                "category",
                category.Category);
            _scanCounter.Add(category.Scanned, tag);
            _deleteCounter.Add(category.Deleted, tag);
            _archiveCounter.Add(category.Archived, tag);
        }
        _skipCounter.Add(result.Skipped);
        _duration.Record(result.Duration.TotalMilliseconds);
    }

    public void RecordFailure(TimeSpan duration)
    {
        lock (_gate)
        {
            _runs++;
            _failures++;
            _lastDurationMilliseconds = duration.TotalMilliseconds;
        }

        _runCounter.Add(1);
        _failureCounter.Add(1);
        _duration.Record(duration.TotalMilliseconds);
    }

    public bool TryRecordAlert(
        string alertType,
        DateTimeOffset now,
        TimeSpan cooldown)
    {
        lock (_gate)
        {
            if (_lastAlerts.TryGetValue(alertType, out var last) &&
                now - last < cooldown)
            {
                return false;
            }

            _lastAlerts[alertType] = now;
            _alerts++;
        }

        _alertCounter.Add(1,
            new KeyValuePair<string, object?>("type", alertType));
        return true;
    }
}

public enum OperationalCleanupFaultPoint
{
    AfterLockAcquired,
    BeforeCommit
}

public interface IOperationalHistoryCleanupFaultInjector
{
    Task InjectAsync(
        OperationalCleanupFaultPoint point,
        CancellationToken cancellationToken);
}

public sealed class NoopOperationalHistoryCleanupFaultInjector
    : IOperationalHistoryCleanupFaultInjector
{
    public Task InjectAsync(
        OperationalCleanupFaultPoint point,
        CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public sealed class OperationalHistoryCleanupService(
    ReliantDbContext db,
    IConfiguration configuration,
    TimeProvider timeProvider,
    OperationalHistoryTelemetry telemetry,
    IOperationalHistoryCleanupFaultInjector faultInjector,
    ILogger<OperationalHistoryCleanupService> logger)
{
    public const long AdvisoryLockKey = 7_341_885_150_001;

    private static readonly JobStatus[] TerminalJobStatuses =
    [
        JobStatus.Succeeded,
        JobStatus.Failed,
        JobStatus.DeadLettered,
        JobStatus.Cancelled
    ];

    private static readonly ContributionState[] TerminalContributionStates =
    [
        ContributionState.Succeeded,
        ContributionState.Failed,
        ContributionState.Completed
    ];

    private readonly OperationalHistoryRetentionOptions _options =
        OperationalHistoryRetentionOptions.From(configuration);

    public OperationalHistoryRetentionOptions Options => _options;

    public async Task<OperationalCapacitySnapshot> InspectCapacityAsync(
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var capacity = await ReadCapacityAsync(
            now,
            cancellationToken);
        telemetry.ObserveCapacity(capacity);
        EmitCapacityAlerts(capacity, now);
        return capacity;
    }

    public async Task<OperationalCleanupResult> RunBatchAsync(
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        IDbContextTransaction? transaction = null;
        try
        {
            transaction = await db.Database.BeginTransactionAsync(
                cancellationToken);
            var lockAcquired = await db.Database
                .SqlQuery<bool>($"""
                    SELECT pg_try_advisory_xact_lock(
                        {AdvisoryLockKey}) AS "Value"
                    """)
                .SingleAsync(cancellationToken);

            if (!lockAcquired)
            {
                await transaction.RollbackAsync(cancellationToken);
                stopwatch.Stop();
                var skippedResult = new OperationalCleanupResult(
                    false,
                    0,
                    0,
                    0,
                    1,
                    stopwatch.Elapsed,
                    Array.Empty<OperationalCleanupCategoryResult>(),
                    telemetry.Snapshot.LastCapacity,
                    telemetry.Snapshot.LastCapacity);
                telemetry.RecordCompleted(skippedResult);
                logger.LogInformation(
                    "Operational cleanup skipped because another scanner owns the advisory lock");
                return skippedResult;
            }

            await faultInjector.InjectAsync(
                OperationalCleanupFaultPoint.AfterLockAcquired,
                cancellationToken);

            var now = timeProvider.GetUtcNow();
            var capacityBefore = await ReadCapacityAsync(
                now,
                cancellationToken);
            EmitCapacityAlerts(capacityBefore, now);

            var categories = new List<OperationalCleanupCategoryResult>();
            categories.Add(await CleanupOutboxAsync(
                now.UtcDateTime - _options.TransportRetention,
                cancellationToken));
            categories.Add(await CleanupInboxAsync(
                now.UtcDateTime - _options.TransportRetention,
                cancellationToken));
            categories.Add(await CleanupJobGroupsAsync(
                now.UtcDateTime - _options.JobRetention,
                cancellationToken));
            categories.Add(await ArchiveProcessingAttemptsAsync(
                now.UtcDateTime - _options.ProviderHistoryRetention,
                now.UtcDateTime,
                cancellationToken));
            categories.Add(await ArchiveReconciliationAsync(
                now.UtcDateTime - _options.ProviderHistoryRetention,
                now.UtcDateTime,
                cancellationToken));

            await faultInjector.InjectAsync(
                OperationalCleanupFaultPoint.BeforeCommit,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            transaction = null;

            var capacityAfter = await ReadCapacityAsync(
                timeProvider.GetUtcNow(),
                cancellationToken);
            stopwatch.Stop();
            var result = new OperationalCleanupResult(
                true,
                categories.Sum(x => x.Scanned),
                categories.Sum(x => x.Deleted),
                categories.Sum(x => x.Archived),
                0,
                stopwatch.Elapsed,
                categories,
                capacityBefore,
                capacityAfter);
            telemetry.RecordCompleted(result);
            logger.LogInformation(
                "Operational cleanup completed: scanned {Scanned}, deleted {Deleted}, archived {Archived}, protected {Protected}, duration {DurationMs} ms",
                result.Scanned,
                result.Deleted,
                result.Archived,
                capacityBefore.ProtectedRows,
                result.Duration.TotalMilliseconds);
            return result;
        }
        catch (OperationCanceledException)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            throw;
        }
        catch (Exception ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            stopwatch.Stop();
            telemetry.RecordFailure(stopwatch.Elapsed);
            var now = timeProvider.GetUtcNow();
            if (telemetry.TryRecordAlert(
                "CleanupFailure",
                now,
                _options.AlertCooldown))
            {
                logger.LogError(
                    OperationalHistoryEventIds.CleanupFailure,
                    ex,
                    "Operational cleanup failure alert: history cleanup did not complete");
            }

            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private async Task<OperationalCleanupCategoryResult>
        CleanupOutboxAsync(
            DateTime cutoff,
            CancellationToken cancellationToken)
    {
        var ids = await EligibleOutbox(cutoff)
            .OrderBy(x => x.OccurredAt)
            .Select(x => x.Id)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);
        var deleted = ids.Count == 0
            ? 0
            : await db.OutboxMessages
                .IgnoreQueryFilters()
                .Where(x => ids.Contains(x.Id))
                .ExecuteDeleteAsync(cancellationToken);
        return new OperationalCleanupCategoryResult(
            "Outbox",
            ids.Count,
            deleted,
            0);
    }

    private async Task<OperationalCleanupCategoryResult>
        CleanupInboxAsync(
            DateTime cutoff,
            CancellationToken cancellationToken)
    {
        var ids = await EligibleInbox(cutoff)
            .OrderBy(x => x.ProcessedAt)
            .Select(x => x.Id)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);
        var deleted = ids.Count == 0
            ? 0
            : await db.InboxMessages
                .IgnoreQueryFilters()
                .Where(x => ids.Contains(x.Id))
                .ExecuteDeleteAsync(cancellationToken);
        return new OperationalCleanupCategoryResult(
            "Inbox",
            ids.Count,
            deleted,
            0);
    }

    private async Task<OperationalCleanupCategoryResult>
        CleanupJobGroupsAsync(
            DateTime cutoff,
            CancellationToken cancellationToken)
    {
        var jobIds = await EligibleJobRuns(cutoff)
            .OrderBy(x => x.CompletedAt)
            .Select(x => x.Id)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);
        if (jobIds.Count == 0)
        {
            return new OperationalCleanupCategoryResult(
                "JobGroup",
                0,
                0,
                0);
        }

        var childRows =
            await db.JobAttempts.IgnoreQueryFilters()
                .CountAsync(x => jobIds.Contains(x.JobRunId),
                    cancellationToken) +
            await db.Leases.IgnoreQueryFilters()
                .CountAsync(x => jobIds.Contains(x.JobRunId),
                    cancellationToken) +
            await db.Checkpoints.IgnoreQueryFilters()
                .CountAsync(x => jobIds.Contains(x.JobRunId),
                    cancellationToken);

        var deleted = 0;
        deleted += await db.Checkpoints.IgnoreQueryFilters()
            .Where(x => jobIds.Contains(x.JobRunId))
            .ExecuteDeleteAsync(cancellationToken);
        deleted += await db.JobAttempts.IgnoreQueryFilters()
            .Where(x => jobIds.Contains(x.JobRunId))
            .ExecuteDeleteAsync(cancellationToken);
        deleted += await db.Leases.IgnoreQueryFilters()
            .Where(x => jobIds.Contains(x.JobRunId))
            .ExecuteDeleteAsync(cancellationToken);
        deleted += await db.JobRuns.IgnoreQueryFilters()
            .Where(x => jobIds.Contains(x.Id))
            .ExecuteDeleteAsync(cancellationToken);

        return new OperationalCleanupCategoryResult(
            "JobGroup",
            jobIds.Count + childRows,
            deleted,
            0);
    }

    private async Task<OperationalCleanupCategoryResult>
        ArchiveProcessingAttemptsAsync(
            DateTime cutoff,
            DateTime archivedAt,
            CancellationToken cancellationToken)
    {
        var rows = await EligibleProcessingAttempts(cutoff)
            .OrderBy(x => x.CompletedAt)
            .Take(_options.BatchSize)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return new OperationalCleanupCategoryResult(
                "ProcessingAttempt",
                0,
                0,
                0);
        }

        var ids = rows.Select(x => x.Id).ToList();
        var existing = await db.OperationalHistoryArchives
            .IgnoreQueryFilters()
            .Where(x =>
                x.SourceType == "ProcessingAttempt" &&
                ids.Contains(x.SourceId))
            .Select(x => x.SourceId)
            .ToListAsync(cancellationToken);
        var existingIds = existing.ToHashSet();
        var archives = rows
            .Where(x => !existingIds.Contains(x.Id))
            .Select(x => new OperationalHistoryArchive
            {
                Id = Guid.NewGuid(),
                SourceType = "ProcessingAttempt",
                SourceId = x.Id,
                OrganizationId = x.OrganizationId,
                SourceOccurredAt = x.CompletedAt ?? x.StartedAt,
                ArchivedAt = archivedAt,
                Payload = JsonSerializer.Serialize(x)
            })
            .ToList();
        db.OperationalHistoryArchives.AddRange(archives);
        await db.SaveChangesAsync(cancellationToken);
        var deleted = await db.ProcessingAttempts
            .IgnoreQueryFilters()
            .Where(x => ids.Contains(x.Id))
            .ExecuteDeleteAsync(cancellationToken);
        return new OperationalCleanupCategoryResult(
            "ProcessingAttempt",
            rows.Count,
            deleted,
            archives.Count);
    }

    private async Task<OperationalCleanupCategoryResult>
        ArchiveReconciliationAsync(
            DateTime cutoff,
            DateTime archivedAt,
            CancellationToken cancellationToken)
    {
        var rows = await EligibleReconciliation(cutoff)
            .OrderBy(x => x.ResolvedAt)
            .Take(_options.BatchSize)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return new OperationalCleanupCategoryResult(
                "Reconciliation",
                0,
                0,
                0);
        }

        var ids = rows.Select(x => x.Id).ToList();
        var existing = await db.OperationalHistoryArchives
            .IgnoreQueryFilters()
            .Where(x =>
                x.SourceType == "Reconciliation" &&
                ids.Contains(x.SourceId))
            .Select(x => x.SourceId)
            .ToListAsync(cancellationToken);
        var existingIds = existing.ToHashSet();
        var archives = rows
            .Where(x => !existingIds.Contains(x.Id))
            .Select(x => new OperationalHistoryArchive
            {
                Id = Guid.NewGuid(),
                SourceType = "Reconciliation",
                SourceId = x.Id,
                OrganizationId = x.OrganizationId,
                SourceOccurredAt = x.ResolvedAt ?? x.CreatedAt,
                ArchivedAt = archivedAt,
                Payload = JsonSerializer.Serialize(x)
            })
            .ToList();
        db.OperationalHistoryArchives.AddRange(archives);
        await db.SaveChangesAsync(cancellationToken);
        var deleted = await db.ReconciliationRecords
            .IgnoreQueryFilters()
            .Where(x => ids.Contains(x.Id))
            .ExecuteDeleteAsync(cancellationToken);
        return new OperationalCleanupCategoryResult(
            "Reconciliation",
            rows.Count,
            deleted,
            archives.Count);
    }

    private IQueryable<OutboxMessage> EligibleOutbox(DateTime cutoff)
        => db.OutboxMessages
            .IgnoreQueryFilters()
            .Where(x =>
                (x.Status == OutboxStatus.Sent ||
                 x.Status == OutboxStatus.Failed) &&
                x.OccurredAt < cutoff &&
                !db.JobRuns.IgnoreQueryFilters().Any(job =>
                    job.Id == x.Id &&
                    (job.Status == JobStatus.Pending ||
                     job.Status == JobStatus.Running)));

    private IQueryable<InboxMessage> EligibleInbox(DateTime cutoff)
        => db.InboxMessages
            .IgnoreQueryFilters()
            .Where(x =>
                (x.Status == InboxStatus.Processed ||
                 x.Status == InboxStatus.Failed) &&
                x.ProcessedAt != null &&
                x.ProcessedAt < cutoff &&
                !db.JobRuns.IgnoreQueryFilters().Any(job =>
                    job.MessageId == x.MessageId &&
                    (job.Status == JobStatus.Pending ||
                     job.Status == JobStatus.Running)));

    private IQueryable<JobRun> EligibleJobRuns(DateTime cutoff)
        => db.JobRuns
            .IgnoreQueryFilters()
            .Where(x =>
                TerminalJobStatuses.Contains(x.Status) &&
                x.CompletedAt != null &&
                x.CompletedAt < cutoff &&
                !db.Leases.IgnoreQueryFilters().Any(lease =>
                    lease.JobRunId == x.Id && lease.IsActive));

    private IQueryable<ProcessingAttempt>
        EligibleProcessingAttempts(DateTime cutoff)
        => db.ProcessingAttempts
            .IgnoreQueryFilters()
            .Where(x =>
                (x.Status == AttemptStatus.Succeeded ||
                 x.Status == AttemptStatus.Failed) &&
                x.CompletedAt != null &&
                x.CompletedAt < cutoff &&
                db.Contributions.IgnoreQueryFilters().Any(contribution =>
                    contribution.Id == x.ContributionId &&
                    TerminalContributionStates.Contains(
                        contribution.State)));

    private IQueryable<ReconciliationRecord>
        EligibleReconciliation(DateTime cutoff)
        => db.ReconciliationRecords
            .IgnoreQueryFilters()
            .Where(x =>
                x.ResolvedAt != null &&
                x.ResolvedAt < cutoff &&
                x.Resolution != "ManualRequired" &&
                db.Contributions.IgnoreQueryFilters().Any(contribution =>
                    contribution.Id == x.ContributionId &&
                    TerminalContributionStates.Contains(
                        contribution.State)));

    private async Task<OperationalCapacitySnapshot> ReadCapacityAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var transportCutoff =
            now.UtcDateTime - _options.TransportRetention;
        var jobCutoff = now.UtcDateTime - _options.JobRetention;
        var providerCutoff =
            now.UtcDateTime - _options.ProviderHistoryRetention;

        var tableRows = new Dictionary<string, long>
        {
            ["Outbox"] = await db.OutboxMessages.IgnoreQueryFilters()
                .LongCountAsync(cancellationToken),
            ["Inbox"] = await db.InboxMessages.IgnoreQueryFilters()
                .LongCountAsync(cancellationToken),
            ["JobRun"] = await db.JobRuns.IgnoreQueryFilters()
                .LongCountAsync(cancellationToken),
            ["JobAttempt"] = await db.JobAttempts.IgnoreQueryFilters()
                .LongCountAsync(cancellationToken),
            ["Lease"] = await db.Leases.IgnoreQueryFilters()
                .LongCountAsync(cancellationToken),
            ["Checkpoint"] = await db.Checkpoints.IgnoreQueryFilters()
                .LongCountAsync(cancellationToken),
            ["ProcessingAttempt"] = await db.ProcessingAttempts
                .IgnoreQueryFilters()
                .LongCountAsync(cancellationToken),
            ["Reconciliation"] = await db.ReconciliationRecords
                .IgnoreQueryFilters()
                .LongCountAsync(cancellationToken),
            ["Archive"] = await db.OperationalHistoryArchives
                .IgnoreQueryFilters()
                .LongCountAsync(cancellationToken),
            ["AuditEvent"] = await db.AuditEvents.IgnoreQueryFilters()
                .LongCountAsync(cancellationToken),
            ["StateTransition"] = await db.StateTransitions
                .IgnoreQueryFilters()
                .LongCountAsync(cancellationToken)
        };

        var eligibleOutbox = await EligibleOutbox(transportCutoff)
            .LongCountAsync(cancellationToken);
        var eligibleInbox = await EligibleInbox(transportCutoff)
            .LongCountAsync(cancellationToken);
        var eligibleJobQuery = EligibleJobRuns(jobCutoff);
        var eligibleJobs = await eligibleJobQuery
            .LongCountAsync(cancellationToken);
        var eligibleJobAttempts = await db.JobAttempts
            .IgnoreQueryFilters()
            .LongCountAsync(
                attempt => eligibleJobQuery.Any(job =>
                    job.Id == attempt.JobRunId),
                cancellationToken);
        var eligibleLeases = await db.Leases
            .IgnoreQueryFilters()
            .LongCountAsync(
                lease => eligibleJobQuery.Any(job =>
                    job.Id == lease.JobRunId),
                cancellationToken);
        var eligibleCheckpoints = await db.Checkpoints
            .IgnoreQueryFilters()
            .LongCountAsync(
                checkpoint => eligibleJobQuery.Any(job =>
                    job.Id == checkpoint.JobRunId),
                cancellationToken);
        var eligibleAttempts = await EligibleProcessingAttempts(
                providerCutoff)
            .LongCountAsync(cancellationToken);
        var eligibleReconciliation = await EligibleReconciliation(
                providerCutoff)
            .LongCountAsync(cancellationToken);
        var eligibleRows = eligibleOutbox + eligibleInbox +
            eligibleJobs + eligibleJobAttempts + eligibleLeases +
            eligibleCheckpoints + eligibleAttempts +
            eligibleReconciliation;

        var oldestCandidates = new DateTime?[]
        {
            await EligibleOutbox(transportCutoff)
                .MinAsync(x => (DateTime?)x.OccurredAt,
                    cancellationToken),
            await EligibleInbox(transportCutoff)
                .MinAsync(x => x.ProcessedAt, cancellationToken),
            await EligibleJobRuns(jobCutoff)
                .MinAsync(x => x.CompletedAt, cancellationToken),
            await EligibleProcessingAttempts(providerCutoff)
                .MinAsync(x => x.CompletedAt, cancellationToken),
            await EligibleReconciliation(providerCutoff)
                .MinAsync(x => x.ResolvedAt, cancellationToken)
        };
        var oldestEligibleAt = oldestCandidates
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .DefaultIfEmpty()
            .Min();
        DateTime? oldest = oldestEligibleAt == default
            ? null
            : oldestEligibleAt;

        var databaseBytes = await db.Database
            .SqlQueryRaw<long>("""
                SELECT COALESCE(SUM(
                    pg_total_relation_size(
                        format('%I.%I', schemaname, tablename))),
                    0)::bigint AS "Value"
                FROM pg_tables
                WHERE schemaname = current_schema()
                  AND tablename IN (
                    'outbox_messages',
                    'inbox_messages',
                    'job_runs',
                    'job_attempts',
                    'leases',
                    'checkpoints',
                    'processing_attempts',
                    'reconciliation_records',
                    'operational_history_archives',
                    'audit_events',
                    'state_transitions')
                """)
            .SingleAsync(cancellationToken);

        var operationalRows = tableRows.Sum(x => x.Value);
        var protectedRows = Math.Max(0, operationalRows - eligibleRows);
        var estimatedBatches = new[]
            {
                eligibleOutbox,
                eligibleInbox,
                eligibleJobs,
                eligibleAttempts,
                eligibleReconciliation
            }
            .Select(count => (long)Math.Ceiling(
                count / (double)_options.BatchSize))
            .DefaultIfEmpty(0)
            .Max();

        return new OperationalCapacitySnapshot(
            operationalRows,
            eligibleRows,
            protectedRows,
            tableRows["Archive"],
            tableRows["AuditEvent"] + tableRows["StateTransition"],
            databaseBytes,
            oldest,
            oldest.HasValue
                ? Math.Max(
                    0,
                    (now.UtcDateTime - oldest.Value).TotalSeconds)
                : 0,
            estimatedBatches,
            estimatedBatches * _options.CleanupInterval.TotalSeconds,
            tableRows);
    }

    private void EmitCapacityAlerts(
        OperationalCapacitySnapshot capacity,
        DateTimeOffset now)
    {
        telemetry.ObserveCapacity(capacity);
        if (capacity.TotalOperationalRows >
                _options.CapacityWarningRows &&
            telemetry.TryRecordAlert(
                "CapacityRows",
                now,
                _options.AlertCooldown))
        {
            logger.LogWarning(
                OperationalHistoryEventIds.CapacityWarning,
                "Operational history capacity alert: {Rows} rows exceeds threshold {Threshold}; eligible {Eligible}, estimated drain {DrainSeconds} seconds",
                capacity.TotalOperationalRows,
                _options.CapacityWarningRows,
                capacity.EligibleRows,
                capacity.EstimatedDrainSeconds);
        }

        if (capacity.DatabaseBytes >
                _options.CapacityWarningBytes &&
            telemetry.TryRecordAlert(
                "CapacityBytes",
                now,
                _options.AlertCooldown))
        {
            logger.LogWarning(
                OperationalHistoryEventIds.CapacityWarning,
                "Operational history size alert: {Bytes} bytes exceeds threshold {Threshold}",
                capacity.DatabaseBytes,
                _options.CapacityWarningBytes);
        }
    }
}

public static class OperationalHistoryEventIds
{
    public static readonly EventId CapacityWarning =
        new(15001, "OperationalHistoryCapacityWarning");
    public static readonly EventId CleanupFailure =
        new(15002, "OperationalHistoryCleanupFailure");
}
