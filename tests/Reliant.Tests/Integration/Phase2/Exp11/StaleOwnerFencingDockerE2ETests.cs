using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Reliant.Application.Dto;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;
using Reliant.Infrastructure.Persistence.Repositories;
using Reliant.Infrastructure.Provider;
using Reliant.Infrastructure.Queue;
using Reliant.Tests.Integration.Fixtures;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Xunit.Abstractions;

namespace Reliant.Tests.Integration.Phase2.Exp11;

[Trait("Category", "Integration")]
[Trait("Dependency", "DockerCli")]
[Trait("Dependency", "PostgreSQL")]
[Trait("Dependency", "LocalStack")]
[Trait("Dependency", "WorkerHost")]
[Collection("Docker Worker Publish")]
public sealed class StaleOwnerFencingDockerE2ETests(
    ITestOutputHelper output)
{
    private const string WorkerRuntimeImage =
        "mcr.microsoft.com/dotnet/aspnet:10.0";

    private sealed record CommandResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    [Fact]
    public async Task PausedOwner_ShouldBeFencedAfterLeaseTakeover()
    {
        var startedAt = DateTime.UtcNow;
        var repositoryRoot = FindRepositoryRoot();
        var runId = Guid.NewGuid().ToString("N")[..10];
        var workerAName =
            $"reliant-exp11-worker-a-{runId}";
        var publishDirectory = Path.Combine(
            Path.GetTempPath(),
            $"reliant-phase2-exp11-{runId}");
        const int visibilityTimeoutSeconds = 2;
        const int leaseSeconds = 5;
        const int maxReceiveCount = 50;

        await using var fixture = new WorkerHostFixture();
        IContainer? workerA = null;
        var workerAPaused = false;

        try
        {
            await PublishWorkerAsync(
                repositoryRoot,
                publishDirectory);
            await fixture.InitializeAsync();

            var queueAdapter = new SqsQueueAdapter(
                CreateQueueConfiguration(
                    fixture.SqsEndpoint,
                    maxReceiveCount));
            var queueUrl =
                await queueAdapter.GetOrCreateQueueAsync(
                    fixture.QueueName);
            var seeded = await SeedWorkAsync(
                fixture.PgConnectionString);
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
                $"host.docker.internal:" +
                localStackUri.Port;

            workerA = BuildWorkerContainer(
                workerAName,
                publishDirectory,
                postgresForContainer,
                queueEndpointForContainer,
                fixture.QueueName,
                visibilityTimeoutSeconds,
                leaseSeconds,
                maxReceiveCount);
            await workerA.StartAsync();

            Lease? leaseA = null;
            JobAttempt? attemptA = null;
            var workerAInsideProvider =
                await WaitUntilAsync(
                    async () =>
                    {
                        await using var db =
                            CreateDbContext(
                                fixture
                                    .PgConnectionString);
                        leaseA = await db.Leases
                            .IgnoreQueryFilters()
                            .SingleOrDefaultAsync(x =>
                                x.JobRunId ==
                                    seeded.MessageId &&
                                x.IsActive);
                        attemptA =
                            await db.JobAttempts
                                .IgnoreQueryFilters()
                                .SingleOrDefaultAsync(x =>
                                    x.JobRunId ==
                                    seeded.MessageId);
                        var contributionState =
                            await db.Contributions
                                .IgnoreQueryFilters()
                                .Where(x =>
                                    x.Id ==
                                    seeded
                                        .ContributionId)
                                .Select(x => x.State)
                                .SingleAsync();
                        var pendingProviderAttempt =
                            await db
                                .ProcessingAttempts
                                .IgnoreQueryFilters()
                                .SingleOrDefaultAsync(x =>
                                    x.ContributionId ==
                                    seeded
                                        .ContributionId &&
                                    x.Status ==
                                    AttemptStatus.Pending);
                        return
                            leaseA is not null &&
                            leaseA.FencingToken == 1 &&
                            attemptA is not null &&
                            attemptA.FencingToken == 1 &&
                            attemptA.Status ==
                                JobAttemptStatus
                                    .Running &&
                            contributionState ==
                                ContributionState
                                    .Processing &&
                            pendingProviderAttempt is not
                                null;
                    },
                    TimeSpan.FromSeconds(60));
            Assert.True(
                workerAInsideProvider,
                "Worker A did not reach the controlled " +
                "Provider window." +
                Environment.NewLine +
                await GetContainerLogsAsync(workerA));
            Assert.NotNull(leaseA);
            Assert.NotNull(attemptA);

            var pausedAt = DateTime.UtcNow;
            await workerA.PauseAsync();
            workerAPaused = true;

            await fixture.StartWorkersAsync(
                providerMode: "Success",
                includeReconciliation: false,
                visibilityTimeoutSeconds:
                    visibilityTimeoutSeconds,
                maxReceiveCount: maxReceiveCount);

            var leaseExpired = await WaitUntilAsync(
                async () =>
                {
                    await using var db =
                        CreateDbContext(
                            fixture.PgConnectionString);
                    return !await db.Leases
                        .IgnoreQueryFilters()
                        .Where(x =>
                            x.Id == leaseA!.Id)
                        .Select(x => x.IsActive)
                        .SingleAsync();
                },
                TimeSpan.FromSeconds(30));
            Assert.True(
                leaseExpired,
                "Worker B did not release Worker A's " +
                "expired Lease." +
                Environment.NewLine +
                fixture.RecentLogs(100));

            var workerBCompleted =
                await WaitUntilAsync(
                    async () =>
                    {
                        await using var db =
                            CreateDbContext(
                                fixture
                                    .PgConnectionString);
                        var job = await db.JobRuns
                            .IgnoreQueryFilters()
                            .SingleAsync(x =>
                                x.Id ==
                                seeded.MessageId);
                        var contributionState =
                            await db.Contributions
                                .IgnoreQueryFilters()
                                .Where(x =>
                                    x.Id ==
                                    seeded
                                        .ContributionId)
                                .Select(x => x.State)
                                .SingleAsync();
                        return
                            job.Status ==
                                JobStatus.Succeeded &&
                            job.FencingToken == 2 &&
                            contributionState ==
                                ContributionState
                                    .Succeeded &&
                            await db.InboxMessages
                                .IgnoreQueryFilters()
                                .CountAsync(x =>
                                    x.MessageId ==
                                    seeded.MessageId
                                        .ToString()) ==
                                1;
                    },
                    TimeSpan.FromSeconds(60));
            Assert.True(
                workerBCompleted,
                "Worker B did not complete the token-2 " +
                "takeover." +
                Environment.NewLine +
                fixture.RecentLogs(120));

            var staleFence = new JobExecutionFence(
                seeded.MessageId,
                leaseA!.Id,
                leaseA.FencingToken);
            var staleConditionalUpdateRejected =
                await TryFenceAsync(
                    fixture.PgConnectionString,
                    staleFence);
            Assert.False(
                staleConditionalUpdateRejected);

            var resumedAt = DateTime.UtcNow;
            await workerA.UnpauseAsync();
            workerAPaused = false;

            var staleOwnerRejected =
                await WaitForContainerLogAsync(
                    workerA,
                    "Fencing rejected stale worker",
                    TimeSpan.FromSeconds(30));
            Assert.True(
                staleOwnerRejected,
                "Worker A resumed but its stale token was " +
                "not explicitly rejected." +
                Environment.NewLine +
                await GetContainerLogsAsync(workerA));

            await Task.Delay(
                TimeSpan.FromSeconds(2));

            var final = await ReadFinalSnapshotAsync(
                fixture.PgConnectionString,
                seeded.ContributionId,
                seeded.MessageId);
            var providerControl = fixture.Host.Services
                .GetRequiredService<
                    ISandboxProviderControl>();

            Assert.Equal(
                ContributionState.Succeeded,
                final.ContributionState);
            Assert.Equal(JobStatus.Succeeded, final.JobStatus);
            Assert.Equal(2, final.JobFencingToken);
            Assert.Equal(2, final.JobAttemptCount);
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
            Assert.Equal(
                [1L, 2L],
                final.Leases.Select(
                    x => x.FencingToken));
            Assert.All(
                final.Leases,
                lease => Assert.False(
                    lease.IsActive));
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
                "Queue was not empty after Worker B ACK.");

            var workerALogs =
                await GetContainerLogsAsync(workerA);
            Assert.DoesNotContain(
                $"Message {seeded.MessageId} processed",
                workerALogs,
                StringComparison.Ordinal);
            Assert.Contains(
                $"token {leaseA.FencingToken}",
                workerALogs,
                StringComparison.Ordinal);

            output.WriteLine(
                "WORKER A | Lease={0} | Token={1} | " +
                "Attempt=1/Running | ProviderAttempt=Pending | " +
                "PausedAt={2:O}",
                leaseA.Id,
                leaseA.FencingToken,
                pausedAt);
            output.WriteLine(
                "TAKEOVER | LeaseExpired=true | " +
                "WorkerBToken={0} | " +
                "TokenStrictlyIncreased={1} | " +
                "JobStatus=Succeeded",
                final.JobFencingToken,
                final.JobFencingToken >
                    leaseA.FencingToken);
            output.WriteLine(
                "FENCE | WorkerAResumedAt={0:O} | " +
                "StaleToken={1} | ConditionalMatch=false | " +
                "AffectedRows=0 | AckedByStaleOwner=false",
                resumedAt,
                leaseA.FencingToken);
            output.WriteLine(
                "FINAL | Contribution=Succeeded | Inbox=1 | " +
                "JobAttempts=2 | Tokens=1,2 | " +
                "ProviderAttempts=2 | StableProviderKeys=1 | " +
                "ProviderEffects=1 | References=1 | " +
                "DeadLetters=0");
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
                if (workerAPaused)
                {
                    await workerA.UnpauseAsync();
                }

                await workerA.DisposeAsync();
            }

            DeletePublishDirectory(publishDirectory);
        }
    }

    private static IContainer BuildWorkerContainer(
        string containerName,
        string publishDirectory,
        string postgresConnectionString,
        string queueEndpoint,
        string queueName,
        int visibilityTimeoutSeconds,
        int leaseSeconds,
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
                "500")
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
                "TimeoutBeforeProcessing")
            .WithEnvironment(
                "Provider__SubmitDelayMs",
                "15000")
            .WithEnvironment(
                "Provider__Secret",
                "phase2-exp11-secret")
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

    private static async Task<bool> TryFenceAsync(
        string connectionString,
        JobExecutionFence fence)
    {
        await using var db =
            CreateDbContext(connectionString);
        var repo = new LeaseRepository(db);
        await db.Database.BeginTransactionAsync();
        try
        {
            var matched =
                await repo.TryLockCurrentOwnerAsync(
                    fence,
                    DateTime.UtcNow);
            await db.Database.RollbackTransactionAsync();
            return matched;
        }
        catch
        {
            await db.Database.RollbackTransactionAsync();
            throw;
        }
    }

    private static async Task<SeededWork> SeedWorkAsync(
        string connectionString)
    {
        var organizationId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var contributionId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(
            new ContributionProcessingMessage(
                Version: 1,
                ContributionId: contributionId,
                OrganizationId: organizationId,
                Trigger: "Created",
                CorrelationId:
                    "phase2-exp11-stale-owner"));

        await using var db =
            CreateDbContext(connectionString);
        db.Organizations.Add(new Organization
        {
            Id = organizationId,
            Name = "Phase 2 Stale Owner Fencing Lab",
            Status = OrganizationStatus.Active,
            Version = 0
        });
        db.Campaigns.Add(new Campaign
        {
            Id = campaignId,
            OrganizationId = organizationId,
            Name = "Experiment 11",
            Status = CampaignStatus.Active,
            Version = 0
        });
        db.Contributions.Add(new Contribution
        {
            Id = contributionId,
            OrganizationId = organizationId,
            CampaignId = campaignId,
            ExternalReference =
                "PHASE2-EXP11-001",
            Amount = 100m,
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
            CorrelationId =
                "phase2-exp11-stale-owner",
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
            JobAttemptCount: job.AttemptCount,
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

    private static async Task<bool>
        WaitForContainerLogAsync(
            IContainer container,
            string expected,
            TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if ((await GetContainerLogsAsync(container))
                .Contains(
                    expected,
                    StringComparison.Ordinal))
            {
                return true;
            }

            await Task.Delay(250);
        }

        return false;
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
                            CultureInfo
                                .InvariantCulture)
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
                "reliant-phase2-exp11-",
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

    private sealed record FinalSnapshot(
        ContributionState ContributionState,
        JobStatus JobStatus,
        long JobFencingToken,
        int JobAttemptCount,
        List<JobAttempt> JobAttempts,
        List<Lease> Leases,
        List<ProcessingAttempt> ProcessingAttempts,
        int InboxCount,
        int ProviderReferenceCount,
        int StateTransitionCount,
        int DeadLetterCount);
}
