using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Reliant.Application.Abstractions;
using Reliant.Application.Contributions.Commands;
using Reliant.Application.Messaging;
using Reliant.Application.Tenancy;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;
using Reliant.Infrastructure.Provider;
using Reliant.Tests.Integration.Fixtures;
using Reliant.Tests.TestHelpers;
using Xunit.Abstractions;

namespace Reliant.Tests.Integration.Phase3.Exp11;

[Trait("Category", "Integration")]
[Trait("Dependency", "PostgreSQL")]
[Trait("Dependency", "LocalStack")]
[Trait("Dependency", "WorkerHost")]
public sealed class CircuitOpenNoAckE2ETests(
    ITestOutputHelper output)
{
    [Fact]
    public async Task CircuitOpen_ShouldNotAckOrConsumeBusinessRetryBudget()
    {
        var startedAt = DateTime.UtcNow;
        var organizationId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();

        await using var fixture = new WorkerHostFixture();
        await fixture.InitializeAsync();
        await SeedOrganizationAndCampaignAsync(
            fixture.PgConnectionString,
            organizationId,
            campaignId);

        // The worker itself uses this raw SDK adapter, so native
        // ApproximateReceiveCount is observed without racing a probe consumer.
        var queue = new RawSqsQueueAdapter(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Queue:Endpoint"] = fixture.SqsEndpoint,
                        ["Queue:Region"] = "us-west-1"
                    })
                .Build());

        try
        {
            await fixture.StartWorkersAsync(
                providerMode: "Success",
                includeReconciliation: false,
                visibilityTimeoutSeconds: 3,
                queueAdapterOverride: queue,
                maxReceiveCount: 10,
                heartbeatIntervalMs: 1000,
                processingConcurrency: 1);

            var circuit = fixture.Host.Services
                .GetRequiredService<CircuitBreaker>();
            for (var failure = 0; failure < 5; failure++)
            {
                circuit.RecordFailure(ErrorCategory.ServerError);
            }

            Assert.Equal(CircuitBreakerState.Open, circuit.State);

            var created = await CreateContributionAsync(
                fixture,
                organizationId,
                campaignId);
            Assert.Equal(201, created.StatusCode);
            Assert.False(created.WasCached);
            Assert.NotNull(created.Body);
            var contributionId = created.Body!.Id;

            Guid messageId;
            await using (var db = CreateDbContext(
                fixture.PgConnectionString))
            {
                messageId = await db.OutboxMessages
                    .IgnoreQueryFilters()
                    .Where(x =>
                        x.OrganizationId == organizationId &&
                        x.Payload.Contains(contributionId.ToString()))
                    .Select(x => x.Id)
                    .SingleAsync();
                var job = await db.JobRuns
                    .IgnoreQueryFilters()
                    .SingleAsync(x => x.Id == messageId);
                Assert.Equal(JobStatus.Pending, job.Status);
                Assert.Equal(0, job.AttemptCount);
            }

            var provider = fixture.Host.Services
                .GetRequiredService<ISandboxProviderControl>();

            // Wait until at least two full open-circuit deliveries have ended as
            // Deferred. This proves visibility redelivery while avoiding a race
            // where ReceiveCount advances before the second JobAttempt commits.
            var openRedeliveryCommitted = await WaitUntilAsync(
                async () =>
                {
                    await using var db = CreateDbContext(
                        fixture.PgConnectionString);
                    var attempts = await db.JobAttempts
                        .IgnoreQueryFilters()
                        .Where(x => x.JobRunId == messageId)
                        .ToListAsync();
                    return queue.MaxApproximateReceiveCount >= 2 &&
                        queue.DeleteCount == 0 &&
                        attempts.Count >= 2 &&
                        attempts.All(x =>
                            x.Status == JobAttemptStatus.Deferred);
                },
                TimeSpan.FromSeconds(60));
            Assert.True(
                openRedeliveryCommitted,
                "The message did not complete two open-circuit deferrals." +
                Environment.NewLine +
                $"ReceiveCount={queue.ReceiveCount}, " +
                $"ApproxReceiveCount={queue.MaxApproximateReceiveCount}, " +
                $"DeleteCount={queue.DeleteCount}" +
                Environment.NewLine +
                fixture.RecentLogs(100));

            var openReceiveCount = queue.ReceiveCount;
            int openJobAttemptCount;
            await using (var db = CreateDbContext(
                fixture.PgConnectionString))
            {
                var contribution = await db.Contributions
                    .IgnoreQueryFilters()
                    .SingleAsync(x => x.Id == contributionId);
                var outbox = await db.OutboxMessages
                    .IgnoreQueryFilters()
                    .SingleAsync(x => x.Id == messageId);
                var job = await db.JobRuns
                    .IgnoreQueryFilters()
                    .SingleAsync(x => x.Id == messageId);
                var jobAttempts = await db.JobAttempts
                    .IgnoreQueryFilters()
                    .Where(x => x.JobRunId == messageId)
                    .OrderBy(x => x.AttemptNumber)
                    .ToListAsync();
                openJobAttemptCount = jobAttempts.Count;

                Assert.Equal(
                    ContributionState.Processing,
                    contribution.State);
                Assert.Equal(0, contribution.RetryCount);
                Assert.Null(contribution.NextRetryAt);
                Assert.Equal(OutboxStatus.Sent, outbox.Status);
                Assert.Equal(JobStatus.Pending, job.Status);
                Assert.Equal(jobAttempts.Count, job.AttemptCount);
                Assert.True(jobAttempts.Count >= 2);
                Assert.All(
                    jobAttempts,
                    attempt =>
                    {
                        Assert.Equal(
                            JobAttemptStatus.Deferred,
                            attempt.Status);
                        Assert.Equal(
                            "Provider circuit is open",
                            attempt.ErrorMessage);
                        Assert.NotNull(attempt.CompletedAt);
                    });
                Assert.Equal(
                    0,
                    await db.ProcessingAttempts
                        .IgnoreQueryFilters()
                        .CountAsync(x =>
                            x.ContributionId == contributionId));
                Assert.Equal(
                    0,
                    await db.ProviderReferences
                        .IgnoreQueryFilters()
                        .CountAsync(x =>
                            x.ContributionId == contributionId));
                Assert.Equal(
                    0,
                    await db.InboxMessages
                        .IgnoreQueryFilters()
                        .CountAsync(x =>
                            x.MessageId == messageId.ToString()));
                Assert.Equal(
                    0,
                    await db.Leases
                        .IgnoreQueryFilters()
                        .CountAsync(x =>
                            x.JobRunId == messageId &&
                            x.IsActive));
            }

            Assert.Equal(CircuitBreakerState.Open, circuit.State);
            Assert.Equal(0, provider.OperationCount);
            Assert.Equal(0, queue.DeleteCount);
            Assert.True(queue.ReceiveCount >= 2);
            Assert.True(queue.MaxApproximateReceiveCount >= 2);

            output.WriteLine(
                "OPEN | MessageId={0} | Circuit=Open | " +
                "State=Processing | ProviderOperation=0 | " +
                "BusinessAttempt=0 | RetryCount=0 | Inbox=0 | " +
                "Delete=0 | Receive={1} | ApproxReceive={2} | " +
                "JobAttempts={3}/all-Deferred",
                messageId,
                openReceiveCount,
                queue.MaxApproximateReceiveCount,
                openJobAttemptCount);

            // Explicitly close the circuit. The next physical redelivery must
            // perform the one provider operation and ACK the original message.
            circuit.RecordSuccess();
            Assert.Equal(CircuitBreakerState.Closed, circuit.State);

            var recovered = await WaitUntilAsync(
                async () =>
                {
                    await using var db = CreateDbContext(
                        fixture.PgConnectionString);
                    var contribution = await db.Contributions
                        .IgnoreQueryFilters()
                        .SingleAsync(x => x.Id == contributionId);
                    var inboxExists = await db.InboxMessages
                        .IgnoreQueryFilters()
                        .AnyAsync(x =>
                            x.MessageId == messageId.ToString());
                    return contribution.State ==
                            ContributionState.Succeeded &&
                        inboxExists &&
                        queue.DeleteCount == 1;
                },
                TimeSpan.FromSeconds(90));
            Assert.True(
                recovered,
                "The redelivered message did not recover after close." +
                Environment.NewLine +
                fixture.RecentLogs(120));

            await using (var db = CreateDbContext(
                fixture.PgConnectionString))
            {
                var contribution = await db.Contributions
                    .IgnoreQueryFilters()
                    .SingleAsync(x => x.Id == contributionId);
                var processingAttempt = await db.ProcessingAttempts
                    .IgnoreQueryFilters()
                    .SingleAsync(x =>
                        x.ContributionId == contributionId);
                var providerReference = await db.ProviderReferences
                    .IgnoreQueryFilters()
                    .SingleAsync(x =>
                        x.ContributionId == contributionId);
                var inbox = await db.InboxMessages
                    .IgnoreQueryFilters()
                    .SingleAsync(x =>
                        x.MessageId == messageId.ToString());
                var job = await db.JobRuns
                    .IgnoreQueryFilters()
                    .SingleAsync(x => x.Id == messageId);
                var jobAttempts = await db.JobAttempts
                    .IgnoreQueryFilters()
                    .Where(x => x.JobRunId == messageId)
                    .OrderBy(x => x.AttemptNumber)
                    .ToListAsync();
                var transitions = await db.StateTransitions
                    .IgnoreQueryFilters()
                    .Where(x => x.ContributionId == contributionId)
                    .ToListAsync();

                Assert.Equal(
                    ContributionState.Succeeded,
                    contribution.State);
                Assert.Equal(0, contribution.RetryCount);
                Assert.Null(contribution.NextRetryAt);
                Assert.Equal(
                    AttemptStatus.Succeeded,
                    processingAttempt.Status);
                Assert.Equal(1, processingAttempt.AttemptNumber);
                Assert.Equal(
                    processingAttempt.ProviderReference,
                    providerReference.Reference);
                Assert.Equal(InboxStatus.Processed, inbox.Status);
                Assert.Equal(JobStatus.Succeeded, job.Status);
                Assert.Equal(jobAttempts.Count, job.AttemptCount);
                Assert.Equal(
                    openJobAttemptCount,
                    jobAttempts.Count(x =>
                        x.Status == JobAttemptStatus.Deferred));
                Assert.Single(
                    jobAttempts,
                    x => x.Status == JobAttemptStatus.Succeeded);
                Assert.Single(
                    transitions,
                    x => x.ToState ==
                        ContributionState.Succeeded);
                Assert.Equal(
                    0,
                    await db.Leases
                        .IgnoreQueryFilters()
                        .CountAsync(x =>
                            x.JobRunId == messageId &&
                            x.IsActive));
                Assert.Equal(
                    0,
                    await db.DeadLetterRecords
                        .IgnoreQueryFilters()
                        .CountAsync());
            }

            Assert.Equal(CircuitBreakerState.Closed, circuit.State);
            Assert.Equal(1, provider.OperationCount);
            Assert.Equal(1, queue.DeleteCount);
            Assert.Contains(
                fixture.LogLines,
                line => line.Contains(
                    "deferred because circuit is open",
                    StringComparison.Ordinal));
            Assert.Contains(
                fixture.LogLines,
                line => line.Contains(
                    "approximate receive count 2",
                    StringComparison.Ordinal));

            var queueUrl = await queue.GetOrCreateQueueAsync(
                fixture.QueueName);
            Assert.Null(await queue.ReceiveAsync(
                queueUrl,
                visibilityTimeoutSeconds: 0));

            output.WriteLine(
                "RECOVERED | Circuit=Closed | State=Succeeded | " +
                "ProviderOperation=1 | BusinessAttempt=1/Succeeded | " +
                "ProviderReference=1 | Inbox=1 | Delete=1 | " +
                "Job=Succeeded | DeadLetter=0 | Queue=empty");
            output.WriteLine(
                "FINAL | RetryBudgetConsumedDuringOpen=0 | " +
                "SilentLoss=0 | RESULT=PASS | " +
                "StartedAt={0:O} | CompletedAt={1:O}",
                startedAt,
                DateTime.UtcNow);
        }
        finally
        {
            TenantFilterAccessor.Clear();
        }
    }

    private static async Task<
        Reliant.Application.Dto.IdempotentResponse<
            Reliant.Application.Dto.ContributionResponse>>
        CreateContributionAsync(
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
            role: null,
            correlationId: "phase3-exp11-circuit-open");
        TenantFilterAccessor.SetOrganizationId(organizationId);
        try
        {
            var sender = scope.ServiceProvider
                .GetRequiredService<MediatR.ISender>();
            return await sender.Send(new CreateContributionCommand(
                campaignId,
                ExternalReference: "PHASE3-EXP11-001",
                Amount: 275m,
                Currency: "NZD",
                IdempotencyKey: "phase3-exp11-create"));
        }
        finally
        {
            TenantFilterAccessor.Clear();
        }
    }

    private static async Task SeedOrganizationAndCampaignAsync(
        string connectionString,
        Guid organizationId,
        Guid campaignId)
    {
        await using var db = CreateDbContext(connectionString);
        db.Organizations.Add(new Organization
        {
            Id = organizationId,
            Name = "Phase 3 Experiment 11 Org",
            Status = OrganizationStatus.Active,
            Version = 0
        });
        db.Campaigns.Add(new Campaign
        {
            Id = campaignId,
            OrganizationId = organizationId,
            Name = "Phase 3 Experiment 11",
            Status = CampaignStatus.Active,
            Version = 0
        });
        await db.SaveChangesAsync();
    }

    private static ReliantDbContext CreateDbContext(
        string connectionString)
    {
        var options =
            new DbContextOptionsBuilder<ReliantDbContext>()
                .UseNpgsql(connectionString)
                .Options;
        return new ReliantDbContext(options);
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
}
