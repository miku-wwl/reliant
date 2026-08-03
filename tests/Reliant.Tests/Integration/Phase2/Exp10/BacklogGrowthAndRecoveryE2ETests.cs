using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Reliant.Application;
using Reliant.Application.Abstractions;
using Reliant.Application.Dto;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using Reliant.Infrastructure;
using Reliant.Infrastructure.Persistence;
using Reliant.Infrastructure.Provider;
using Reliant.Infrastructure.Queue;
using Reliant.Tests.Integration.Fixtures;
using Reliant.Worker.Handlers;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Xunit.Abstractions;

namespace Reliant.Tests.Integration.Phase2.Exp10;

[Trait("Category", "Integration")]
[Trait("Dependency", "PostgreSQL")]
[Trait("Dependency", "LocalStack")]
[Trait("Dependency", "WorkerHost")]
public sealed class BacklogGrowthAndRecoveryE2ETests(
    ITestOutputHelper output)
{
    private const int MessageCount = 40;
    private const int LowConcurrency = 1;
    private const int RecoveryConcurrency = 8;
    private const int LowProviderDelayMs = 500;
    private const int RecoveryProviderDelayMs = 25;

    private sealed record WorkItem(
        Guid ContributionId,
        Guid MessageId,
        string Payload);

    private sealed record QueueDepth(
        int Visible,
        int InFlight)
    {
        public int Total => Visible + InFlight;
    }

    private sealed record LoadSample(
        int Succeeded,
        int RunningJobs,
        int RunningAttempts,
        int ActiveLeases,
        int DatabaseConnections);

    private sealed record FinalSnapshot(
        int Contributions,
        int SucceededContributions,
        int OutboxMessages,
        int InboxMessages,
        int JobRuns,
        int SucceededJobRuns,
        int JobAttempts,
        int SucceededJobAttempts,
        int ProcessingAttempts,
        int SucceededProcessingAttempts,
        int ProviderReferences,
        int DeadLetters,
        int ActiveLeases,
        int DuplicateProcessingContributions,
        int DuplicateProviderReferences);

    [Fact]
    public async Task ProducerBurst_ShouldGrowObservableBacklog_ThenDrainAfterScaleOut()
    {
        var startedAt = DateTime.UtcNow;
        await using var fixture = new WorkerHostFixture();
        await fixture.InitializeAsync();

        var queueConfiguration = CreateConfiguration(
            fixture,
            LowConcurrency,
            LowProviderDelayMs);
        var queueAdapter = new SqsQueueAdapter(queueConfiguration);
        using var sqs = CreateSqsClient(fixture.SqsEndpoint);
        var queueUrl = await queueAdapter.GetOrCreateQueueAsync(
            fixture.QueueName);
        var items = await SeedWorkAsync(
            fixture.PgConnectionString);

        var publishStopwatch = Stopwatch.StartNew();
        await Task.WhenAll(items.Select(item =>
            queueAdapter.SendAsync(
                queueUrl,
                item.Payload,
                item.MessageId.ToString(),
                "ContributionCreated")));
        publishStopwatch.Stop();

        var peakObserved = await WaitUntilAsync(
            async () =>
                (await GetQueueDepthAsync(sqs, queueUrl)).Total ==
                MessageCount,
            TimeSpan.FromSeconds(15));
        Assert.True(
            peakObserved,
            $"Expected {MessageCount} queued messages before starting workers.");

        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        var oldestAge = await MeasureOldestMessageAgeAsync(
            sqs,
            queueUrl,
            MessageCount);
        Assert.True(
            oldestAge >= TimeSpan.FromSeconds(1),
            $"Oldest message age was only {oldestAge.TotalMilliseconds:F0}ms.");

        var visibilityRestored = await WaitUntilAsync(
            async () =>
                (await GetQueueDepthAsync(sqs, queueUrl)).Total ==
                MessageCount,
            TimeSpan.FromSeconds(10));
        Assert.True(
            visibilityRestored,
            "The LocalStack SentTimestamp sampling pass did not restore all messages.");

        await using var lowWorker = await WorkerInstance.StartAsync(
            CreateConfiguration(
                fixture,
                LowConcurrency,
                LowProviderDelayMs));

        var loadSamples = new List<LoadSample>();
        var throttledDeadline =
            DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < throttledDeadline)
        {
            var sample = await ReadLoadSampleAsync(
                fixture.PgConnectionString);
            loadSamples.Add(sample);
            if (sample.Succeeded >= 4)
            {
                break;
            }

            await Task.Delay(250);
        }

        var throttled = loadSamples[^1];
        Assert.True(
            throttled.Succeeded >= 4,
            "The low-capacity worker did not make measurable progress.");
        Assert.True(
            loadSamples.Max(x => x.RunningJobs) <= LowConcurrency,
            "Running Job count exceeded the configured low concurrency.");
        Assert.True(
            loadSamples.Max(x => x.RunningAttempts) <= LowConcurrency,
            "Running JobAttempt count exceeded the configured low concurrency.");
        Assert.True(
            loadSamples.Max(x => x.ActiveLeases) <= LowConcurrency,
            "Active Lease count exceeded the configured low concurrency.");
        Assert.True(
            loadSamples.Max(x => x.DatabaseConnections) < 30,
            "Database connections grew unexpectedly during throttled processing.");

        var throttledDepth = await GetQueueDepthAsync(
            sqs,
            queueUrl);
        Assert.True(
            throttledDepth.Total >= 25,
            $"Backlog drained too far before scale-out: {throttledDepth.Total}.");

        var recoveryStopwatch = Stopwatch.StartNew();
        await using var recoveryWorker =
            await WorkerInstance.StartAsync(
                CreateConfiguration(
                    fixture,
                    RecoveryConcurrency,
                    RecoveryProviderDelayMs));

        var recovered = await WaitUntilAsync(
            async () =>
            {
                var depth = await GetQueueDepthAsync(
                    sqs,
                    queueUrl);
                var snapshot = await ReadFinalSnapshotAsync(
                    fixture.PgConnectionString);
                return
                    depth.Total == 0 &&
                    snapshot.SucceededContributions ==
                        MessageCount &&
                    snapshot.SucceededJobRuns ==
                        MessageCount &&
                    snapshot.ActiveLeases == 0;
            },
            TimeSpan.FromSeconds(60));
        recoveryStopwatch.Stop();
        Assert.True(
            recovered,
            "Backlog did not drain after capacity was restored.");

        var finalDepth = await GetQueueDepthAsync(
            sqs,
            queueUrl);
        var final = await ReadFinalSnapshotAsync(
            fixture.PgConnectionString);
        var providerEffects =
            lowWorker.Services
                .GetRequiredService<ISandboxProviderControl>()
                .OperationCount +
            recoveryWorker.Services
                .GetRequiredService<ISandboxProviderControl>()
                .OperationCount;
        Assert.Equal(0, finalDepth.Total);
        Assert.Equal(MessageCount, final.Contributions);
        Assert.Equal(MessageCount, final.SucceededContributions);
        Assert.Equal(MessageCount, final.OutboxMessages);
        Assert.Equal(MessageCount, final.InboxMessages);
        Assert.Equal(MessageCount, final.JobRuns);
        Assert.Equal(MessageCount, final.SucceededJobRuns);
        Assert.Equal(MessageCount, final.JobAttempts);
        Assert.Equal(MessageCount, final.SucceededJobAttempts);
        Assert.Equal(MessageCount, final.ProcessingAttempts);
        Assert.Equal(
            MessageCount,
            final.SucceededProcessingAttempts);
        Assert.Equal(MessageCount, final.ProviderReferences);
        Assert.Equal(MessageCount, providerEffects);
        Assert.Equal(0, final.DeadLetters);
        Assert.Equal(0, final.ActiveLeases);
        Assert.Equal(
            0,
            final.DuplicateProcessingContributions);
        Assert.Equal(
            0,
            final.DuplicateProviderReferences);

        var publishRate =
            MessageCount /
            Math.Max(
                0.001,
                publishStopwatch.Elapsed.TotalSeconds);
        output.WriteLine(
            "PRODUCER | Messages={0} | PublishMs={1} | Rate={2:F1}msg/s | LowCapacity≈{3:F1}msg/s",
            MessageCount,
            publishStopwatch.ElapsedMilliseconds,
            publishRate,
            1000d / LowProviderDelayMs);
        output.WriteLine(
            "PEAK | Depth={0} | OldestAgeMs={1:F0}",
            MessageCount,
            oldestAge.TotalMilliseconds);
        output.WriteLine(
            "THROTTLED | Concurrency={0} | ProviderDelayMs={1} | Succeeded={2} | Depth={3} | RunningJobsMax={4} | RunningAttemptsMax={5} | ActiveLeasesMax={6} | DbConnectionsMax={7}",
            LowConcurrency,
            LowProviderDelayMs,
            throttled.Succeeded,
            throttledDepth.Total,
            loadSamples.Max(x => x.RunningJobs),
            loadSamples.Max(x => x.RunningAttempts),
            loadSamples.Max(x => x.ActiveLeases),
            loadSamples.Max(x => x.DatabaseConnections));
        output.WriteLine(
            "SCALE | AddedConcurrency={0} | ProviderDelayMs={1}",
            RecoveryConcurrency,
            RecoveryProviderDelayMs);
        output.WriteLine(
            "RECOVERY | DrainMs={0} | FinalDepth={1}",
            recoveryStopwatch.ElapsedMilliseconds,
            finalDepth.Total);
        output.WriteLine(
            "FINAL | Succeeded={0} | Inbox={1} | JobAttempts={2} | ProcessingAttempts={3} | References={4} | ProviderEffects={5} | DeadLetters={6} | DuplicateGroups={7}",
            final.SucceededContributions,
            final.InboxMessages,
            final.JobAttempts,
            final.ProcessingAttempts,
            final.ProviderReferences,
            providerEffects,
            final.DeadLetters,
            final.DuplicateProcessingContributions +
                final.DuplicateProviderReferences);
        output.WriteLine(
            "RESULT | PASS | StartedAt={0:O} | CompletedAt={1:O}",
            startedAt,
            DateTime.UtcNow);
    }

    private static IConfiguration CreateConfiguration(
        WorkerHostFixture fixture,
        int processingConcurrency,
        int providerSubmitDelayMs)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:PostgreSQL"] =
                        fixture.PgConnectionString,
                    ["Queue:Endpoint"] = fixture.SqsEndpoint,
                    ["Queue:Region"] = "us-west-1",
                    ["Queue:QueueName"] = fixture.QueueName,
                    ["Queue:MaxReceiveCount"] = "100",
                    ["Queue:RequestTimeoutSeconds"] = "5",
                    ["Queue:PublishTimeoutSeconds"] = "5",
                    ["Queue:MaxErrorRetry"] = "1",
                    ["Provider:Mode"] = "Success",
                    ["Provider:Secret"] =
                        "sandbox-secret-key",
                    ["Provider:SubmitDelayMs"] =
                        providerSubmitDelayMs.ToString(
                            CultureInfo.InvariantCulture),
                    ["Worker:ProcessingConcurrency"] =
                        processingConcurrency.ToString(
                            CultureInfo.InvariantCulture),
                    ["Worker:VisibilityTimeoutSeconds"] =
                        "10",
                    ["Worker:LeaseSeconds"] = "30",
                    ["Worker:HeartbeatIntervalMs"] = "500"
                })
            .Build();
    }

    private static AmazonSQSClient CreateSqsClient(
        string endpoint)
    {
        return new AmazonSQSClient(
            "test",
            "test",
            new AmazonSQSConfig
            {
                ServiceURL = endpoint,
                AuthenticationRegion = "us-west-1",
                MaxErrorRetry = 1
            });
    }

    private static async Task<List<WorkItem>> SeedWorkAsync(
        string connectionString)
    {
        var organizationId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var items = Enumerable.Range(1, MessageCount)
            .Select(index =>
            {
                var contributionId = Guid.NewGuid();
                var messageId = Guid.NewGuid();
                var payload = JsonSerializer.Serialize(
                    new ContributionProcessingMessage(
                        Version: 1,
                        ContributionId: contributionId,
                        OrganizationId: organizationId,
                        Trigger: "Created",
                        CorrelationId:
                            $"phase2-exp10-{index:000}"));
                return new WorkItem(
                    contributionId,
                    messageId,
                    payload);
            })
            .ToList();

        await using var db = CreateDbContext(
            connectionString);
        db.Organizations.Add(new Organization
        {
            Id = organizationId,
            Name = "Phase 2 Backlog Recovery Lab",
            Status = OrganizationStatus.Active,
            Version = 0
        });
        db.Campaigns.Add(new Campaign
        {
            Id = campaignId,
            OrganizationId = organizationId,
            Name = "Experiment 10",
            Status = CampaignStatus.Active,
            Version = 0
        });

        foreach (var (item, index) in items.Select(
            (item, index) => (item, index)))
        {
            db.Contributions.Add(new Contribution
            {
                Id = item.ContributionId,
                OrganizationId = organizationId,
                CampaignId = campaignId,
                ExternalReference =
                    $"PHASE2-EXP10-{index + 1:000}",
                Amount = 100m + index,
                Currency = "NZD",
                State = ContributionState.Created,
                Version = 0
            });
            var outbox = new OutboxMessage
            {
                Id = item.MessageId,
                OrganizationId = organizationId,
                MessageType = "ContributionCreated",
                Payload = item.Payload,
                CorrelationId =
                    $"phase2-exp10-{index + 1:000}",
                OccurredAt = now.AddMilliseconds(index),
                SentAt = now,
                SendCount = 1,
                Status = OutboxStatus.Sent,
                Version = 0
            };
            db.OutboxMessages.Add(outbox);
            db.JobRuns.Add(
                JobRun.ForContributionProcessing(outbox));
        }

        await db.SaveChangesAsync();
        return items;
    }

    private static async Task<TimeSpan>
        MeasureOldestMessageAgeAsync(
            AmazonSQSClient sqs,
            string queueUrl,
            int expectedCount)
    {
        var sampledMessages = new List<Message>();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        try
        {
            while (
                sampledMessages.Count < expectedCount &&
                DateTime.UtcNow < deadline)
            {
                var response = await sqs.ReceiveMessageAsync(
                    new ReceiveMessageRequest
                    {
                        QueueUrl = queueUrl,
                        MaxNumberOfMessages = 10,
                        VisibilityTimeout = 30,
                        WaitTimeSeconds = 1,
                        MessageSystemAttributeNames =
                        [
                            MessageSystemAttributeName
                                .SentTimestamp
                        ]
                    });
                sampledMessages.AddRange(response.Messages);
            }

            if (sampledMessages.Count != expectedCount)
            {
                throw new InvalidOperationException(
                    $"Expected to sample {expectedCount} messages, " +
                    $"but received {sampledMessages.Count}.");
            }

            var oldestSentAt = sampledMessages
                .Select(message =>
                {
                    if (!message.Attributes.TryGetValue(
                        MessageSystemAttributeName.SentTimestamp,
                        out var value) ||
                        !long.TryParse(
                            value,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out var milliseconds))
                    {
                        throw new InvalidOperationException(
                            "SQS message did not expose SentTimestamp.");
                    }

                    return DateTimeOffset
                        .FromUnixTimeMilliseconds(milliseconds);
                })
                .Min();
            return DateTimeOffset.UtcNow - oldestSentAt;
        }
        finally
        {
            foreach (var batch in sampledMessages.Chunk(10))
            {
                var response =
                    await sqs.ChangeMessageVisibilityBatchAsync(
                        new ChangeMessageVisibilityBatchRequest
                        {
                            QueueUrl = queueUrl,
                            Entries = batch
                                .Select((message, index) =>
                                    new ChangeMessageVisibilityBatchRequestEntry
                                    {
                                        Id = $"restore-{index}",
                                        ReceiptHandle =
                                            message.ReceiptHandle,
                                        VisibilityTimeout = 0
                                    })
                                .ToList()
                        });
                if (response.Failed.Count > 0)
                {
                    throw new InvalidOperationException(
                        "Failed to restore one or more sampled " +
                        "messages to visible state.");
                }
            }
        }
    }

    private static async Task<QueueDepth>
        GetQueueDepthAsync(
            AmazonSQSClient sqs,
            string queueUrl)
    {
        var response =
            await sqs.GetQueueAttributesAsync(
                queueUrl,
                [
                    QueueAttributeName
                        .ApproximateNumberOfMessages,
                    QueueAttributeName
                        .ApproximateNumberOfMessagesNotVisible
                ]);
        return new QueueDepth(
            response.ApproximateNumberOfMessages,
            response
                .ApproximateNumberOfMessagesNotVisible);
    }

    private static async Task<LoadSample>
        ReadLoadSampleAsync(string connectionString)
    {
        await using var db = CreateDbContext(
            connectionString);
        var succeeded = await db.Contributions
            .IgnoreQueryFilters()
            .CountAsync(x =>
                x.State == ContributionState.Succeeded);
        var runningJobs = await db.JobRuns
            .IgnoreQueryFilters()
            .CountAsync(x => x.Status == JobStatus.Running);
        var runningAttempts = await db.JobAttempts
            .IgnoreQueryFilters()
            .CountAsync(x =>
                x.Status == JobAttemptStatus.Running);
        var activeLeases = await db.Leases
            .IgnoreQueryFilters()
            .CountAsync(x => x.IsActive);

        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*)::int FROM pg_stat_activity " +
            "WHERE datname = current_database()";
        var databaseConnections = Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture);

        return new LoadSample(
            succeeded,
            runningJobs,
            runningAttempts,
            activeLeases,
            databaseConnections);
    }

    private static async Task<FinalSnapshot>
        ReadFinalSnapshotAsync(string connectionString)
    {
        await using var db = CreateDbContext(
            connectionString);
        return new FinalSnapshot(
            Contributions: await db.Contributions
                .IgnoreQueryFilters()
                .CountAsync(),
            SucceededContributions: await db.Contributions
                .IgnoreQueryFilters()
                .CountAsync(x =>
                    x.State ==
                    ContributionState.Succeeded),
            OutboxMessages: await db.OutboxMessages
                .IgnoreQueryFilters()
                .CountAsync(),
            InboxMessages: await db.InboxMessages
                .IgnoreQueryFilters()
                .CountAsync(),
            JobRuns: await db.JobRuns
                .IgnoreQueryFilters()
                .CountAsync(),
            SucceededJobRuns: await db.JobRuns
                .IgnoreQueryFilters()
                .CountAsync(x =>
                    x.Status == JobStatus.Succeeded),
            JobAttempts: await db.JobAttempts
                .IgnoreQueryFilters()
                .CountAsync(),
            SucceededJobAttempts: await db.JobAttempts
                .IgnoreQueryFilters()
                .CountAsync(x =>
                    x.Status ==
                    JobAttemptStatus.Succeeded),
            ProcessingAttempts:
                await db.ProcessingAttempts
                    .IgnoreQueryFilters()
                    .CountAsync(),
            SucceededProcessingAttempts:
                await db.ProcessingAttempts
                    .IgnoreQueryFilters()
                    .CountAsync(x =>
                        x.Status ==
                        AttemptStatus.Succeeded),
            ProviderReferences:
                await db.ProviderReferences
                    .IgnoreQueryFilters()
                    .CountAsync(),
            DeadLetters: await db.DeadLetterRecords
                .IgnoreQueryFilters()
                .CountAsync(),
            ActiveLeases: await db.Leases
                .IgnoreQueryFilters()
                .CountAsync(x => x.IsActive),
            DuplicateProcessingContributions:
                await db.ProcessingAttempts
                    .IgnoreQueryFilters()
                    .GroupBy(x => x.ContributionId)
                    .CountAsync(group => group.Count() > 1),
            DuplicateProviderReferences:
                await db.ProviderReferences
                    .IgnoreQueryFilters()
                    .GroupBy(x => x.ContributionId)
                    .CountAsync(group => group.Count() > 1));
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

    private sealed class WorkerInstance(
        IHost host) : IAsyncDisposable
    {
        public IServiceProvider Services => host.Services;

        public static async Task<WorkerInstance> StartAsync(
            IConfiguration configuration)
        {
            var builder = Host.CreateApplicationBuilder(
                new HostApplicationBuilderSettings
                {
                    Args = Array.Empty<string>()
                });
            builder.Configuration.AddConfiguration(
                configuration);
            builder.Services.AddReliantApplication();
            builder.Services.AddReliantInfrastructure(
                builder.Configuration);
            builder.Logging.ClearProviders();
            builder.Services.AddHostedService<
                ProcessingHandlerService>();

            var workerHost = builder.Build();
            await workerHost.StartAsync();
            return new WorkerInstance(workerHost);
        }

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync(
                TimeSpan.FromSeconds(10));
            host.Dispose();
        }
    }
}
