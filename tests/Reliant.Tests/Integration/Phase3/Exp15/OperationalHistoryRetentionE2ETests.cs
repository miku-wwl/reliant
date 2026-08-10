using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;
using Reliant.Tests.Integration.Fixtures;
using Reliant.Worker.Scheduling;
using System.Diagnostics;
using System.Globalization;
using Xunit.Abstractions;

namespace Reliant.Tests.Integration.Phase3.Exp15;

[Trait("Category", "Integration")]
[Trait("Dependency", "PostgreSQL")]
[Trait("Dependency", "LocalStack")]
[Trait("Dependency", "WorkerHost")]
public sealed class OperationalHistoryRetentionE2ETests(
    ITestOutputHelper output)
{
    private const int ExpiredGroupCount = 5;
    private const int BatchSize = 2;

    private sealed record SeededData(
        IReadOnlyList<Guid> ExpiredContributionIds,
        IReadOnlyList<Guid> ExpiredJobIds,
        Guid FreshContributionId,
        Guid FreshJobId,
        Guid ActiveContributionId,
        Guid ActiveJobId,
        Guid PendingOutboxId,
        Guid ProcessingInboxId,
        int BusinessRows,
        int AuditRows);

    private sealed record DataCounts(
        int Outbox,
        int Inbox,
        int JobRuns,
        int JobAttempts,
        int Leases,
        int Checkpoints,
        int ProcessingAttempts,
        int Reconciliation,
        int Archives,
        int Contributions,
        int ProviderReferences,
        int AuditEvents,
        int StateTransitions);

    [Fact]
    public async Task Cleanup_ShouldBeBounded_Reentrant_Fenced_Observable_AndSafe()
    {
        var startedAt = DateTime.UtcNow;
        await using var fixture = new WorkerHostFixture();
        await fixture.InitializeAsync();
        var seeded = await SeedHistoryAsync(
            fixture.PgConnectionString);
        var injector = new ControlledCleanupFaultInjector();

        await fixture.StartWorkersAsync(
            includeProcessing: false,
            includeReconciliation: false,
            cleanupFaultInjectorOverride: injector,
            configurationOverrides:
                new Dictionary<string, string?>
                {
                    ["Worker:Maintenance:Cleanup:Enabled"] = "false",
                    ["Worker:Maintenance:Cleanup:BatchSize"] =
                        BatchSize.ToString(
                            CultureInfo.InvariantCulture),
                    ["Worker:Maintenance:Cleanup:TransportRetentionDays"] =
                        "30",
                    ["Worker:Maintenance:Cleanup:JobRetentionDays"] =
                        "30",
                    ["Worker:Maintenance:Cleanup:ProviderHistoryRetentionDays"] =
                        "90",
                    ["Worker:Maintenance:Cleanup:IntervalMinutes"] = "1",
                    ["Worker:Maintenance:Cleanup:CapacityWarningRows"] = "1",
                    ["Worker:Maintenance:Cleanup:CapacityWarningBytes"] =
                        long.MaxValue.ToString(
                            CultureInfo.InvariantCulture),
                    ["Worker:Maintenance:Cleanup:AlertCooldownMinutes"] = "60"
                },
            includeOutboxPublisher: false);

        var telemetry = fixture.Host.Services
            .GetRequiredService<OperationalHistoryTelemetry>();
        var initialCounts = await ReadCountsAsync(
            fixture.PgConnectionString);
        var initialCapacity = await InspectAsync(fixture);

        Assert.Equal(40, initialCapacity.EligibleRows);
        Assert.True(initialCapacity.ProtectedRows > 0);
        Assert.True(initialCapacity.DatabaseBytes > 0);
        Assert.NotNull(initialCapacity.OldestEligibleAt);
        Assert.True(initialCapacity.OldestEligibleAgeSeconds >
            TimeSpan.FromDays(100).TotalSeconds);
        Assert.Equal(3, initialCapacity.EstimatedBatches);
        Assert.Equal(180, initialCapacity.EstimatedDrainSeconds);
        Assert.Contains(
            fixture.LogLines,
            line => line.Contains(
                "Operational history capacity alert",
                StringComparison.Ordinal));

        output.WriteLine(
            "CAPACITY | OperationalRows={0} | Eligible={1} | " +
            "Protected={2} | Bytes={3} | OldestAgeDays={4:F1} | " +
            "EstimatedBatches={5} | EstimatedDrainSeconds={6:F0} | " +
            "CapacityAlert=1",
            initialCapacity.TotalOperationalRows,
            initialCapacity.EligibleRows,
            initialCapacity.ProtectedRows,
            initialCapacity.DatabaseBytes,
            initialCapacity.OldestEligibleAgeSeconds /
                TimeSpan.FromDays(1).TotalSeconds,
            initialCapacity.EstimatedBatches,
            initialCapacity.EstimatedDrainSeconds);

        // A process termination before commit must roll back the entire bounded
        // batch. A later run sees exactly the same candidates.
        injector.CancelBeforeCommit();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => RunCleanupAsync(fixture));
        var afterCancellation = await ReadCountsAsync(
            fixture.PgConnectionString);
        Assert.Equal(initialCounts, afterCancellation);
        Assert.Equal(0, afterCancellation.Archives);

        output.WriteLine(
            "INTERRUPT | Point=BeforeCommit | Transaction=RolledBack | " +
            "RowsChanged=0 | Restartable=true");

        // Hold scanner A immediately after the PostgreSQL advisory lock. Scanner
        // B must return promptly instead of doing duplicate work or waiting on a
        // long blocking lock.
        injector.BlockAfterLock();
        var scannerA = RunCleanupAsync(fixture);
        await injector.WaitUntilBlockedAsync(
            TimeSpan.FromSeconds(10));
        var scannerBStopwatch = Stopwatch.StartNew();
        var scannerB = await RunCleanupAsync(fixture);
        scannerBStopwatch.Stop();
        injector.ReleaseBlockedScanner();
        var scannerAResult = await scannerA;

        Assert.True(scannerAResult.LockAcquired);
        Assert.False(scannerB.LockAcquired);
        Assert.Equal(1, scannerB.Skipped);
        Assert.True(scannerBStopwatch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.All(
            scannerAResult.Categories.Where(x =>
                x.Category != "JobGroup"),
            category => Assert.True(
                category.Scanned <= BatchSize,
                $"{category.Category} exceeded its batch cap."));
        Assert.Equal(4, scannerAResult.Archived);

        output.WriteLine(
            "CONCURRENT | ScannerA=LockOwner | ScannerB=Skipped | " +
            "ScannerBMs={0} | BatchSize={1} | FirstDeleted={2} | " +
            "FirstArchived={3}",
            scannerBStopwatch.ElapsedMilliseconds,
            BatchSize,
            scannerAResult.Deleted,
            scannerAResult.Archived);

        // Continue one bounded batch at a time until the eligible set is empty.
        var successfulRuns = 1;
        while (true)
        {
            var capacity = await InspectAsync(fixture);
            if (capacity.EligibleRows == 0)
            {
                break;
            }

            var result = await RunCleanupAsync(fixture);
            Assert.True(result.LockAcquired);
            Assert.True(result.Deleted > 0);
            successfulRuns++;
        }

        var noOp = await RunCleanupAsync(fixture);
        Assert.True(noOp.LockAcquired);
        Assert.Equal(0, noOp.Deleted);
        Assert.Equal(0, noOp.Archived);

        var final = await ReadCountsAsync(
            fixture.PgConnectionString);
        var finalCapacity = await InspectAsync(fixture);
        await AssertExpiredOperationalRowsRemovedAsync(
            fixture.PgConnectionString,
            seeded);
        await AssertProtectedAndBusinessRowsRemainAsync(
            fixture.PgConnectionString,
            seeded);

        Assert.Equal(10, final.Archives);
        Assert.Equal(seeded.BusinessRows, final.Contributions);
        Assert.Equal(ExpiredGroupCount, final.ProviderReferences);
        Assert.Equal(seeded.AuditRows, final.AuditEvents +
            final.StateTransitions);
        Assert.Equal(0, finalCapacity.EligibleRows);
        Assert.Equal(0, finalCapacity.EstimatedBatches);

        // A non-cancellation execution failure must increment the failure metric
        // and emit the dedicated structured alert without committing changes.
        var beforeFailure = await ReadCountsAsync(
            fixture.PgConnectionString);
        injector.FailAfterLock();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunCleanupAsync(fixture));
        var afterFailure = await ReadCountsAsync(
            fixture.PgConnectionString);
        Assert.Equal(beforeFailure, afterFailure);

        var metrics = telemetry.Snapshot;
        Assert.Equal(1, metrics.Failures);
        Assert.True(metrics.Runs >= successfulRuns + 2);
        Assert.True(metrics.Scanned > 0);
        Assert.True(metrics.Deleted > 0);
        Assert.Equal(10, metrics.Archived);
        Assert.True(metrics.Skipped > 0);
        Assert.True(metrics.LastDurationMilliseconds >= 0);
        Assert.Equal(2, metrics.Alerts);
        Assert.Contains(
            fixture.LogLines,
            line => line.Contains(
                "Operational cleanup failure alert",
                StringComparison.Ordinal));

        // Finally prove the production ScheduledMaintenance wiring invokes the
        // same cleanup service when enabled, rather than relying only on direct
        // service calls in this lab.
        await fixture.StopWorkersAsync();
        var hostedCandidateId = await SeedHostedCleanupCandidateAsync(
            fixture.PgConnectionString);
        await fixture.StartWorkersAsync(
            includeProcessing: false,
            includeReconciliation: false,
            cleanupFaultInjectorOverride: injector,
            configurationOverrides:
                new Dictionary<string, string?>
                {
                    ["Worker:Maintenance:Cleanup:Enabled"] = "true",
                    ["Worker:Maintenance:Cleanup:BatchSize"] = "2",
                    ["Worker:Maintenance:Cleanup:TransportRetentionDays"] =
                        "30",
                    ["Worker:Maintenance:Cleanup:JobRetentionDays"] =
                        "30",
                    ["Worker:Maintenance:Cleanup:ProviderHistoryRetentionDays"] =
                        "90",
                    ["Worker:Maintenance:Cleanup:IntervalMinutes"] = "1",
                    ["Worker:Maintenance:Cleanup:CapacityWarningRows"] =
                        long.MaxValue.ToString(
                            CultureInfo.InvariantCulture),
                    ["Worker:Maintenance:Cleanup:CapacityWarningBytes"] =
                        long.MaxValue.ToString(
                            CultureInfo.InvariantCulture)
                },
            includeOutboxPublisher: false);
        Assert.True(
            await WaitUntilAsync(
                async () =>
                {
                    await using var db = CreateDbContext(
                        fixture.PgConnectionString);
                    return !await db.OutboxMessages
                        .IgnoreQueryFilters()
                        .AnyAsync(x => x.Id == hostedCandidateId);
                },
                TimeSpan.FromSeconds(15)),
            "ScheduledMaintenance did not run enabled history cleanup.");
        Assert.Contains(
            fixture.LogLines,
            line => line.Contains(
                "Operational cleanup completed",
                StringComparison.Ordinal));

        output.WriteLine(
            "FINAL | SuccessfulRuns={0} | Eligible=0 | " +
            "Archives={1} | BusinessRows={2} | AuditRows={3} | " +
            "ActiveJob=protected | PendingOutbox=protected | " +
            "UnknownAttempt=protected | ManualRequired=protected",
            successfulRuns,
            final.Archives,
            final.Contributions,
            final.AuditEvents + final.StateTransitions);
        output.WriteLine(
            "METRICS | Runs={0} | Scanned={1} | Deleted={2} | " +
            "Archived={3} | Skipped={4} | Failures={5} | " +
            "Alerts={6} | CapacityAlert=1 | CleanupFailureAlert=1",
            metrics.Runs,
            metrics.Scanned,
            metrics.Deleted,
            metrics.Archived,
            metrics.Skipped,
            metrics.Failures,
            metrics.Alerts);
        output.WriteLine(
            "HOSTED | ScheduledMaintenance=Enabled | " +
            "ExpiredOutboxDeleted=true");
        output.WriteLine(
            "RESULT | PASS | StartedAt={0:O} | CompletedAt={1:O}",
            startedAt,
            DateTime.UtcNow);
    }

    private static async Task<Guid> SeedHostedCleanupCandidateAsync(
        string connectionString)
    {
        await using var db = CreateDbContext(connectionString);
        var organizationId = await db.Organizations
            .Select(x => x.Id)
            .FirstAsync();
        var id = Guid.NewGuid();
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = id,
            OrganizationId = organizationId,
            MessageType = "HostedCleanupEvidence",
            Payload = "{}",
            CorrelationId = "exp15-hosted-cleanup",
            OccurredAt = DateTime.UtcNow.AddDays(-120),
            SentAt = DateTime.UtcNow.AddDays(-120),
            SendCount = 1,
            Status = OutboxStatus.Sent,
            Version = 0
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<SeededData> SeedHistoryAsync(
        string connectionString)
    {
        var old = DateTime.UtcNow.AddDays(-120);
        var now = DateTime.UtcNow;
        var organizationId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var expiredContributionIds = new List<Guid>();
        var expiredJobIds = new List<Guid>();

        await using var db = CreateDbContext(connectionString);
        db.Organizations.Add(new Organization
        {
            Id = organizationId,
            Name = "Phase 3 Retention Lab",
            Status = OrganizationStatus.Active,
            Version = 0
        });
        db.Campaigns.Add(new Campaign
        {
            Id = campaignId,
            OrganizationId = organizationId,
            Name = "Experiment 15",
            Status = CampaignStatus.Active,
            Version = 0
        });

        for (var index = 1; index <= ExpiredGroupCount; index++)
        {
            var contributionId = Guid.NewGuid();
            var jobId = Guid.NewGuid();
            var leaseId = Guid.NewGuid();
            expiredContributionIds.Add(contributionId);
            expiredJobIds.Add(jobId);
            AddContribution(
                db,
                contributionId,
                organizationId,
                campaignId,
                $"EXP15-OLD-{index:000}",
                ContributionState.Succeeded);
            AddOperationalGroup(
                db,
                contributionId,
                organizationId,
                jobId,
                leaseId,
                old.AddMinutes(index),
                terminal: true);
            db.ProviderReferences.Add(new ProviderReference
            {
                Id = Guid.NewGuid(),
                ContributionId = contributionId,
                OrganizationId = organizationId,
                ProviderName = "sandbox",
                Reference = $"exp15_ref_{index:000}",
                CreatedAt = old
            });
            AddAuditEvidence(
                db,
                organizationId,
                contributionId,
                old,
                index);
        }

        var freshContributionId = Guid.NewGuid();
        var freshJobId = Guid.NewGuid();
        AddContribution(
            db,
            freshContributionId,
            organizationId,
            campaignId,
            "EXP15-FRESH",
            ContributionState.Succeeded);
        AddOperationalGroup(
            db,
            freshContributionId,
            organizationId,
            freshJobId,
            Guid.NewGuid(),
            now,
            terminal: true);
        AddAuditEvidence(
            db,
            organizationId,
            freshContributionId,
            now,
            100);

        var activeContributionId = Guid.NewGuid();
        var activeJobId = Guid.NewGuid();
        var activeLeaseId = Guid.NewGuid();
        AddContribution(
            db,
            activeContributionId,
            organizationId,
            campaignId,
            "EXP15-ACTIVE",
            ContributionState.ReconciliationPending);
        AddActiveOperationalGroup(
            db,
            activeContributionId,
            organizationId,
            activeJobId,
            activeLeaseId,
            old,
            now);
        AddAuditEvidence(
            db,
            organizationId,
            activeContributionId,
            old,
            200);

        var pendingOutboxId = Guid.NewGuid();
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = pendingOutboxId,
            OrganizationId = organizationId,
            MessageType = "ProtectedPending",
            Payload = "{}",
            CorrelationId = "exp15-pending",
            OccurredAt = old,
            Status = OutboxStatus.Pending,
            Version = 0
        });
        var processingInboxId = Guid.NewGuid();
        db.InboxMessages.Add(new InboxMessage
        {
            Id = processingInboxId,
            MessageId = $"exp15-processing-{processingInboxId:N}",
            OrganizationId = organizationId,
            MessageType = "ProtectedProcessing",
            HandlerName = "Exp15",
            HandlerVersion = "1.0",
            ProcessedAt = old,
            Status = InboxStatus.Processing
        });

        await db.SaveChangesAsync();
        return new SeededData(
            expiredContributionIds,
            expiredJobIds,
            freshContributionId,
            freshJobId,
            activeContributionId,
            activeJobId,
            pendingOutboxId,
            processingInboxId,
            BusinessRows: ExpiredGroupCount + 2,
            AuditRows: (ExpiredGroupCount + 2) * 2);
    }

    private static void AddContribution(
        ReliantDbContext db,
        Guid contributionId,
        Guid organizationId,
        Guid campaignId,
        string externalReference,
        ContributionState state)
    {
        db.Contributions.Add(new Contribution
        {
            Id = contributionId,
            OrganizationId = organizationId,
            CampaignId = campaignId,
            ExternalReference = externalReference,
            Amount = 100m,
            Currency = "NZD",
            State = state,
            Version = 0
        });
    }

    private static void AddOperationalGroup(
        ReliantDbContext db,
        Guid contributionId,
        Guid organizationId,
        Guid jobId,
        Guid leaseId,
        DateTime occurredAt,
        bool terminal)
    {
        var payload = "{}";
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = jobId,
            OrganizationId = organizationId,
            MessageType = "ContributionCreated",
            Payload = payload,
            CorrelationId = $"exp15-{jobId:N}",
            OccurredAt = occurredAt,
            SentAt = occurredAt,
            SendCount = 1,
            Status = OutboxStatus.Sent,
            Version = 0
        });
        db.JobRuns.Add(new JobRun
        {
            Id = jobId,
            OrganizationId = organizationId,
            JobDefinitionId = KnownJobDefinitions.ContributionProcessingId,
            QueueUrl = KnownJobDefinitions.ContributionProcessingQueue,
            MessageId = jobId.ToString(),
            Payload = payload,
            Status = terminal ? JobStatus.Succeeded : JobStatus.Running,
            AttemptCount = 1,
            StartedAt = occurredAt,
            CompletedAt = terminal ? occurredAt : null,
            CreatedAt = occurredAt,
            FencingToken = 1,
            Version = 1
        });
        db.JobAttempts.Add(new JobAttempt
        {
            Id = Guid.NewGuid(),
            JobRunId = jobId,
            AttemptNumber = 1,
            LeaseId = leaseId,
            FencingToken = 1,
            WorkerId = "exp15-worker",
            StartedAt = occurredAt,
            CompletedAt = terminal ? occurredAt : null,
            Status = terminal
                ? JobAttemptStatus.Succeeded
                : JobAttemptStatus.Running
        });
        db.Leases.Add(new Lease
        {
            Id = leaseId,
            JobRunId = jobId,
            FencingToken = 1,
            WorkerId = "exp15-worker",
            AcquiredAt = occurredAt,
            ExpiresAt = occurredAt.AddMinutes(1),
            LastHeartbeatAt = occurredAt,
            IsActive = !terminal
        });
        db.Checkpoints.Add(new Checkpoint
        {
            Id = Guid.NewGuid(),
            JobRunId = jobId,
            Key = "exp15",
            Value = "completed",
            SavedAt = occurredAt
        });
        db.InboxMessages.Add(new InboxMessage
        {
            Id = Guid.NewGuid(),
            MessageId = jobId.ToString(),
            OrganizationId = organizationId,
            MessageType = "ContributionCreated",
            HandlerName = "ProcessingHandler",
            HandlerVersion = "1.0",
            ProcessedAt = occurredAt,
            Status = InboxStatus.Processed
        });
        db.ProcessingAttempts.Add(new ProcessingAttempt
        {
            Id = Guid.NewGuid(),
            ContributionId = contributionId,
            OrganizationId = organizationId,
            AttemptNumber = 1,
            ProviderName = "sandbox",
            ProviderIdempotencyKey = $"exp15-{contributionId:N}",
            ProviderReference = $"exp15-{contributionId:N}",
            Status = AttemptStatus.Succeeded,
            RequestPayload = "{}",
            ResponsePayload = "{}",
            StartedAt = occurredAt,
            CompletedAt = occurredAt
        });
        db.ReconciliationRecords.Add(new ReconciliationRecord
        {
            Id = Guid.NewGuid(),
            ContributionId = contributionId,
            OrganizationId = organizationId,
            LocalState = ContributionState.Succeeded,
            ProviderState = "Succeeded",
            Difference = ReconciliationDifference.None,
            Resolution = "AutoFixed",
            ResolvedAt = occurredAt,
            ResolvedBy = "exp15",
            CreatedAt = occurredAt
        });
    }

    private static void AddActiveOperationalGroup(
        ReliantDbContext db,
        Guid contributionId,
        Guid organizationId,
        Guid jobId,
        Guid leaseId,
        DateTime old,
        DateTime now)
    {
        AddOperationalGroup(
            db,
            contributionId,
            organizationId,
            jobId,
            leaseId,
            old,
            terminal: false);
        var job = db.JobRuns.Local.Single(x => x.Id == jobId);
        job.CompletedAt = null;
        var lease = db.Leases.Local.Single(x => x.Id == leaseId);
        lease.ExpiresAt = now.AddHours(1);
        lease.IsActive = true;
        var attempt = db.ProcessingAttempts.Local.Single(x =>
            x.ContributionId == contributionId);
        attempt.Status = AttemptStatus.Pending;
        attempt.CompletedAt = null;
        attempt.ProviderReference = null;
        db.ProcessingAttempts.Add(new ProcessingAttempt
        {
            Id = Guid.NewGuid(),
            ContributionId = contributionId,
            OrganizationId = organizationId,
            AttemptNumber = 2,
            ProviderName = "sandbox",
            ProviderIdempotencyKey = $"exp15-{contributionId:N}",
            Status = AttemptStatus.Unknown,
            ErrorCategory = ErrorCategory.UnknownOutcome,
            ErrorMessage = "Protected unresolved outcome",
            RequestPayload = "{}",
            StartedAt = old,
            CompletedAt = old
        });
        var reconciliation = db.ReconciliationRecords.Local.Single(x =>
            x.ContributionId == contributionId);
        reconciliation.LocalState =
            ContributionState.ReconciliationPending;
        reconciliation.ProviderState = "Conflicting";
        reconciliation.Difference =
            ReconciliationDifference.StateMismatch;
        reconciliation.Resolution = "ManualRequired";
        reconciliation.ResolvedAt = null;
        reconciliation.ResolvedBy = null;
    }

    private static void AddAuditEvidence(
        ReliantDbContext db,
        Guid organizationId,
        Guid contributionId,
        DateTime changedAt,
        int index)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            EntityType = "Contribution",
            EntityId = contributionId,
            Action = "Exp15Evidence",
            ChangedBy = "exp15",
            ChangedAt = changedAt,
            CorrelationId = $"exp15-audit-{index:000}"
        });
        db.StateTransitions.Add(new StateTransition
        {
            Id = Guid.NewGuid(),
            ContributionId = contributionId,
            FromState = ContributionState.Processing,
            ToState = ContributionState.Succeeded,
            Reason = "Exp15 retained audit evidence",
            ChangedBy = "exp15",
            ChangedAt = changedAt
        });
    }

    private static async Task<OperationalCleanupResult> RunCleanupAsync(
        WorkerHostFixture fixture)
    {
        using var scope = fixture.Host.Services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<OperationalHistoryCleanupService>()
            .RunBatchAsync();
    }

    private static async Task<OperationalCapacitySnapshot> InspectAsync(
        WorkerHostFixture fixture)
    {
        using var scope = fixture.Host.Services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<OperationalHistoryCleanupService>()
            .InspectCapacityAsync();
    }

    private static async Task<DataCounts> ReadCountsAsync(
        string connectionString)
    {
        await using var db = CreateDbContext(connectionString);
        return new DataCounts(
            await db.OutboxMessages.IgnoreQueryFilters().CountAsync(),
            await db.InboxMessages.IgnoreQueryFilters().CountAsync(),
            await db.JobRuns.IgnoreQueryFilters().CountAsync(),
            await db.JobAttempts.IgnoreQueryFilters().CountAsync(),
            await db.Leases.IgnoreQueryFilters().CountAsync(),
            await db.Checkpoints.IgnoreQueryFilters().CountAsync(),
            await db.ProcessingAttempts.IgnoreQueryFilters().CountAsync(),
            await db.ReconciliationRecords.IgnoreQueryFilters().CountAsync(),
            await db.OperationalHistoryArchives.IgnoreQueryFilters()
                .CountAsync(),
            await db.Contributions.IgnoreQueryFilters().CountAsync(),
            await db.ProviderReferences.IgnoreQueryFilters().CountAsync(),
            await db.AuditEvents.IgnoreQueryFilters().CountAsync(),
            await db.StateTransitions.IgnoreQueryFilters().CountAsync());
    }

    private static async Task AssertExpiredOperationalRowsRemovedAsync(
        string connectionString,
        SeededData seeded)
    {
        await using var db = CreateDbContext(connectionString);
        Assert.Equal(0, await db.OutboxMessages.IgnoreQueryFilters()
            .CountAsync(x => seeded.ExpiredJobIds.Contains(x.Id)));
        Assert.Equal(0, await db.InboxMessages.IgnoreQueryFilters()
            .CountAsync(x => seeded.ExpiredJobIds
                .Select(id => id.ToString())
                .Contains(x.MessageId)));
        Assert.Equal(0, await db.JobRuns.IgnoreQueryFilters()
            .CountAsync(x => seeded.ExpiredJobIds.Contains(x.Id)));
        Assert.Equal(0, await db.JobAttempts.IgnoreQueryFilters()
            .CountAsync(x => seeded.ExpiredJobIds.Contains(x.JobRunId)));
        Assert.Equal(0, await db.Leases.IgnoreQueryFilters()
            .CountAsync(x => seeded.ExpiredJobIds.Contains(x.JobRunId)));
        Assert.Equal(0, await db.Checkpoints.IgnoreQueryFilters()
            .CountAsync(x => seeded.ExpiredJobIds.Contains(x.JobRunId)));
        Assert.Equal(0, await db.ProcessingAttempts.IgnoreQueryFilters()
            .CountAsync(x => seeded.ExpiredContributionIds
                .Contains(x.ContributionId)));
        Assert.Equal(0, await db.ReconciliationRecords.IgnoreQueryFilters()
            .CountAsync(x => seeded.ExpiredContributionIds
                .Contains(x.ContributionId)));
    }

    private static async Task AssertProtectedAndBusinessRowsRemainAsync(
        string connectionString,
        SeededData seeded)
    {
        await using var db = CreateDbContext(connectionString);
        Assert.True(await db.OutboxMessages.IgnoreQueryFilters()
            .AnyAsync(x => x.Id == seeded.FreshJobId));
        Assert.True(await db.JobRuns.IgnoreQueryFilters()
            .AnyAsync(x => x.Id == seeded.FreshJobId));
        Assert.True(await db.OutboxMessages.IgnoreQueryFilters()
            .AnyAsync(x => x.Id == seeded.ActiveJobId));
        Assert.True(await db.JobRuns.IgnoreQueryFilters()
            .AnyAsync(x =>
                x.Id == seeded.ActiveJobId &&
                x.Status == JobStatus.Running));
        Assert.True(await db.Leases.IgnoreQueryFilters()
            .AnyAsync(x =>
                x.JobRunId == seeded.ActiveJobId && x.IsActive));
        Assert.True(await db.OutboxMessages.IgnoreQueryFilters()
            .AnyAsync(x =>
                x.Id == seeded.PendingOutboxId &&
                x.Status == OutboxStatus.Pending));
        Assert.True(await db.InboxMessages.IgnoreQueryFilters()
            .AnyAsync(x =>
                x.Id == seeded.ProcessingInboxId &&
                x.Status == InboxStatus.Processing));
        Assert.Equal(2, await db.ProcessingAttempts
            .IgnoreQueryFilters()
            .CountAsync(x =>
                x.ContributionId == seeded.ActiveContributionId &&
                (x.Status == AttemptStatus.Pending ||
                 x.Status == AttemptStatus.Unknown)));
        Assert.True(await db.ReconciliationRecords
            .IgnoreQueryFilters()
            .AnyAsync(x =>
                x.ContributionId == seeded.ActiveContributionId &&
                x.Resolution == "ManualRequired" &&
                x.ResolvedAt == null));
        Assert.Equal(seeded.BusinessRows, await db.Contributions
            .IgnoreQueryFilters()
            .CountAsync());
    }

    private static ReliantDbContext CreateDbContext(
        string connectionString)
        => new(
            new DbContextOptionsBuilder<ReliantDbContext>()
                .UseNpgsql(connectionString)
                .Options);

    private static async Task<bool> WaitUntilAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return await condition();
    }

    private sealed class ControlledCleanupFaultInjector
        : IOperationalHistoryCleanupFaultInjector
    {
        private readonly object _gate = new();
        private Mode _mode;
        private TaskCompletionSource _blocked = NewSignal();
        private TaskCompletionSource _release = NewSignal();

        public void CancelBeforeCommit()
        {
            lock (_gate)
            {
                _mode = Mode.CancelBeforeCommit;
            }
        }

        public void BlockAfterLock()
        {
            lock (_gate)
            {
                _blocked = NewSignal();
                _release = NewSignal();
                _mode = Mode.BlockAfterLock;
            }
        }

        public void ReleaseBlockedScanner()
        {
            lock (_gate)
            {
                _mode = Mode.None;
                _release.TrySetResult();
            }
        }

        public void FailAfterLock()
        {
            lock (_gate)
            {
                _mode = Mode.FailAfterLock;
            }
        }

        public async Task WaitUntilBlockedAsync(TimeSpan timeout)
        {
            Task task;
            lock (_gate)
            {
                task = _blocked.Task;
            }

            await task.WaitAsync(timeout);
        }

        public async Task InjectAsync(
            OperationalCleanupFaultPoint point,
            CancellationToken cancellationToken)
        {
            Mode mode;
            Task release;
            lock (_gate)
            {
                mode = _mode;
                release = _release.Task;
                if (mode == Mode.BlockAfterLock &&
                    point == OperationalCleanupFaultPoint.AfterLockAcquired)
                {
                    _blocked.TrySetResult();
                }
                else if (mode == Mode.CancelBeforeCommit &&
                    point == OperationalCleanupFaultPoint.BeforeCommit)
                {
                    _mode = Mode.None;
                }
                else if (mode == Mode.FailAfterLock &&
                    point == OperationalCleanupFaultPoint.AfterLockAcquired)
                {
                    _mode = Mode.None;
                }
            }

            if (mode == Mode.BlockAfterLock &&
                point == OperationalCleanupFaultPoint.AfterLockAcquired)
            {
                await release.WaitAsync(cancellationToken);
            }
            else if (mode == Mode.CancelBeforeCommit &&
                     point == OperationalCleanupFaultPoint.BeforeCommit)
            {
                throw new OperationCanceledException(
                    "Simulated cleanup process termination");
            }
            else if (mode == Mode.FailAfterLock &&
                     point == OperationalCleanupFaultPoint.AfterLockAcquired)
            {
                throw new InvalidOperationException(
                    "Simulated cleanup database failure");
            }
        }

        private static TaskCompletionSource NewSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private enum Mode
        {
            None,
            CancelBeforeCommit,
            BlockAfterLock,
            FailAfterLock
        }
    }
}
