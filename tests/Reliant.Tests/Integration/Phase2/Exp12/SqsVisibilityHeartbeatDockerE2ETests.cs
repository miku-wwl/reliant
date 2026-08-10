using Amazon.SQS;
using Amazon.SQS.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Reliant.Application.Abstractions;
using Reliant.Application.Dto;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;
using Reliant.Infrastructure.Provider;
using Reliant.Infrastructure.Queue;
using Reliant.Tests.Integration.Fixtures;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Xunit.Abstractions;

namespace Reliant.Tests.Integration.Phase2.Exp12;

[CollectionDefinition(
    "Docker Worker Publish",
    DisableParallelization = true)]
public sealed class DockerWorkerPublishCollection
{
}

[Trait("Category", "Integration")]
[Trait("Dependency", "DockerCli")]
[Trait("Dependency", "PostgreSQL")]
[Trait("Dependency", "LocalStack")]
[Trait("Dependency", "WorkerHost")]
[Collection("Docker Worker Publish")]
public sealed class SqsVisibilityHeartbeatDockerE2ETests(
    ITestOutputHelper output)
{
    private const string WorkerRuntimeImage =
        "mcr.microsoft.com/dotnet/runtime:10.0";

    private sealed record CommandResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    [Fact]
    public async Task HealthyLongTask_ShouldRenewLeaseAndVisibility_ThenRedeliverAfterKill()
    {
        var startedAt = DateTime.UtcNow;
        var repositoryRoot = FindRepositoryRoot();
        var runId = Guid.NewGuid().ToString("N")[..10];
        var workerAName =
            $"reliant-exp12-worker-a-{runId}";
        var publishDirectory = Path.Combine(
            Path.GetTempPath(),
            $"reliant-phase2-exp12-{runId}");
        const int visibilityTimeoutSeconds = 5;
        const int leaseSeconds = 4;
        const int heartbeatIntervalMs = 1000;
        const int healthyWindowSeconds = 20;
        const int maxReceiveCount = 10;

        await using var fixture = new WorkerHostFixture();
        IContainer? workerA = null;

        try
        {
            await PublishWorkerAsync(
                repositoryRoot,
                publishDirectory);
            await fixture.InitializeAsync();

            var queueConfiguration =
                CreateQueueConfiguration(
                    fixture.SqsEndpoint,
                    maxReceiveCount);
            var queueAdapter =
                new SqsQueueAdapter(queueConfiguration);
            var queueUrl =
                await queueAdapter.GetOrCreateQueueAsync(
                    fixture.QueueName);
            var seeded = await SeedWorkAsync(
                fixture.PgConnectionString,
                "heartbeat-crash");
            await queueAdapter.SendAsync(
                queueUrl,
                seeded.Payload,
                seeded.MessageId.ToString(),
                "ContributionCreated");

            var postgresForContainer =
                new NpgsqlConnectionStringBuilder(
                    fixture.PgConnectionString)
                {
                    Host = "host.docker.internal",
                    GssEncryptionMode =
                        GssEncryptionMode.Disable
                }.ConnectionString;
            var localStackUri =
                new Uri(fixture.SqsEndpoint);
            var queueEndpointForContainer =
                $"{localStackUri.Scheme}://" +
                "host.docker.internal:" +
                localStackUri.Port;

            workerA = BuildWorkerContainer(
                workerAName,
                publishDirectory,
                postgresForContainer,
                queueEndpointForContainer,
                fixture.QueueName,
                visibilityTimeoutSeconds,
                leaseSeconds,
                heartbeatIntervalMs,
                maxReceiveCount);
            await workerA.StartAsync();

            var workerAStarted = await WaitUntilAsync(
                async () =>
                {
                    await using var db = CreateDbContext(
                        fixture.PgConnectionString);
                    var lease = await db.Leases
                        .IgnoreQueryFilters()
                        .SingleOrDefaultAsync(x =>
                            x.JobRunId == seeded.MessageId &&
                            x.IsActive);
                    var pendingAttempt = await db
                        .ProcessingAttempts
                        .IgnoreQueryFilters()
                        .SingleOrDefaultAsync(x =>
                            x.ContributionId ==
                                seeded.ContributionId &&
                            x.Status == AttemptStatus.Pending);
                    return lease is not null &&
                        lease.FencingToken == 1 &&
                        pendingAttempt is not null;
                },
                TimeSpan.FromSeconds(60));
            Assert.True(
                workerAStarted,
                "Worker A did not enter the controlled long task." +
                Environment.NewLine +
                await GetContainerLogsAsync(workerA));

            await fixture.StartWorkersAsync(
                providerMode: "Success",
                includeReconciliation: false,
                visibilityTimeoutSeconds:
                    visibilityTimeoutSeconds,
                maxReceiveCount: maxReceiveCount,
                leaseSeconds: leaseSeconds,
                heartbeatIntervalMs:
                    heartbeatIntervalMs,
                processingConcurrency: 1);

            var heartbeatSamples =
                new List<HeartbeatSample>();
            var healthyDeadline = DateTime.UtcNow.AddSeconds(
                healthyWindowSeconds);
            while (DateTime.UtcNow < healthyDeadline)
            {
                await using var db = CreateDbContext(
                    fixture.PgConnectionString);
                var lease = await db.Leases
                    .IgnoreQueryFilters()
                    .SingleAsync(x =>
                        x.JobRunId == seeded.MessageId &&
                        x.IsActive);
                heartbeatSamples.Add(new HeartbeatSample(
                    DateTime.UtcNow,
                    lease.ExpiresAt,
                    lease.LastHeartbeatAt));

                Assert.Equal(
                    1,
                    await db.JobAttempts
                        .IgnoreQueryFilters()
                        .CountAsync(x =>
                            x.JobRunId == seeded.MessageId));
                Assert.Equal(
                    1,
                    await db.Leases
                        .IgnoreQueryFilters()
                        .CountAsync(x =>
                            x.JobRunId == seeded.MessageId));
                Assert.Equal(
                    0,
                    await db.InboxMessages
                        .IgnoreQueryFilters()
                        .CountAsync(x =>
                            x.MessageId ==
                                seeded.MessageId.ToString()));

                await Task.Delay(500);
            }

            var distinctLeaseHeartbeats = heartbeatSamples
                .Where(x => x.LastHeartbeatAt.HasValue)
                .Select(x => x.LastHeartbeatAt)
                .Distinct()
                .Count();
            Assert.True(
                distinctLeaseHeartbeats >= 10,
                $"Only {distinctLeaseHeartbeats} database Lease " +
                "heartbeats were observed during the 20-second task.");
            Assert.True(
                heartbeatSamples.Max(x => x.ExpiresAt) -
                    heartbeatSamples.Min(x => x.ExpiresAt) >=
                    TimeSpan.FromSeconds(15),
                "Lease ExpiresAt did not continue moving forward.");

            var workerALogs =
                await GetContainerLogsAsync(workerA);
            Assert.Contains(
                $"Processing message {seeded.MessageId}; " +
                "approximate receive count 1",
                workerALogs,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                $"Processing message {seeded.MessageId}",
                fixture.RecentLogs(500),
                StringComparison.Ordinal);

            using (var sqs = CreateSqsClient(
                fixture.SqsEndpoint))
            {
                var depth = await ReadQueueDepthAsync(
                    sqs,
                    queueUrl);
                Assert.Equal(0, depth.Visible);
                Assert.Equal(1, depth.NotVisible);
            }

            var previousHeartbeat = heartbeatSamples
                .Where(x => x.LastHeartbeatAt.HasValue)
                .Max(x => x.LastHeartbeatAt)!.Value;
            var finalHeartbeatObserved =
                await WaitUntilAsync(
                    async () =>
                    {
                        await using var db = CreateDbContext(
                            fixture.PgConnectionString);
                        var heartbeat = await db.Leases
                            .IgnoreQueryFilters()
                            .Where(x =>
                                x.JobRunId == seeded.MessageId &&
                                x.IsActive)
                            .Select(x => x.LastHeartbeatAt)
                            .SingleAsync();
                        return heartbeat > previousHeartbeat;
                    },
                    TimeSpan.FromSeconds(5));
            Assert.True(
                finalHeartbeatObserved,
                "A final synchronized heartbeat was not observed " +
                "before killing Worker A.");
            await Task.Delay(300);

            var killedAt = DateTime.UtcNow;
            var killResult = await RunCommandAsync(
                "docker",
                ["kill", workerAName],
                repositoryRoot,
                TimeSpan.FromSeconds(30));
            Assert.Equal(
                0,
                killResult.ExitCode);

            var workerBCompleted = await WaitUntilAsync(
                async () =>
                {
                    await using var db = CreateDbContext(
                        fixture.PgConnectionString);
                    var contributionState = await db
                        .Contributions
                        .IgnoreQueryFilters()
                        .Where(x =>
                            x.Id == seeded.ContributionId)
                        .Select(x => x.State)
                        .SingleAsync();
                    var job = await db.JobRuns
                        .IgnoreQueryFilters()
                        .SingleAsync(x =>
                            x.Id == seeded.MessageId);
                    return contributionState ==
                            ContributionState.Succeeded &&
                        job.Status == JobStatus.Succeeded &&
                        job.FencingToken == 2 &&
                        await db.InboxMessages
                            .IgnoreQueryFilters()
                            .CountAsync(x =>
                                x.MessageId ==
                                    seeded.MessageId.ToString()) == 1 &&
                        !await db.Leases
                            .IgnoreQueryFilters()
                            .AnyAsync(x =>
                                x.JobRunId == seeded.MessageId &&
                                x.IsActive);
                },
                TimeSpan.FromSeconds(60));
            var redeliveredAt = DateTime.UtcNow;
            Assert.True(
                workerBCompleted,
                "Worker B did not complete the redelivered " +
                "message after Worker A was killed." +
                Environment.NewLine +
                fixture.RecentLogs(200));
            Assert.True(
                redeliveredAt - killedAt >=
                    TimeSpan.FromSeconds(3),
                "The message became visible before the last " +
                "five-second visibility renewal expired.");
            Assert.Contains(
                $"Processing message {seeded.MessageId}; " +
                "approximate receive count 2",
                fixture.RecentLogs(500),
                StringComparison.Ordinal);

            var final = await ReadFinalSnapshotAsync(
                fixture.PgConnectionString,
                seeded.ContributionId,
                seeded.MessageId);
            var providerControl = fixture.Host.Services
                .GetRequiredService<ISandboxProviderControl>();

            Assert.Equal(
                ContributionState.Succeeded,
                final.ContributionState);
            Assert.Equal(JobStatus.Succeeded, final.JobStatus);
            Assert.Equal(2, final.JobFencingToken);
            Assert.Equal(2, final.JobAttempts.Count);
            Assert.Equal(
                [1L, 2L],
                final.JobAttempts.Select(
                    x => x.FencingToken));
            Assert.Equal(
                JobAttemptStatus.Abandoned,
                final.JobAttempts[0].Status);
            Assert.Equal(
                JobAttemptStatus.Succeeded,
                final.JobAttempts[1].Status);
            Assert.Equal(2, final.Leases.Count);
            Assert.All(
                final.Leases,
                lease => Assert.False(lease.IsActive));
            Assert.Equal(2, final.ProcessingAttempts.Count);
            Assert.Equal(
                AttemptStatus.Pending,
                final.ProcessingAttempts[0].Status);
            Assert.Equal(
                AttemptStatus.Succeeded,
                final.ProcessingAttempts[1].Status);
            Assert.Single(
                final.ProcessingAttempts
                    .Select(x =>
                        x.ProviderIdempotencyKey)
                    .Distinct());
            Assert.Equal(1, final.InboxCount);
            Assert.Equal(1, final.ProviderReferenceCount);
            Assert.Equal(1, providerControl.OperationCount);
            Assert.Equal(3, final.StateTransitionCount);
            Assert.Equal(0, final.DeadLetterCount);

            var queueEmpty = await WaitUntilAsync(
                async () =>
                    await queueAdapter.ReceiveAsync(
                        queueUrl,
                        visibilityTimeoutSeconds: 0,
                        CancellationToken.None) is null,
                TimeSpan.FromSeconds(15));
            Assert.True(
                queueEmpty,
                "Processing queue was not empty after " +
                "Worker B ACK.");

            using (var sqs = CreateSqsClient(
                fixture.SqsEndpoint))
            {
                var dlqUrl = (await sqs.GetQueueUrlAsync(
                    fixture.QueueName + "-dlq")).QueueUrl;
                var dlqDepth = await ReadQueueDepthAsync(
                    sqs,
                    dlqUrl);
                Assert.Equal(0, dlqDepth.Visible);
                Assert.Equal(0, dlqDepth.NotVisible);
            }

            output.WriteLine(
                "HEALTHY | DurationSeconds={0} | " +
                "VisibilitySeconds={1} | HeartbeatMs={2} | " +
                "LeaseHeartbeatSamples={3} | " +
                "ReceiveCount=1 | WorkerBEntered=false",
                healthyWindowSeconds,
                visibilityTimeoutSeconds,
                heartbeatIntervalMs,
                distinctLeaseHeartbeats);
            output.WriteLine(
                "CRASH | WorkerA=docker-kill | KilledAt={0:O} | " +
                "RedeliveredAt={1:O} | ReceiveCount=2",
                killedAt,
                redeliveredAt);
            output.WriteLine(
                "FINAL | Contribution=Succeeded | Inbox=1 | " +
                "JobAttempts=2 | Tokens=1,2 | " +
                "ProviderEffects=1 | DeadLetters=0 | " +
                "Queue=empty | DLQ=empty");
            output.WriteLine(
                "RESULT | PASS | StartedAt={0:O} | " +
                "CompletedAt={1:O}",
                startedAt,
                DateTime.UtcNow);
        }
        finally
        {
            if (workerA is not null)
            {
                await workerA.DisposeAsync();
            }

            DeletePublishDirectory(publishDirectory);
        }
    }

    [Fact]
    public async Task VisibilityFailures_ShouldBeClassifiedLogged_AndStopHeartbeat()
    {
        const int visibilityTimeoutSeconds = 5;
        const int leaseSeconds = 4;
        const int heartbeatIntervalMs = 500;
        const int maxReceiveCount = 10;

        await using var fixture = new WorkerHostFixture();
        await fixture.InitializeAsync();
        var queueConfiguration = CreateQueueConfiguration(
            fixture.SqsEndpoint,
            maxReceiveCount);
        var realAdapter =
            new SqsQueueAdapter(queueConfiguration);
        var queueUrl =
            await realAdapter.GetOrCreateQueueAsync(
                fixture.QueueName);

        var invalidReceipt = await Assert.ThrowsAsync<
            QueueVisibilityRenewalException>(
            () => realAdapter.RenewVisibilityAsync(
                queueUrl,
                "expired-receipt-handle",
                visibilityTimeoutSeconds));
        Assert.Equal(
            QueueVisibilityFailureKind.InvalidReceiptHandle,
            invalidReceipt.FailureKind);
        Assert.False(invalidReceipt.IsTransient);

        var seeded = await SeedWorkAsync(
            fixture.PgConnectionString,
            "heartbeat-rate-limit");
        await realAdapter.SendAsync(
            queueUrl,
            seeded.Payload,
            seeded.MessageId.ToString(),
            "ContributionCreated");
        var failingAdapter =
            new RateLimitedVisibilityQueueAdapter(
                realAdapter);
        await fixture.StartWorkersAsync(
            providerMode: "Success",
            includeReconciliation: false,
            visibilityTimeoutSeconds:
                visibilityTimeoutSeconds,
            queueAdapterOverride: failingAdapter,
            maxReceiveCount: maxReceiveCount,
            leaseSeconds: leaseSeconds,
            heartbeatIntervalMs: heartbeatIntervalMs,
            processingConcurrency: 1,
            providerSubmitDelayMs: 20000);

        var failureLogged = await WaitUntilAsync(
            () => Task.FromResult(
                fixture.LogLines.Any(line =>
                    line.Contains(
                        $"SQS visibility heartbeat failed for message {seeded.MessageId}",
                        StringComparison.Ordinal) &&
                    line.Contains(
                        "failure kind RateLimited; transient True",
                        StringComparison.Ordinal))),
            TimeSpan.FromSeconds(20));
        Assert.True(
            failureLogged,
            "Rate-limited visibility renewal was not " +
            "recorded as a structured warning." +
            Environment.NewLine +
            fixture.RecentLogs(100));
        Assert.Equal(1, failingAdapter.RenewalAttempts);

        await Task.Delay(1500);
        Assert.Equal(
            1,
            failingAdapter.RenewalAttempts);

        await using var db = CreateDbContext(
            fixture.PgConnectionString);
        var lease = await db.Leases
            .IgnoreQueryFilters()
            .SingleAsync(x =>
                x.JobRunId == seeded.MessageId);
        Assert.NotNull(lease.LastHeartbeatAt);

        output.WriteLine(
            "FAILURE | InvalidReceiptHandle={0} | " +
            "Transient={1}",
            invalidReceipt.FailureKind,
            invalidReceipt.IsTransient);
        output.WriteLine(
            "FAILURE | RateLimitLog=true | " +
            "RenewalAttempts={0} | HeartbeatStopped=true",
            failingAdapter.RenewalAttempts);
        output.WriteLine("RESULT | PASS");
    }

    private sealed class RateLimitedVisibilityQueueAdapter(
        IQueueAdapter inner) : IQueueAdapter
    {
        private int _renewalAttempts;

        public int RenewalAttempts => _renewalAttempts;

        public Task<string> GetOrCreateQueueAsync(
            string queueName,
            CancellationToken cancellationToken = default)
            => inner.GetOrCreateQueueAsync(
                queueName,
                cancellationToken);

        public Task<IQueueMessage?> ReceiveAsync(
            string queueUrl,
            int visibilityTimeoutSeconds,
            CancellationToken cancellationToken = default)
            => inner.ReceiveAsync(
                queueUrl,
                visibilityTimeoutSeconds,
                cancellationToken);

        public Task RenewVisibilityAsync(
            string queueUrl,
            string receiptHandle,
            int visibilityTimeoutSeconds,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(
                ref _renewalAttempts);
            throw new QueueVisibilityRenewalException(
                QueueVisibilityFailureKind.RateLimited,
                isTransient: true,
                "Injected SQS throttling for Exp12",
                new IOException("429 Too Many Requests"));
        }

        public Task DeleteAsync(
            string queueUrl,
            string receiptHandle,
            CancellationToken cancellationToken = default)
            => inner.DeleteAsync(
                queueUrl,
                receiptHandle,
                cancellationToken);

        public Task SendAsync(
            string queueUrl,
            string messageBody,
            string messageId,
            string messageType,
            CancellationToken cancellationToken = default)
            => inner.SendAsync(
                queueUrl,
                messageBody,
                messageId,
                messageType,
                cancellationToken);
    }

    private static IContainer BuildWorkerContainer(
        string containerName,
        string publishDirectory,
        string postgresConnectionString,
        string queueEndpoint,
        string queueName,
        int visibilityTimeoutSeconds,
        int leaseSeconds,
        int heartbeatIntervalMs,
        int maxReceiveCount)
        => new ContainerBuilder()
            .WithImage(WorkerRuntimeImage)
            .WithName(containerName)
            .WithHostname(containerName)
            .WithBindMount(
                publishDirectory,
                "/app",
                AccessMode.ReadOnly)
            .WithWorkingDirectory("/app")
            .WithExtraHost(
                "host.docker.internal",
                "host-gateway")
            .WithEnvironment(
                "ConnectionStrings__PostgreSQL",
                postgresConnectionString)
            .WithEnvironment(
                "Queue__Endpoint",
                queueEndpoint)
            .WithEnvironment(
                "Queue__Region",
                "us-west-1")
            .WithEnvironment(
                "Queue__QueueName",
                queueName)
            .WithEnvironment(
                "Queue__MaxReceiveCount",
                maxReceiveCount.ToString(
                    CultureInfo.InvariantCulture))
            .WithEnvironment(
                "Worker__VisibilityTimeoutSeconds",
                visibilityTimeoutSeconds.ToString(
                    CultureInfo.InvariantCulture))
            .WithEnvironment(
                "Worker__LeaseSeconds",
                leaseSeconds.ToString(
                    CultureInfo.InvariantCulture))
            .WithEnvironment(
                "Worker__HeartbeatIntervalMs",
                heartbeatIntervalMs.ToString(
                    CultureInfo.InvariantCulture))
            .WithEnvironment(
                "Worker__ProcessingConcurrency",
                "1")
            .WithEnvironment(
                "Worker__Outbox__IntervalMs",
                "100")
            .WithEnvironment(
                "Worker__Maintenance__IntervalMs",
                "100")
            .WithEnvironment(
                "Provider__Mode",
                "Success")
            .WithEnvironment(
                "Provider__SubmitDelayMs",
                "60000")
            .WithEnvironment(
                "Provider__Secret",
                "phase2-exp12-secret")
            .WithCommand(
                "dotnet",
                "Reliant.Worker.dll")
            .Build();

    private static async Task PublishWorkerAsync(
        string repositoryRoot,
        string publishDirectory)
    {
        var result = await RunCommandAsync(
            "dotnet",
            [
                "publish",
                "src/Reliant.Worker/Reliant.Worker.csproj",
                "--configuration",
                "Release",
                "--no-restore",
                "--disable-build-servers",
                "--output",
                publishDirectory,
                "--nologo",
                "--verbosity",
                "quiet"
            ],
            repositoryRoot,
            TimeSpan.FromMinutes(3));
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Worker publish failed." +
                Environment.NewLine +
                result.StandardError);
        }
    }

    private static async Task<CommandResult>
        RunCommandAsync(
            string fileName,
            IEnumerable<string> arguments,
            string workingDirectory,
            TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process
        {
            StartInfo = startInfo
        };
        if (!process.Start())
        {
            throw new InvalidOperationException(
                $"Unable to start {fileName}.");
        }

        var standardOutput =
            process.StandardOutput.ReadToEndAsync();
        var standardError =
            process.StandardError.ReadToEndAsync();
        using var timeoutCts =
            new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(
                timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(
                    entireProcessTree: true);
            }
            catch
            {
                // Process may have exited during cleanup.
            }

            throw new TimeoutException(
                $"{fileName} timed out after {timeout}.");
        }

        return new CommandResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private static async Task<SeededWork> SeedWorkAsync(
        string connectionString,
        string correlationSuffix)
    {
        var organizationId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var contributionId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var correlationId =
            $"phase2-exp12-{correlationSuffix}";
        var payload = JsonSerializer.Serialize(
            new ContributionProcessingMessage(
                Version: 1,
                ContributionId: contributionId,
                OrganizationId: organizationId,
                Trigger: "Created",
                CorrelationId: correlationId));

        await using var db =
            CreateDbContext(connectionString);
        db.Organizations.Add(new Organization
        {
            Id = organizationId,
            Name = "Phase 2 SQS Visibility Heartbeat Lab",
            Status = OrganizationStatus.Active,
            Version = 0
        });
        db.Campaigns.Add(new Campaign
        {
            Id = campaignId,
            OrganizationId = organizationId,
            Name = "Experiment 12",
            Status = CampaignStatus.Active,
            Version = 0
        });
        db.Contributions.Add(new Contribution
        {
            Id = contributionId,
            OrganizationId = organizationId,
            CampaignId = campaignId,
            ExternalReference =
                $"PHASE2-EXP12-{Guid.NewGuid():N}",
            Amount = 120m,
            Currency = "NZD",
            State = ContributionState.Created,
            Version = 0
        });
        var outbox = new OutboxMessage
        {
            Id = messageId,
            OrganizationId = organizationId,
            MessageType = "ContributionCreated",
            Payload = payload,
            CorrelationId = correlationId,
            OccurredAt = DateTime.UtcNow,
            SentAt = DateTime.UtcNow,
            SendCount = 1,
            Status = OutboxStatus.Sent,
            Version = 0
        };
        db.OutboxMessages.Add(outbox);
        db.JobRuns.Add(
            JobRun.ForContributionProcessing(outbox));
        await db.SaveChangesAsync();

        return new SeededWork(
            contributionId,
            messageId,
            payload);
    }

    private static async Task<FinalSnapshot>
        ReadFinalSnapshotAsync(
            string connectionString,
            Guid contributionId,
            Guid jobRunId)
    {
        await using var db =
            CreateDbContext(connectionString);
        var job = await db.JobRuns
            .IgnoreQueryFilters()
            .SingleAsync(x => x.Id == jobRunId);
        return new FinalSnapshot(
            ContributionState:
                await db.Contributions
                    .IgnoreQueryFilters()
                    .Where(x =>
                        x.Id == contributionId)
                    .Select(x => x.State)
                    .SingleAsync(),
            JobStatus: job.Status,
            JobFencingToken: job.FencingToken,
            JobAttempts:
                await db.JobAttempts
                    .IgnoreQueryFilters()
                    .Where(x =>
                        x.JobRunId == jobRunId)
                    .OrderBy(x => x.AttemptNumber)
                    .ToListAsync(),
            Leases:
                await db.Leases
                    .IgnoreQueryFilters()
                    .Where(x =>
                        x.JobRunId == jobRunId)
                    .OrderBy(x => x.FencingToken)
                    .ToListAsync(),
            ProcessingAttempts:
                await db.ProcessingAttempts
                    .IgnoreQueryFilters()
                    .Where(x =>
                        x.ContributionId ==
                            contributionId)
                    .OrderBy(x => x.AttemptNumber)
                    .ToListAsync(),
            InboxCount:
                await db.InboxMessages
                    .IgnoreQueryFilters()
                    .CountAsync(x =>
                        x.MessageId ==
                            jobRunId.ToString()),
            ProviderReferenceCount:
                await db.ProviderReferences
                    .IgnoreQueryFilters()
                    .CountAsync(x =>
                        x.ContributionId ==
                            contributionId),
            StateTransitionCount:
                await db.StateTransitions
                    .IgnoreQueryFilters()
                    .CountAsync(x =>
                        x.ContributionId ==
                            contributionId),
            DeadLetterCount:
                await db.DeadLetterRecords
                    .IgnoreQueryFilters()
                    .CountAsync());
    }

    private static async Task<QueueDepth> ReadQueueDepthAsync(
        AmazonSQSClient sqs,
        string queueUrl)
    {
        var attributes = await sqs.GetQueueAttributesAsync(
            queueUrl,
            [
                QueueAttributeName
                    .ApproximateNumberOfMessages,
                QueueAttributeName
                    .ApproximateNumberOfMessagesNotVisible
            ]);
        return new QueueDepth(
            attributes.ApproximateNumberOfMessages,
            attributes
                .ApproximateNumberOfMessagesNotVisible);
    }

    private static AmazonSQSClient CreateSqsClient(
        string endpoint)
        => new(
            "test",
            "test",
            new AmazonSQSConfig
            {
                ServiceURL = endpoint,
                AuthenticationRegion = "us-west-1"
            });

    private static async Task<string>
        GetContainerLogsAsync(IContainer container)
    {
        var (standardOutput, standardError) =
            await container.GetLogsAsync(
                DateTime.MinValue,
                DateTime.MaxValue,
                timestampsEnabled: false,
                CancellationToken.None);
        return standardOutput +
            Environment.NewLine +
            standardError;
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

    private static IConfiguration
        CreateQueueConfiguration(
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

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(
                    directory.FullName,
                    "Reliant.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate Reliant.slnx.");
    }

    private static void DeletePublishDirectory(
        string publishDirectory)
    {
        var resolved =
            Path.GetFullPath(publishDirectory);
        var tempRoot = Path.GetFullPath(
            Path.GetTempPath());
        if (!resolved.StartsWith(
            tempRoot,
            StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith(
                "reliant-phase2-exp12-",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to delete unexpected path {resolved}");
        }

        if (Directory.Exists(resolved))
        {
            Directory.Delete(
                resolved,
                recursive: true);
        }
    }

    private sealed record SeededWork(
        Guid ContributionId,
        Guid MessageId,
        string Payload);

    private sealed record HeartbeatSample(
        DateTime ObservedAt,
        DateTime ExpiresAt,
        DateTime? LastHeartbeatAt);

    private sealed record QueueDepth(
        int Visible,
        int NotVisible);

    private sealed record FinalSnapshot(
        ContributionState ContributionState,
        JobStatus JobStatus,
        long JobFencingToken,
        List<JobAttempt> JobAttempts,
        List<Lease> Leases,
        List<ProcessingAttempt> ProcessingAttempts,
        int InboxCount,
        int ProviderReferenceCount,
        int StateTransitionCount,
        int DeadLetterCount);
}
