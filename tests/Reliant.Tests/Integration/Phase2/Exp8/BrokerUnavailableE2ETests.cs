using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Reliant.Application.Abstractions;
using Reliant.Application.Contributions.Commands;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;
using Reliant.Infrastructure.Provider;
using Reliant.Tests.Integration.Fixtures;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace Reliant.Tests.Integration.Phase2.Exp8;

[Trait("Category", "Integration")]
[Trait("Dependency", "PostgreSQL")]
[Trait("Dependency", "LocalStack")]
[Trait("Dependency", "WorkerHost")]
public sealed class BrokerUnavailableE2ETests(ITestOutputHelper output)
{
    [Fact]
    public async Task BrokerOutage_ShouldPreserveOutbox_AndPublishAfterRecovery()
    {
        var startedAt = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        await using var fixture = new WorkerHostFixture();
        await fixture.InitializeAsync();
        await fixture.StartWorkersAsync(
            providerMode: "Success",
            includeReconciliation: false,
            outboxRetryBaseMs: 500,
            outboxRetryCapMs: 2000,
            outboxRetryJitterMs: 0,
            queueRequestTimeoutSeconds: 5,
            queuePublishTimeoutSeconds: 1,
            queueMaxErrorRetry: 0);
        await EnsureQueueExistsAsync(fixture);

        var organizationId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        await SeedTenantAsync(
            fixture.PgConnectionString,
            organizationId,
            campaignId);

        await fixture.StopBrokerAsync();
        var brokerStoppedAt = DateTime.UtcNow;

        var contributionId = await CreateContributionAsync(
            fixture,
            organizationId,
            campaignId);

        Guid outboxId;
        await using (var db = CreateDbContext(
            fixture.PgConnectionString))
        {
            var contribution = await db.Contributions
                .IgnoreQueryFilters()
                .SingleAsync(x => x.Id == contributionId);
            var outbox = await db.OutboxMessages
                .IgnoreQueryFilters()
                .SingleAsync(x =>
                    x.OrganizationId == organizationId &&
                    x.MessageType == "ContributionCreated");
            var jobRun = await db.JobRuns
                .IgnoreQueryFilters()
                .SingleAsync(x => x.Id == outbox.Id);

            Assert.Equal(
                ContributionState.Created,
                contribution.State);
            Assert.Equal(OutboxStatus.Pending, outbox.Status);
            Assert.Null(outbox.SentAt);
            Assert.Equal(JobStatus.Pending, jobRun.Status);
            outboxId = outbox.Id;
        }

        var observedFailures = await WaitUntilAsync(
            () => Task.FromResult(
                GetPublishFailureLogs(
                    fixture.LogLines,
                    outboxId).Count >= 3),
            TimeSpan.FromSeconds(30));
        Assert.True(
            observedFailures,
            "Publisher did not expose three classified failures." +
            Environment.NewLine +
            fixture.RecentLogs(100));

        var outageLogs = GetPublishFailureLogs(
            fixture.LogLines,
            outboxId);
        Assert.InRange(outageLogs.Count, 3, 4);
        Assert.All(outageLogs, log =>
        {
            Assert.True(
                log.Contains(
                    "NetworkFailure",
                    StringComparison.Ordinal) ||
                log.Contains(
                    "Timeout",
                    StringComparison.Ordinal),
                $"Unexpected broker error classification: {log}");
            Assert.Contains(
                "transient=True",
                log,
                StringComparison.OrdinalIgnoreCase);
        });

        await using (var db = CreateDbContext(
            fixture.PgConnectionString))
        {
            var contribution = await db.Contributions
                .IgnoreQueryFilters()
                .SingleAsync(x => x.Id == contributionId);
            var outbox = await db.OutboxMessages
                .IgnoreQueryFilters()
                .SingleAsync(x => x.Id == outboxId);
            var attempts = await db.ProcessingAttempts
                .IgnoreQueryFilters()
                .CountAsync(x =>
                    x.ContributionId == contributionId);

            Assert.Equal(
                ContributionState.Created,
                contribution.State);
            Assert.Equal(OutboxStatus.Pending, outbox.Status);
            Assert.Null(outbox.SentAt);
            Assert.Equal(outageLogs.Count, outbox.SendCount);
            Assert.Equal(0, attempts);
        }

        var retryDelays = ParseRetryDelays(outageLogs);
        Assert.Equal(
            new[] { 500L, 1000L, 2000L },
            retryDelays.Take(3));
        var observedCategories = outageLogs
            .Select(log => log.Contains(
                    "category=Timeout",
                    StringComparison.Ordinal)
                ? "Timeout"
                : "NetworkFailure")
            .Distinct()
            .ToArray();

        await fixture.StartBrokerAsync();
        var brokerRecoveredAt = DateTime.UtcNow;

        var recovered = await WaitUntilAsync(
            async () =>
            {
                await using var db = CreateDbContext(
                    fixture.PgConnectionString);
                var outbox = await db.OutboxMessages
                    .IgnoreQueryFilters()
                    .SingleAsync(x => x.Id == outboxId);
                var contribution = await db.Contributions
                    .IgnoreQueryFilters()
                    .SingleAsync(x => x.Id == contributionId);
                return outbox.Status == OutboxStatus.Sent &&
                    contribution.State ==
                        ContributionState.Succeeded;
            },
            TimeSpan.FromSeconds(40));
        Assert.True(
            recovered,
            "Outbox did not resume after LocalStack recovery." +
            Environment.NewLine +
            fixture.RecentLogs(120));
        var businessCompletedAt = DateTime.UtcNow;

        OutboxMessage finalOutbox;
        int inboxCount;
        int processingAttemptCount;
        int providerReferenceCount;
        await using (var db = CreateDbContext(
            fixture.PgConnectionString))
        {
            finalOutbox = await db.OutboxMessages
                .IgnoreQueryFilters()
                .SingleAsync(x => x.Id == outboxId);
            inboxCount = await db.InboxMessages
                .IgnoreQueryFilters()
                .CountAsync(x =>
                    x.OrganizationId == organizationId &&
                    x.MessageId == outboxId.ToString());
            processingAttemptCount = await db.ProcessingAttempts
                .IgnoreQueryFilters()
                .CountAsync(x =>
                    x.ContributionId == contributionId);
            providerReferenceCount = await db.ProviderReferences
                .IgnoreQueryFilters()
                .CountAsync(x =>
                    x.ContributionId == contributionId);
        }

        Assert.Equal(OutboxStatus.Sent, finalOutbox.Status);
        Assert.NotNull(finalOutbox.SentAt);
        Assert.Equal(1, inboxCount);
        Assert.Equal(1, processingAttemptCount);
        Assert.Equal(1, providerReferenceCount);

        var provider = fixture.Host.Services
            .GetRequiredService<IProvider>() as SandboxProvider;
        Assert.NotNull(provider);
        Assert.Equal(1, provider.OperationCount);

        var sendCountAtRecovery = finalOutbox.SendCount;
        var failureLogsAtRecovery = GetPublishFailureLogs(
            fixture.LogLines,
            outboxId).Count;
        Assert.Equal(
            failureLogsAtRecovery,
            finalOutbox.SendCount);
        await Task.Delay(TimeSpan.FromSeconds(3));

        await using (var db = CreateDbContext(
            fixture.PgConnectionString))
        {
            var stableOutbox = await db.OutboxMessages
                .IgnoreQueryFilters()
                .SingleAsync(x => x.Id == outboxId);
            Assert.Equal(OutboxStatus.Sent, stableOutbox.Status);
            Assert.Equal(
                sendCountAtRecovery,
                stableOutbox.SendCount);
        }
        Assert.Equal(
            failureLogsAtRecovery,
            GetPublishFailureLogs(
                fixture.LogLines,
                outboxId).Count);

        output.WriteLine(
            "OUTAGE | Broker=LocalStackPaused | BusinessState=Created | OutboxStatus=Pending | SentAt=null | Failures={0} | Category={1} | Transient=true",
            outageLogs.Count,
            string.Join(",", observedCategories));
        output.WriteLine(
            "BACKOFF | {0}",
            string.Join(
                " | ",
                retryDelays.Take(3).Select(
                    (delay, index) =>
                        $"Failure{index + 1}={delay}ms")));
        output.WriteLine(
            "RECOVERY | BrokerRestarted=true | OutboxStatus=Sent | SendCount={0} | BusinessState=Succeeded | RecoveryMs={1}",
            finalOutbox.SendCount,
            (long)(businessCompletedAt -
                brokerRecoveredAt).TotalMilliseconds);
        output.WriteLine(
            "FINAL | BusinessRows=1 | OutboxRows=1 | InboxRows={0} | ProcessingAttempts={1} | ProviderReferences={2} | ProviderEffects={3} | SilentLoss=false",
            inboxCount,
            processingAttemptCount,
            providerReferenceCount,
            provider.OperationCount);
        output.WriteLine(
            "STABILITY | WaitMs=3000 | SendCountBefore={0} | SendCountAfter={0} | FailureLogsBefore={1} | FailureLogsAfter={1} | ContinuedRetry=false",
            sendCountAtRecovery,
            failureLogsAtRecovery);
        output.WriteLine(
            "RESULT | PASS | StartedAt={0:O} | BrokerStoppedAt={1:O} | BrokerRecoveredAt={2:O} | CompletedAt={3:O} | DurationMs={4}",
            startedAt,
            brokerStoppedAt,
            brokerRecoveredAt,
            DateTime.UtcNow,
            stopwatch.ElapsedMilliseconds);
    }

    private static async Task EnsureQueueExistsAsync(
        WorkerHostFixture fixture)
    {
        using var scope = fixture.Host.Services.CreateScope();
        var queue = scope.ServiceProvider
            .GetRequiredService<IQueueAdapter>();
        await queue.GetOrCreateQueueAsync(fixture.QueueName);
    }

    private static async Task SeedTenantAsync(
        string connectionString,
        Guid organizationId,
        Guid campaignId)
    {
        await using var db = CreateDbContext(connectionString);
        db.Organizations.Add(new Organization
        {
            Id = organizationId,
            Name = "Phase 2 Broker Outage Lab",
            Status = OrganizationStatus.Active,
            Version = 0
        });
        db.Campaigns.Add(new Campaign
        {
            Id = campaignId,
            OrganizationId = organizationId,
            Name = "Experiment 8",
            Status = CampaignStatus.Active,
            Version = 0
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> CreateContributionAsync(
        WorkerHostFixture fixture,
        Guid organizationId,
        Guid campaignId)
    {
        using var scope = fixture.Host.Services.CreateScope();
        var tenant = scope.ServiceProvider
            .GetRequiredService<ITenantContext>();
        tenant.SetTenant(
            organizationId,
            userId: null,
            role: "student",
            correlationId: "phase2-exp8-broker-outage");
        TenantFilterAccessor.SetOrganizationId(organizationId);

        try
        {
            var sender = scope.ServiceProvider
                .GetRequiredService<ISender>();
            var response = await sender.Send(
                new CreateContributionCommand(
                    campaignId,
                    ExternalReference: "PHASE2-EXP8-001",
                    Amount: 100m,
                    Currency: "NZD",
                    IdempotencyKey:
                        "phase2-exp8-create-001"));
            Assert.Equal(201, response.StatusCode);
            Assert.NotNull(response.Body);
            return response.Body!.Id;
        }
        finally
        {
            TenantFilterAccessor.Clear();
        }
    }

    private static List<string> GetPublishFailureLogs(
        IEnumerable<string> logs,
        Guid outboxId)
        => logs
            .Where(line =>
                line.Contains(
                    outboxId.ToString(),
                    StringComparison.Ordinal) &&
                (line.Contains(
                        "Outbox publish failure",
                        StringComparison.OrdinalIgnoreCase) ||
                    line.Contains(
                        "Failed to send outbox message",
                        StringComparison.OrdinalIgnoreCase)))
            .ToList();

    private static List<long> ParseRetryDelays(
        IEnumerable<string> logs)
    {
        var pattern = new Regex(
            @"retry in (?<delay>\d+) ms",
            RegexOptions.IgnoreCase);
        return logs
            .Select(line => pattern.Match(line))
            .Where(match => match.Success)
            .Select(match =>
                long.Parse(match.Groups["delay"].Value))
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
}
