using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Reliant.Application.Abstractions;
using Reliant.Application.Dto;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;
using Reliant.Infrastructure.Provider;
using Reliant.Tests.Integration.Fixtures;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace Reliant.Tests.Integration.Phase2.Exp7;

[Trait("Category", "Integration")]
[Trait("Dependency", "PostgreSQL")]
[Trait("Dependency", "LocalStack")]
[Trait("Dependency", "WorkerHost")]
public sealed class RetryExhaustionE2ETests(ITestOutputHelper output)
{
    private const int MaxAttempts = 5;

    [Fact]
    public Task PersistentTransientFailure_ShouldExhaustRetryBudget_AndStop()
        => RunScenarioAsync(output);

    internal static async Task RunScenarioAsync(
        ITestOutputHelper output)
    {
        var startedAt = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        await using var fixture = new WorkerHostFixture();
        await fixture.InitializeAsync();
        await fixture.StartWorkersAsync(
            providerMode: "RateLimited",
            includeReconciliation: false);

        var (organizationId, contributionId) =
            await SeedContributionAndJobAsync(
                fixture.PgConnectionString);

        var reachedTerminalState = await WaitUntilAsync(
            async () =>
            {
                await using var db = CreateDbContext(
                    fixture.PgConnectionString);
                return await db.Contributions
                    .IgnoreQueryFilters()
                    .AnyAsync(x =>
                        x.Id == contributionId &&
                        x.State == ContributionState.Failed);
            },
            TimeSpan.FromSeconds(50));
        Assert.True(
            reachedTerminalState,
            "Contribution did not reach a terminal state." +
            Environment.NewLine +
            fixture.RecentLogs(100));

        var terminalAt = DateTime.UtcNow;
        var snapshot = await ReadSnapshotAsync(
            fixture.PgConnectionString,
            contributionId,
            organizationId);

        Assert.Equal(MaxAttempts, snapshot.Contribution.RetryCount);
        Assert.Equal(
            ErrorCategory.RateLimited,
            snapshot.Contribution.LastErrorCategory);
        Assert.Contains(
            "429",
            snapshot.Contribution.LastErrorMessage!,
            StringComparison.Ordinal);
        Assert.Null(snapshot.Contribution.NextRetryAt);

        Assert.Equal(MaxAttempts, snapshot.Attempts.Count);
        Assert.Equal(
            Enumerable.Range(1, MaxAttempts),
            snapshot.Attempts.Select(x => x.AttemptNumber));
        Assert.All(snapshot.Attempts, attempt =>
        {
            Assert.Equal(AttemptStatus.Failed, attempt.Status);
            Assert.Equal(
                ErrorCategory.RateLimited,
                attempt.ErrorCategory);
            Assert.NotNull(attempt.CompletedAt);
        });
        Assert.Single(
            snapshot.Attempts
                .Select(x => x.ProviderIdempotencyKey)
                .Distinct());

        Assert.Equal(MaxAttempts - 1, snapshot.RetryOutboxes.Count);
        Assert.Equal(MaxAttempts, snapshot.InboxCount);

        var deadLetter = Assert.Single(snapshot.DeadLetters);
        Assert.Equal(
            "ContributionRetryExhausted",
            deadLetter.MessageType);
        Assert.Equal(
            contributionId.ToString(),
            deadLetter.OriginalMessageId);
        Assert.Equal(MaxAttempts, deadLetter.AttemptCount);
        Assert.Equal(
            ErrorCategory.RateLimited,
            deadLetter.ErrorCategory);
        Assert.Contains(
            "429",
            deadLetter.ErrorMessage!,
            StringComparison.Ordinal);
        Assert.Equal(DeadLetterStatus.Pending, deadLetter.Status);
        Assert.InRange(
            deadLetter.DeadLetteredAt,
            startedAt,
            DateTime.UtcNow);
        Assert.Single(snapshot.OperatorAlerts);

        Assert.Equal(MaxAttempts, snapshot.JobRuns.Count);
        Assert.Equal(
            MaxAttempts - 1,
            snapshot.JobRuns.Count(x =>
                x.Status == JobStatus.Succeeded));
        Assert.Single(
            snapshot.JobRuns,
            x => x.Status == JobStatus.DeadLettered);
        Assert.Equal(MaxAttempts, snapshot.JobAttempts.Count);
        Assert.Equal(
            MaxAttempts - 1,
            snapshot.JobAttempts.Count(x =>
                x.Status == JobAttemptStatus.Succeeded));
        Assert.Single(
            snapshot.JobAttempts,
            x => x.Status == JobAttemptStatus.Failed);

        var backoffRecords = ParseBackoffRecords(
            fixture.LogLines,
            contributionId);
        Assert.Equal(MaxAttempts - 1, backoffRecords.Count);

        var expectedBaseDelays = new[] { 1000L, 2000L, 4000L, 8000L };
        for (var index = 0; index < expectedBaseDelays.Length; index++)
        {
            var record = backoffRecords[index];
            var expectedAttempt = index + 1;
            Assert.Equal(expectedAttempt, record.Attempt);
            Assert.InRange(
                record.DelayMs,
                expectedBaseDelays[index],
                expectedBaseDelays[index] + 1000);
        }

        Assert.Contains(
            fixture.LogLines,
            line =>
                line.Contains(
                    contributionId.ToString(),
                    StringComparison.Ordinal) &&
                line.Contains(
                    $"exhausted after {MaxAttempts} attempts",
                    StringComparison.OrdinalIgnoreCase));

        var attemptCountAtTerminal = snapshot.Attempts.Count;
        var retryOutboxCountAtTerminal =
            snapshot.RetryOutboxes.Count;
        await Task.Delay(TimeSpan.FromSeconds(3));

        var stableSnapshot = await ReadSnapshotAsync(
            fixture.PgConnectionString,
            contributionId,
            organizationId);
        Assert.Equal(
            attemptCountAtTerminal,
            stableSnapshot.Attempts.Count);
        Assert.Equal(
            retryOutboxCountAtTerminal,
            stableSnapshot.RetryOutboxes.Count);
        Assert.Equal(
            ContributionState.Failed,
            stableSnapshot.Contribution.State);
        Assert.Null(stableSnapshot.Contribution.NextRetryAt);
        Assert.Single(stableSnapshot.DeadLetters);

        var providerControl = fixture.Host.Services
            .GetRequiredService<ISandboxProviderControl>();
        Assert.Equal(0, providerControl.OperationCount);

        output.WriteLine(
            "CONFIG | ProviderMode=RateLimited | MaxAttempts={0} | BackoffBaseMs=1000 | BackoffCapMs=30000 | JitterMs=0-1000",
            MaxAttempts);
        output.WriteLine(
            "ATTEMPTS | Count={0} | Numbers={1} | Statuses=Failed | ErrorCategory=RateLimited | ProviderEffects={2}",
            snapshot.Attempts.Count,
            string.Join(
                ",",
                snapshot.Attempts.Select(x => x.AttemptNumber)),
            providerControl.OperationCount);
        output.WriteLine(
            "BACKOFF | {0}",
            string.Join(
                " | ",
                backoffRecords.Select(x =>
                    $"Attempt{x.Attempt}={x.DelayMs}ms")));
        output.WriteLine(
            "FINAL | Contribution=Failed | RetryCount={0} | NextRetryAt=null | DeadLetters={1} | JobRuns={2} | DeadLetteredJobs={3}",
            snapshot.Contribution.RetryCount,
            snapshot.DeadLetters.Count,
            snapshot.JobRuns.Count,
            snapshot.JobRuns.Count(x =>
                x.Status == JobStatus.DeadLettered));
        output.WriteLine(
            "STABILITY | WaitMs=3000 | AttemptsBefore={0} | AttemptsAfter={1} | RetryOutboxesBefore={2} | RetryOutboxesAfter={3} | ContinuedRetry=false",
            attemptCountAtTerminal,
            stableSnapshot.Attempts.Count,
            retryOutboxCountAtTerminal,
            stableSnapshot.RetryOutboxes.Count);
        output.WriteLine(
            "RESULT | PASS | StartedAt={0:O} | TerminalAt={1:O} | CompletedAt={2:O} | DurationMs={3}",
            startedAt,
            terminalAt,
            DateTime.UtcNow,
            stopwatch.ElapsedMilliseconds);
    }

    private static async Task<(Guid OrganizationId, Guid ContributionId)>
        SeedContributionAndJobAsync(string connectionString)
    {
        var organizationId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var contributionId = Guid.NewGuid();
        var outbox = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MessageType = "ContributionCreated",
            CorrelationId = "phase2-exp7-retry-exhaustion",
            OccurredAt = DateTime.UtcNow,
            Status = OutboxStatus.Pending,
            Version = 0
        };
        outbox.Payload = JsonSerializer.Serialize(
            new ContributionProcessingMessage(
                Version: 1,
                ContributionId: contributionId,
                OrganizationId: organizationId,
                Trigger: "Created",
                CorrelationId: outbox.CorrelationId));

        await using var db = CreateDbContext(connectionString);
        db.Organizations.Add(new Organization
        {
            Id = organizationId,
            Name = "Phase 2 Retry Exhaustion Lab",
            Status = OrganizationStatus.Active,
            Version = 0
        });
        db.Campaigns.Add(new Campaign
        {
            Id = campaignId,
            OrganizationId = organizationId,
            Name = "Experiment 7",
            Status = CampaignStatus.Active,
            Version = 0
        });
        db.Contributions.Add(new Contribution
        {
            Id = contributionId,
            OrganizationId = organizationId,
            CampaignId = campaignId,
            ExternalReference = "PHASE2-EXP7-001",
            Amount = 100m,
            Currency = "NZD",
            State = ContributionState.Created,
            Version = 0
        });
        db.OutboxMessages.Add(outbox);
        db.JobRuns.Add(
            JobRun.ForContributionProcessing(outbox));
        await db.SaveChangesAsync();

        return (organizationId, contributionId);
    }

    private static async Task<RetrySnapshot> ReadSnapshotAsync(
        string connectionString,
        Guid contributionId,
        Guid organizationId)
    {
        await using var db = CreateDbContext(connectionString);
        var contribution = await db.Contributions
            .IgnoreQueryFilters()
            .SingleAsync(x => x.Id == contributionId);
        var attempts = await db.ProcessingAttempts
            .IgnoreQueryFilters()
            .Where(x => x.ContributionId == contributionId)
            .OrderBy(x => x.AttemptNumber)
            .ToListAsync();
        var retryOutboxes = await db.OutboxMessages
            .IgnoreQueryFilters()
            .Where(x =>
                x.OrganizationId == organizationId &&
                x.MessageType == "ContributionRetryRequested")
            .OrderBy(x => x.OccurredAt)
            .ToListAsync();
        var inboxCount = await db.InboxMessages
            .IgnoreQueryFilters()
            .CountAsync(x => x.OrganizationId == organizationId);
        var deadLetters = await db.DeadLetterRecords
            .IgnoreQueryFilters()
            .Where(x =>
                x.OrganizationId == organizationId &&
                x.MessageType == "ContributionRetryExhausted")
            .ToListAsync();
        var operatorAlerts = await db.OutboxMessages
            .IgnoreQueryFilters()
            .Where(x =>
                x.OrganizationId == organizationId &&
                x.MessageType == "OperatorAlert")
            .ToListAsync();
        var jobRuns = await db.JobRuns
            .IgnoreQueryFilters()
            .Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync();
        var jobRunIds = jobRuns.Select(x => x.Id).ToList();
        var jobAttempts = await db.JobAttempts
            .IgnoreQueryFilters()
            .Where(x => jobRunIds.Contains(x.JobRunId))
            .OrderBy(x => x.StartedAt)
            .ToListAsync();

        return new RetrySnapshot(
            contribution,
            attempts,
            retryOutboxes,
            inboxCount,
            deadLetters,
            operatorAlerts,
            jobRuns,
            jobAttempts);
    }

    private static List<BackoffRecord> ParseBackoffRecords(
        IEnumerable<string> logs,
        Guid contributionId)
    {
        var pattern = new Regex(
            @"attempt (?<attempt>\d+)/5, delay (?<delay>\d+) ms",
            RegexOptions.IgnoreCase);

        return logs
            .Where(line =>
                line.Contains(
                    contributionId.ToString(),
                    StringComparison.Ordinal) &&
                line.Contains(
                    "Retry backoff scheduled",
                    StringComparison.OrdinalIgnoreCase))
            .Select(line => pattern.Match(line))
            .Where(match => match.Success)
            .Select(match => new BackoffRecord(
                int.Parse(match.Groups["attempt"].Value),
                long.Parse(match.Groups["delay"].Value)))
            .OrderBy(x => x.Attempt)
            .ToList();
    }

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

            await Task.Delay(250);
        }

        return await condition();
    }

    private static ReliantDbContext CreateDbContext(
        string connectionString)
        => new(
            new DbContextOptionsBuilder<ReliantDbContext>()
                .UseNpgsql(connectionString)
                .Options);

    private sealed record BackoffRecord(
        int Attempt,
        long DelayMs);

    private sealed record RetrySnapshot(
        Contribution Contribution,
        List<ProcessingAttempt> Attempts,
        List<OutboxMessage> RetryOutboxes,
        int InboxCount,
        List<DeadLetterRecord> DeadLetters,
        List<OutboxMessage> OperatorAlerts,
        List<JobRun> JobRuns,
        List<JobAttempt> JobAttempts);
}
