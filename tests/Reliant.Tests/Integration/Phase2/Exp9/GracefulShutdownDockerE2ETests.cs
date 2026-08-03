using Amazon.SQS;
using Amazon.SQS.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Reliant.Application.Dto;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;
using Reliant.Infrastructure.Queue;
using Reliant.Tests.Integration.Fixtures;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Xunit.Abstractions;

namespace Reliant.Tests.Integration.Phase2.Exp9;

[Trait("Category", "Integration")]
[Trait("Dependency", "DockerCli")]
[Trait("Dependency", "PostgreSQL")]
[Trait("Dependency", "LocalStack")]
public sealed class GracefulShutdownDockerE2ETests(
    ITestOutputHelper output)
{
    private const string WorkerRuntimeImage =
        "mcr.microsoft.com/dotnet/runtime:10.0";

    private sealed record CommandResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

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

    [Fact]
    public async Task Sigterm_ShouldStopNewReceives_ReleaseCurrentWork_AndRecover()
    {
        var startedAt = DateTime.UtcNow;
        var repositoryRoot = FindRepositoryRoot();
        var runId = Guid.NewGuid().ToString("N")[..10];
        var workerAName =
            $"reliant-exp9-worker-a-{runId}";
        var workerBName =
            $"reliant-exp9-worker-b-{runId}";
        var publishDirectory = Path.Combine(
            Path.GetTempPath(),
            $"reliant-phase2-exp9-{runId}");
        const int visibilityTimeoutSeconds = 5;

        await using var fixture = new WorkerHostFixture();
        IContainer? workerA = null;
        IContainer? workerB = null;

        try
        {
            await PublishWorkerAsync(
                repositoryRoot,
                publishDirectory);
            await fixture.InitializeAsync();

            var queueConfiguration =
                CreateQueueConfiguration(
                    fixture.SqsEndpoint);
            var queueAdapter =
                new SqsQueueAdapter(queueConfiguration);
            var queueUrl =
                await queueAdapter.GetOrCreateQueueAsync(
                    fixture.QueueName);
            using var sqs = CreateSqsClient(
                fixture.SqsEndpoint);

            var organizationId = Guid.NewGuid();
            var campaignId = Guid.NewGuid();
            var workItems = await SeedTwoWorkItemsAsync(
                fixture.PgConnectionString,
                organizationId,
                campaignId);

            foreach (var item in workItems)
            {
                await queueAdapter.SendAsync(
                    queueUrl,
                    item.Payload,
                    item.MessageId.ToString(),
                    "ContributionCreated");
            }

            var postgresForContainer =
                GetPostgresConnectionForContainer(
                    fixture.PgConnectionString);
            var queueEndpointForContainer =
                GetQueueEndpointForContainer(
                    fixture.SqsEndpoint);

            workerA = BuildWorkerContainer(
                workerAName,
                publishDirectory,
                postgresForContainer,
                queueEndpointForContainer,
                fixture.QueueName,
                visibilityTimeoutSeconds,
                providerSubmitDelayMs: 60000);
            await workerA.StartAsync();

            var taskStarted = await WaitUntilAsync(
                async () =>
                {
                    await using var db = CreateDbContext(
                        fixture.PgConnectionString);
                    return await db.JobAttempts
                            .IgnoreQueryFilters()
                            .CountAsync(x =>
                                x.Status ==
                                    JobAttemptStatus.Running) == 1 &&
                        await db.Leases
                            .IgnoreQueryFilters()
                            .CountAsync(x => x.IsActive) == 1 &&
                        await db.Contributions
                            .IgnoreQueryFilters()
                            .CountAsync(x =>
                                x.State ==
                                    ContributionState.Processing) == 1 &&
                        await db.ProcessingAttempts
                            .IgnoreQueryFilters()
                            .CountAsync(x =>
                                x.Status ==
                                    AttemptStatus.Pending) == 1;
                },
                TimeSpan.FromSeconds(60));
            Assert.True(
                taskStarted,
                "Worker A did not enter the long-running task." +
                Environment.NewLine +
                await GetContainerLogsAsync(workerA));

            Guid activeJobId;
            Guid activeContributionId;
            Guid untouchedJobId;
            Guid untouchedContributionId;
            await using (var db = CreateDbContext(
                fixture.PgConnectionString))
            {
                var runningAttempt = await db.JobAttempts
                    .IgnoreQueryFilters()
                    .SingleAsync(x =>
                        x.Status ==
                            JobAttemptStatus.Running);
                activeJobId = runningAttempt.JobRunId;
                var activeItem = workItems.Single(
                    x => x.MessageId == activeJobId);
                var untouchedItem = workItems.Single(
                    x => x.MessageId != activeJobId);
                activeContributionId =
                    activeItem.ContributionId;
                untouchedJobId = untouchedItem.MessageId;
                untouchedContributionId =
                    untouchedItem.ContributionId;

                var untouchedJob = await db.JobRuns
                    .IgnoreQueryFilters()
                    .SingleAsync(x => x.Id == untouchedJobId);
                var untouchedContribution =
                    await db.Contributions
                        .IgnoreQueryFilters()
                        .SingleAsync(x =>
                            x.Id ==
                                untouchedContributionId);

                Assert.Equal(
                    JobStatus.Pending,
                    untouchedJob.Status);
                Assert.Equal(
                    ContributionState.Created,
                    untouchedContribution.State);
                Assert.Equal(
                    0,
                    await db.JobAttempts
                        .IgnoreQueryFilters()
                        .CountAsync(x =>
                            x.JobRunId == untouchedJobId));
                Assert.Equal(
                    0,
                    await db.InboxMessages
                        .IgnoreQueryFilters()
                        .CountAsync(x =>
                            x.MessageId ==
                                untouchedJobId.ToString()));
            }

            var signalSentAt = DateTime.UtcNow;
            await RunDockerAsync(
                repositoryRoot,
                TimeSpan.FromSeconds(30),
                ensureSuccess: true,
                "kill",
                "--signal=SIGTERM",
                workerAName);

            var stopped = await WaitForContainerStoppedAsync(
                repositoryRoot,
                workerAName,
                TimeSpan.FromSeconds(30));
            Assert.True(
                stopped,
                "Worker A did not exit after SIGTERM." +
                Environment.NewLine +
                await GetContainerLogsAsync(workerA));
            var workerAExitedAt = DateTime.UtcNow;
            var workerAExitCode = await GetContainerExitCodeAsync(
                repositoryRoot,
                workerAName);
            var workerALogs =
                await GetContainerLogsAsync(workerA);

            var shutdownSettled = await WaitUntilAsync(
                async () =>
                {
                    await using var db = CreateDbContext(
                        fixture.PgConnectionString);
                    var job = await db.JobRuns
                        .IgnoreQueryFilters()
                        .SingleAsync(x => x.Id == activeJobId);
                    var attempt = await db.JobAttempts
                        .IgnoreQueryFilters()
                        .SingleAsync(x =>
                            x.JobRunId == activeJobId);
                    var providerAttempt =
                        await db.ProcessingAttempts
                            .IgnoreQueryFilters()
                            .SingleAsync(x =>
                                x.ContributionId ==
                                    activeContributionId);
                    var activeLeaseCount =
                        await db.Leases
                            .IgnoreQueryFilters()
                            .CountAsync(x =>
                                x.JobRunId == activeJobId &&
                                x.IsActive);

                    return job.Status == JobStatus.Pending &&
                        attempt.Status ==
                            JobAttemptStatus.Abandoned &&
                        attempt.CompletedAt.HasValue &&
                        providerAttempt.Status ==
                            AttemptStatus.Unknown &&
                        providerAttempt.CompletedAt.HasValue &&
                        activeLeaseCount == 0;
                },
                TimeSpan.FromSeconds(8));
            Assert.True(
                shutdownSettled,
                "SIGTERM did not durably release the current task." +
                Environment.NewLine +
                workerALogs);

            await using (var db = CreateDbContext(
                fixture.PgConnectionString))
            {
                var activeContribution =
                    await db.Contributions
                        .IgnoreQueryFilters()
                        .SingleAsync(x =>
                            x.Id == activeContributionId);
                var untouchedContribution =
                    await db.Contributions
                        .IgnoreQueryFilters()
                        .SingleAsync(x =>
                            x.Id ==
                                untouchedContributionId);
                var activeInboxCount =
                    await db.InboxMessages
                        .IgnoreQueryFilters()
                        .CountAsync(x =>
                            x.MessageId ==
                                activeJobId.ToString());
                var untouchedInboxCount =
                    await db.InboxMessages
                        .IgnoreQueryFilters()
                        .CountAsync(x =>
                            x.MessageId ==
                                untouchedJobId.ToString());
                var untouchedJobAttemptCount =
                    await db.JobAttempts
                        .IgnoreQueryFilters()
                        .CountAsync(x =>
                            x.JobRunId == untouchedJobId);
                var checkpointCount =
                    await db.Checkpoints
                        .IgnoreQueryFilters()
                        .CountAsync();

                Assert.Equal(
                    ContributionState.Processing,
                    activeContribution.State);
                Assert.Equal(
                    ContributionState.Created,
                    untouchedContribution.State);
                Assert.Equal(0, activeInboxCount);
                Assert.Equal(0, untouchedInboxCount);
                Assert.Equal(0, untouchedJobAttemptCount);
                Assert.Equal(0, checkpointCount);
            }

            Assert.Contains(
                "Graceful shutdown interrupted message",
                workerALogs,
                StringComparison.Ordinal);
            Assert.Contains(
                "draining 1 in-flight processing task",
                workerALogs,
                StringComparison.Ordinal);

            var twoMessagesRecoverable =
                await WaitUntilAsync(
                    async () =>
                    {
                        var depth = await GetQueueDepthAsync(
                            sqs,
                            queueUrl);
                        return depth.Total == 2;
                    },
                    TimeSpan.FromSeconds(20));
            Assert.True(
                twoMessagesRecoverable,
                "Expected both the unacknowledged current message " +
                "and untouched message to remain recoverable.");
            var depthBeforeRestart =
                await GetQueueDepthAsync(sqs, queueUrl);

            workerB = BuildWorkerContainer(
                workerBName,
                publishDirectory,
                postgresForContainer,
                queueEndpointForContainer,
                fixture.QueueName,
                visibilityTimeoutSeconds,
                providerSubmitDelayMs: 0);
            await workerB.StartAsync();

            var recovered = await WaitUntilAsync(
                async () =>
                {
                    await using var db = CreateDbContext(
                        fixture.PgConnectionString);
                    return await db.Contributions
                            .IgnoreQueryFilters()
                            .CountAsync(x =>
                                (x.Id == activeContributionId ||
                                 x.Id ==
                                    untouchedContributionId) &&
                                x.State ==
                                    ContributionState.Succeeded) == 2 &&
                        await db.InboxMessages
                            .IgnoreQueryFilters()
                            .CountAsync(x =>
                                x.MessageId ==
                                    activeJobId.ToString() ||
                                x.MessageId ==
                                    untouchedJobId.ToString()) == 2 &&
                        await db.JobRuns
                            .IgnoreQueryFilters()
                            .CountAsync(x =>
                                (x.Id == activeJobId ||
                                 x.Id == untouchedJobId) &&
                                x.Status ==
                                    JobStatus.Succeeded) == 2;
                },
                TimeSpan.FromSeconds(60));
            Assert.True(
                recovered,
                "Worker B did not recover both tasks." +
                Environment.NewLine +
                await GetContainerLogsAsync(workerB));

            var queueDrained = await WaitUntilAsync(
                async () =>
                {
                    var depth = await GetQueueDepthAsync(
                        sqs,
                        queueUrl);
                    return depth.Total == 0;
                },
                TimeSpan.FromSeconds(20));
            Assert.True(queueDrained);

            int finalJobAttemptCount;
            int finalProcessingAttemptCount;
            int finalReferenceCount;
            await using (var db = CreateDbContext(
                fixture.PgConnectionString))
            {
                var jobAttempts = await db.JobAttempts
                    .IgnoreQueryFilters()
                    .Where(x =>
                        x.JobRunId == activeJobId ||
                        x.JobRunId == untouchedJobId)
                    .OrderBy(x => x.StartedAt)
                    .ToListAsync();
                var processingAttempts =
                    await db.ProcessingAttempts
                        .IgnoreQueryFilters()
                        .Where(x =>
                            x.ContributionId ==
                                activeContributionId ||
                            x.ContributionId ==
                                untouchedContributionId)
                        .OrderBy(x => x.StartedAt)
                        .ToListAsync();
                finalReferenceCount =
                    await db.ProviderReferences
                        .IgnoreQueryFilters()
                        .CountAsync(x =>
                            x.ContributionId ==
                                activeContributionId ||
                            x.ContributionId ==
                                untouchedContributionId);
                var activeLeaseCount =
                    await db.Leases
                        .IgnoreQueryFilters()
                        .CountAsync(x => x.IsActive);
                var checkpointCount =
                    await db.Checkpoints
                        .IgnoreQueryFilters()
                        .CountAsync();

                finalJobAttemptCount = jobAttempts.Count;
                finalProcessingAttemptCount =
                    processingAttempts.Count;

                Assert.Equal(3, jobAttempts.Count);
                Assert.Equal(
                    1,
                    jobAttempts.Count(x =>
                        x.Status ==
                            JobAttemptStatus.Abandoned));
                Assert.Equal(
                    2,
                    jobAttempts.Count(x =>
                        x.Status ==
                            JobAttemptStatus.Succeeded));
                Assert.Equal(3, processingAttempts.Count);
                Assert.Equal(
                    1,
                    processingAttempts.Count(x =>
                        x.Status ==
                            AttemptStatus.Unknown));
                Assert.Equal(
                    2,
                    processingAttempts.Count(x =>
                        x.Status ==
                            AttemptStatus.Succeeded));
                Assert.Equal(2, finalReferenceCount);
                Assert.Equal(0, activeLeaseCount);
                Assert.Equal(0, checkpointCount);
            }

            output.WriteLine(
                "SIGNAL | Type=SIGTERM | WorkerAExitCode={0} | SignalToExitMs={1}",
                workerAExitCode,
                (long)(workerAExitedAt -
                    signalSentAt).TotalMilliseconds);
            output.WriteLine(
                "SHUTDOWN | NewWorkAccepted=false | ActiveJob=Pending | ActiveAttempt=Abandoned | ProviderAttempt=Unknown | LeaseActive=false | Acked=false | Checkpoints=0");
            output.WriteLine(
                "QUEUE | BeforeRestartVisible={0} | BeforeRestartInFlight={1} | RecoverableMessages={2} | FinalDepth=0",
                depthBeforeRestart.Visible,
                depthBeforeRestart.InFlight,
                depthBeforeRestart.Total);
            output.WriteLine(
                "RECOVERY | Contributions=2:Succeeded | Inbox=2 | JobAttempts={0} | ProcessingAttempts={1} | ProviderReferences={2} | ActiveLeases=0",
                finalJobAttemptCount,
                finalProcessingAttemptCount,
                finalReferenceCount);
            output.WriteLine(
                "RESULT | PASS | SilentLoss=false | StartedAt={0:O} | SignalSentAt={1:O} | CompletedAt={2:O}",
                startedAt,
                signalSentAt,
                DateTime.UtcNow);
        }
        finally
        {
            if (workerB is not null)
            {
                await workerB.DisposeAsync();
            }

            if (workerA is not null)
            {
                await workerA.DisposeAsync();
            }

            if (Directory.Exists(publishDirectory))
            {
                Directory.Delete(
                    publishDirectory,
                    recursive: true);
            }
        }
    }

    private static IContainer BuildWorkerContainer(
        string containerName,
        string publishDirectory,
        string postgresConnectionString,
        string queueEndpoint,
        string queueName,
        int visibilityTimeoutSeconds,
        int providerSubmitDelayMs)
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
            .WithEnvironment("Queue__Endpoint", queueEndpoint)
            .WithEnvironment("Queue__Region", "us-west-1")
            .WithEnvironment("Queue__QueueName", queueName)
            .WithEnvironment("Queue__MaxReceiveCount", "5")
            .WithEnvironment(
                "Worker__VisibilityTimeoutSeconds",
                visibilityTimeoutSeconds.ToString(
                    CultureInfo.InvariantCulture))
            .WithEnvironment("Worker__LeaseSeconds", "30")
            .WithEnvironment(
                "Worker__HeartbeatIntervalMs",
                "500")
            .WithEnvironment(
                "Worker__ProcessingConcurrency",
                "1")
            .WithEnvironment(
                "Worker__Outbox__IntervalMs",
                "1000")
            .WithEnvironment(
                "Worker__Reconciliation__IntervalMs",
                "1000")
            .WithEnvironment(
                "Worker__Maintenance__IntervalMs",
                "1000")
            .WithEnvironment("Provider__Mode", "Success")
            .WithEnvironment(
                "Provider__SubmitDelayMs",
                providerSubmitDelayMs.ToString(
                    CultureInfo.InvariantCulture))
            .WithEnvironment(
                "Provider__Secret",
                "phase2-exp9-secret")
            .WithCommand("dotnet", "Reliant.Worker.dll")
            .Build();

    private static async Task<IReadOnlyList<WorkItem>>
        SeedTwoWorkItemsAsync(
            string connectionString,
            Guid organizationId,
            Guid campaignId)
    {
        var now = DateTime.UtcNow;
        var items = Enumerable.Range(1, 2)
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
                            $"phase2-exp9-{index}"));
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
            Name = "Phase 2 Graceful Shutdown Lab",
            Status = OrganizationStatus.Active,
            Version = 0
        });
        db.Campaigns.Add(new Campaign
        {
            Id = campaignId,
            OrganizationId = organizationId,
            Name = "Experiment 9",
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
                    $"PHASE2-EXP9-{index + 1:000}",
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
                CorrelationId = $"phase2-exp9-{index + 1}",
                OccurredAt = now.AddMilliseconds(index),
                SentAt = now,
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

    private static async Task<CommandResult> RunCommandAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        bool ensureSuccess = true)
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
                $"Unable to start command: {fileName}");
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
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process may have exited during timeout cleanup.
            }

            throw new TimeoutException(
                $"Command timed out after {timeout}: " +
                $"{fileName} {string.Join(' ', arguments)}");
        }

        var result = new CommandResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
        if (ensureSuccess && result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Command failed with exit code " +
                $"{result.ExitCode}: {fileName} " +
                $"{string.Join(' ', arguments)}" +
                Environment.NewLine +
                result.StandardError);
        }

        return result;
    }

    private static Task<CommandResult> RunDockerAsync(
        string repositoryRoot,
        TimeSpan timeout,
        bool ensureSuccess,
        params string[] arguments)
        => RunCommandAsync(
            "docker",
            arguments,
            repositoryRoot,
            timeout,
            ensureSuccess);

    private static async Task PublishWorkerAsync(
        string repositoryRoot,
        string publishDirectory)
    {
        await RunCommandAsync(
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
    }

    private static async Task<string> GetContainerLogsAsync(
        IContainer container)
    {
        var (standardOutput, standardError) =
            await container.GetLogsAsync(
                DateTime.MinValue,
                DateTime.MaxValue,
                timestampsEnabled: false,
                CancellationToken.None);
        return standardOutput + Environment.NewLine +
            standardError;
    }

    private static async Task<bool>
        WaitForContainerStoppedAsync(
            string repositoryRoot,
            string containerName,
            TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var inspect = await RunDockerAsync(
                repositoryRoot,
                TimeSpan.FromSeconds(10),
                ensureSuccess: false,
                "inspect",
                "--format",
                "{{.State.Running}}",
                containerName);
            if (inspect.ExitCode == 0 &&
                string.Equals(
                    inspect.StandardOutput.Trim(),
                    "false",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            await Task.Delay(200);
        }

        return false;
    }

    private static async Task<int> GetContainerExitCodeAsync(
        string repositoryRoot,
        string containerName)
    {
        var inspect = await RunDockerAsync(
            repositoryRoot,
            TimeSpan.FromSeconds(10),
            ensureSuccess: true,
            "inspect",
            "--format",
            "{{.State.ExitCode}}",
            containerName);
        return int.Parse(
            inspect.StandardOutput.Trim(),
            CultureInfo.InvariantCulture);
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

    private static IConfiguration CreateQueueConfiguration(
        string queueEndpoint)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Queue:Endpoint"] = queueEndpoint,
                    ["Queue:Region"] = "us-west-1",
                    ["Queue:MaxReceiveCount"] = "5"
                })
            .Build();

    private static AmazonSQSClient CreateSqsClient(
        string queueEndpoint)
        => new(
            "test",
            "test",
            new AmazonSQSConfig
            {
                ServiceURL = queueEndpoint,
                AuthenticationRegion = "us-west-1"
            });

    private static string
        GetPostgresConnectionForContainer(
            string connectionString)
        => new NpgsqlConnectionStringBuilder(
            connectionString)
        {
            Host = "host.docker.internal",
            GssEncryptionMode =
                GssEncryptionMode.Disable
        }.ConnectionString;

    private static string GetQueueEndpointForContainer(
        string queueEndpoint)
    {
        var uri = new Uri(queueEndpoint);
        return $"{uri.Scheme}://host.docker.internal:" +
            uri.Port;
    }

    private static ReliantDbContext CreateDbContext(
        string connectionString)
        => new(
            new DbContextOptionsBuilder<ReliantDbContext>()
                .UseNpgsql(connectionString)
                .Options);
}
