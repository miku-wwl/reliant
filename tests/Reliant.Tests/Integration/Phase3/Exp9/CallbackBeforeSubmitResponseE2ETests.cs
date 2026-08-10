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
using Reliant.Infrastructure.Queue;
using Reliant.Tests.Integration.Fixtures;
using Reliant.Tests.TestHelpers;
using System.Globalization;
using Xunit.Abstractions;

namespace Reliant.Tests.Integration.Phase3.Exp9;

[Trait("Category", "Integration")]
[Trait("Dependency", "PostgreSQL")]
[Trait("Dependency", "LocalStack")]
[Trait("Dependency", "WorkerHost")]
public sealed class CallbackBeforeSubmitResponseE2ETests(
    ITestOutputHelper output)
{
    private const string CallbackEventId =
        "phase3-exp9-callback-before-response";

    [Fact]
    public async Task CallbackBeforeSubmitResponse_ShouldWinWithoutLostUpdate()
    {
        var startedAt = DateTime.UtcNow;
        var organizationId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        const int maxReceiveCount = 10;

        await using var fixture = new WorkerHostFixture();
        await fixture.InitializeAsync();
        await SeedOrganizationAndCampaignAsync(
            fixture.PgConnectionString,
            organizationId,
            campaignId);

        var countingQueue = new CountingQueueAdapter(
            new SqsQueueAdapter(CreateQueueConfiguration(
                fixture.SqsEndpoint,
                maxReceiveCount)));
        using var delayedResponse =
            new DelayAfterProviderProcessedFault();

        try
        {
            await fixture.StartWorkersAsync(
                providerMode: "Success",
                includeReconciliation: false,
                faultInjector: delayedResponse,
                queueAdapterOverride: countingQueue,
                maxReceiveCount: maxReceiveCount,
                processingConcurrency: 1);

            var created = await CreateContributionAsync(
                fixture,
                organizationId,
                campaignId);
            Assert.Equal(201, created.StatusCode);
            Assert.False(created.WasCached);
            Assert.NotNull(created.Body);
            var contributionId = created.Body!.Id;

            await delayedResponse.WaitUntilProviderProcessedAsync(
                TimeSpan.FromSeconds(60));

            var provider = fixture.Host.Services
                .GetRequiredService<ISandboxProviderControl>();
            Assert.Equal(1, provider.OperationCount);
            Assert.Equal(1, countingQueue.SendCount);
            Assert.Equal(1, countingQueue.ReceiveCount);
            Assert.Equal(0, countingQueue.DeleteCount);

            Guid businessMessageId;
            string providerKey;
            await using (var db = CreateDbContext(
                fixture.PgConnectionString))
            {
                var contribution = await db.Contributions
                    .IgnoreQueryFilters()
                    .SingleAsync(x => x.Id == contributionId);
                var attempt = await db.ProcessingAttempts
                    .IgnoreQueryFilters()
                    .SingleAsync(x =>
                        x.ContributionId == contributionId);
                businessMessageId = await db.OutboxMessages
                    .IgnoreQueryFilters()
                    .Where(x =>
                        x.OrganizationId == organizationId &&
                        x.Payload.Contains(contributionId.ToString()))
                    .Select(x => x.Id)
                    .SingleAsync();
                providerKey = attempt.ProviderIdempotencyKey;

                Assert.Equal(
                    ContributionState.Processing,
                    contribution.State);
                Assert.Equal(AttemptStatus.Pending, attempt.Status);
                Assert.Equal(1, attempt.AttemptNumber);
                Assert.Null(attempt.CompletedAt);
                Assert.Null(attempt.ProviderReference);
                Assert.False(string.IsNullOrWhiteSpace(providerKey));
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
                            x.MessageId == businessMessageId.ToString()));
            }

            output.WriteLine(
                "PROVIDER COMPLETE / RESPONSE PAUSED | " +
                "MessageId={0} | State=Processing | " +
                "Attempt=1/Pending | ProviderOperation=1 | " +
                "ProviderKey={1} | ProcessingInbox=0 | ACK=0",
                businessMessageId,
                providerKey);

            var callback = new ProviderCallbackPayload(
                EventId: CallbackEventId,
                EventType: "contribution.submit",
                ProviderReference: null,
                IdempotencyKey: providerKey,
                Status: "succeeded",
                OccurredAt: DateTime.UtcNow.ToString("O"),
                Version: 1);
            var firstCallback = await SendCallbackAsync(
                fixture,
                callback);
            Assert.Equal(200, firstCallback.StatusCode);

            await using (var db = CreateDbContext(
                fixture.PgConnectionString))
            {
                var contribution = await db.Contributions
                    .IgnoreQueryFilters()
                    .SingleAsync(x => x.Id == contributionId);
                var attempt = await db.ProcessingAttempts
                    .IgnoreQueryFilters()
                    .SingleAsync(x =>
                        x.ContributionId == contributionId);
                var callbackInbox = await db.InboxMessages
                    .IgnoreQueryFilters()
                    .SingleAsync(x =>
                        x.MessageId ==
                            $"callback-{CallbackEventId}");
                var successTransition = await db.StateTransitions
                    .IgnoreQueryFilters()
                    .SingleAsync(x =>
                        x.ContributionId == contributionId &&
                        x.ToState == ContributionState.Succeeded);

                Assert.Equal(
                    ContributionState.Succeeded,
                    contribution.State);
                Assert.Equal(AttemptStatus.Pending, attempt.Status);
                Assert.Equal(InboxStatus.Processed, callbackInbox.Status);
                Assert.Equal(
                    ContributionState.Processing,
                    successTransition.FromState);
                Assert.Equal(
                    "CallbackHandler",
                    successTransition.ChangedBy);
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
                            x.MessageId == businessMessageId.ToString()));
            }

            output.WriteLine(
                "CALLBACK WINS | HTTP-equivalent={0} | " +
                "State=Succeeded | CallbackInbox=1 | " +
                "SucceededTransition=CallbackHandler | " +
                "AttemptStill=Pending | SubmitResponse=paused",
                firstCallback.StatusCode);

            delayedResponse.ReleaseResponse();

            var completed = await WaitUntilAsync(
                async () =>
                {
                    await using var db = CreateDbContext(
                        fixture.PgConnectionString);
                    var state = await db.Contributions
                        .IgnoreQueryFilters()
                        .Where(x => x.Id == contributionId)
                        .Select(x => x.State)
                        .SingleAsync();
                    var attemptStatus = await db.ProcessingAttempts
                        .IgnoreQueryFilters()
                        .Where(x =>
                            x.ContributionId == contributionId)
                        .Select(x => x.Status)
                        .SingleAsync();
                    return state == ContributionState.Succeeded &&
                        attemptStatus == AttemptStatus.Succeeded &&
                        countingQueue.DeleteCount == 1;
                },
                TimeSpan.FromSeconds(60));
            Assert.True(
                completed,
                "The delayed submit response did not complete safely." +
                Environment.NewLine +
                fixture.RecentLogs(120));

            var duplicateCallback = await SendCallbackAsync(
                fixture,
                callback);
            Assert.Equal(200, duplicateCallback.StatusCode);

            ReconciliationResult reconciliation;
            using (var scope = fixture.Host.Services.CreateScope())
            {
                var sender = scope.ServiceProvider
                    .GetRequiredService<MediatR.ISender>();
                reconciliation = await sender.Send(
                    new ReconcileContributionCommand(contributionId));
            }

            Assert.True(reconciliation.Resolved);
            Assert.Equal(
                "Not in reconciliation state, skipping",
                reconciliation.Resolution);
            Assert.Null(reconciliation.Difference);

            await using (var db = CreateDbContext(
                fixture.PgConnectionString))
            {
                var contribution = await db.Contributions
                    .IgnoreQueryFilters()
                    .SingleAsync(x => x.Id == contributionId);
                var attempt = await db.ProcessingAttempts
                    .IgnoreQueryFilters()
                    .SingleAsync(x =>
                        x.ContributionId == contributionId);
                var providerReference = await db.ProviderReferences
                    .IgnoreQueryFilters()
                    .SingleAsync(x =>
                        x.ContributionId == contributionId);
                var transitions = await db.StateTransitions
                    .IgnoreQueryFilters()
                    .Where(x => x.ContributionId == contributionId)
                    .OrderBy(x => x.ChangedAt)
                    .ThenBy(x => x.Id)
                    .ToListAsync();
                var callbackInboxes = await db.InboxMessages
                    .IgnoreQueryFilters()
                    .Where(x =>
                        x.MessageId ==
                            $"callback-{CallbackEventId}")
                    .ToListAsync();
                var processingInboxes = await db.InboxMessages
                    .IgnoreQueryFilters()
                    .Where(x =>
                        x.MessageId == businessMessageId.ToString())
                    .ToListAsync();
                var job = await db.JobRuns
                    .IgnoreQueryFilters()
                    .SingleAsync(x => x.Id == businessMessageId);
                var jobAttempts = await db.JobAttempts
                    .IgnoreQueryFilters()
                    .Where(x => x.JobRunId == businessMessageId)
                    .ToListAsync();

                Assert.Equal(
                    ContributionState.Succeeded,
                    contribution.State);
                Assert.Equal(AttemptStatus.Succeeded, attempt.Status);
                Assert.NotNull(attempt.CompletedAt);
                Assert.Equal(
                    providerReference.Reference,
                    attempt.ProviderReference);
                Assert.Single(callbackInboxes);
                Assert.Single(processingInboxes);
                Assert.Equal(4, transitions.Count);
                Assert.Single(
                    transitions,
                    x => x.ToState == ContributionState.Succeeded);
                Assert.Equal(
                    "CallbackHandler",
                    transitions.Single(x =>
                        x.ToState ==
                            ContributionState.Succeeded).ChangedBy);
                Assert.Equal(JobStatus.Succeeded, job.Status);
                Assert.Single(jobAttempts);
                Assert.Equal(
                    JobAttemptStatus.Succeeded,
                    jobAttempts[0].Status);
                Assert.Equal(
                    0,
                    await db.Leases
                        .IgnoreQueryFilters()
                        .CountAsync(x =>
                            x.JobRunId == businessMessageId &&
                            x.IsActive));
                Assert.Equal(
                    0,
                    await db.ReconciliationRecords
                        .IgnoreQueryFilters()
                        .CountAsync(x =>
                            x.ContributionId == contributionId));
                Assert.Equal(
                    0,
                    await db.DeadLetterRecords
                        .IgnoreQueryFilters()
                        .CountAsync());
            }

            Assert.Equal(1, provider.OperationCount);
            Assert.Equal(1, countingQueue.SendCount);
            Assert.Equal(1, countingQueue.ReceiveCount);
            Assert.Equal(1, countingQueue.DeleteCount);
            Assert.Contains(
                fixture.LogLines,
                line => line.Contains(
                    "state changed during provider call from Processing to Succeeded",
                    StringComparison.Ordinal));
            Assert.Contains(
                fixture.LogLines,
                line => line.Contains(
                    "already Succeeded (likely via callback), skipping state transition",
                    StringComparison.Ordinal));

            var queueUrl = await countingQueue
                .GetOrCreateQueueAsync(fixture.QueueName);
            Assert.Null(await countingQueue.ReceiveAsync(
                queueUrl,
                visibilityTimeoutSeconds: 0));

            output.WriteLine(
                "LATE RESPONSE | Attempt=Succeeded | ProviderReference=1 | " +
                "WorkerReload=true | WorkerSecondTransition=0 | ACK=1");
            output.WriteLine(
                "DUPLICATE + RECONCILIATION | CallbackHTTP={0} | " +
                "CallbackInbox=1 | SucceededTransition=1 | " +
                "Reconciliation=skipped | ReconciliationRecord=0",
                duplicateCallback.StatusCode);
            output.WriteLine(
                "FINAL | State=Succeeded | ProviderOperation=1 | " +
                "QueueSend/Receive/Delete=1/1/1 | Queue=empty | " +
                "DeadLetter=0 | RESULT=PASS | " +
                "StartedAt={0:O} | CompletedAt={1:O}",
                startedAt,
                DateTime.UtcNow);
        }
        finally
        {
            delayedResponse.ReleaseResponse();
            TenantFilterAccessor.Clear();
        }
    }

    private static async Task<CallbackHandleResult> SendCallbackAsync(
        WorkerHostFixture fixture,
        ProviderCallbackPayload payload)
    {
        using var scope = fixture.Host.Services.CreateScope();
        var sender = scope.ServiceProvider
            .GetRequiredService<MediatR.ISender>();
        return await sender.Send(
            new HandleProviderCallbackCommand(payload));
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
        var tenantContext = scope.ServiceProvider
            .GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(
            organizationId,
            userId: null,
            role: null,
            correlationId: "phase3-exp9-callback-before-response");
        TenantFilterAccessor.SetOrganizationId(organizationId);
        try
        {
            var sender = scope.ServiceProvider
                .GetRequiredService<MediatR.ISender>();
            return await sender.Send(new CreateContributionCommand(
                campaignId,
                ExternalReference: "PHASE3-EXP9-001",
                Amount: 225m,
                Currency: "NZD",
                IdempotencyKey: "phase3-exp9-create"));
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
            Name = "Phase 3 Experiment 9 Org",
            Status = OrganizationStatus.Active,
            Version = 0
        });
        db.Campaigns.Add(new Campaign
        {
            Id = campaignId,
            OrganizationId = organizationId,
            Name = "Phase 3 Experiment 9",
            Status = CampaignStatus.Active,
            Version = 0
        });
        await db.SaveChangesAsync();
    }

    private static IConfiguration CreateQueueConfiguration(
        string endpoint,
        int maxReceiveCount)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Queue:Endpoint"] = endpoint,
                    ["Queue:Region"] = "us-west-1",
                    ["Queue:MaxReceiveCount"] =
                        maxReceiveCount.ToString(
                            CultureInfo.InvariantCulture)
                })
            .Build();

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

    private sealed class DelayAfterProviderProcessedFault :
        IWorkerFaultInjector,
        IDisposable
    {
        private readonly TaskCompletionSource _providerProcessed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new(false);
        private int _triggered;

        public void Inject(
            WorkerFaultPoint point,
            string contributionId)
        {
            if (point != WorkerFaultPoint.AfterProviderProcessed ||
                Interlocked.CompareExchange(ref _triggered, 1, 0) != 0)
            {
                return;
            }

            _providerProcessed.TrySetResult();
            if (!_release.Wait(TimeSpan.FromSeconds(90)))
            {
                throw new TimeoutException(
                    "Exp9 delayed response boundary was not released.");
            }
        }

        public Task WaitUntilProviderProcessedAsync(TimeSpan timeout)
            => _providerProcessed.Task.WaitAsync(timeout);

        public void ReleaseResponse()
            => _release.Set();

        public void Dispose()
        {
            ReleaseResponse();
            _release.Dispose();
        }
    }
}
