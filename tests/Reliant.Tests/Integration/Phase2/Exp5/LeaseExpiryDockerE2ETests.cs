using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Reliant.Application.Abstractions;
using Reliant.Application.Dto;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;
using Reliant.Infrastructure.Persistence.Repositories;
using Reliant.Infrastructure.Queue;
using Reliant.Tests.Integration.Fixtures;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Xunit.Abstractions;

namespace Reliant.Tests.Integration.Phase2.Exp5;

[Trait("Category", "Integration")]
[Trait("Dependency", "DockerCli")]
[Trait("Dependency", "PostgreSQL")]
[Trait("Dependency", "LocalStack")]
public sealed class LeaseExpiryDockerE2ETests(ITestOutputHelper output)
{
    private const string WorkerRuntimeImage =
        "mcr.microsoft.com/dotnet/runtime:10.0";

    private sealed record CommandResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

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

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException(
                $"Unable to start command: {fileName}");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeoutCts = new CancellationTokenSource(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Process may have exited while timeout cleanup was running.
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
                $"Command failed with exit code {result.ExitCode}: " +
                $"{fileName} {string.Join(' ', arguments)}" +
                $"{Environment.NewLine}STDOUT:{Environment.NewLine}" +
                result.StandardOutput +
                $"{Environment.NewLine}STDERR:{Environment.NewLine}" +
                result.StandardError);
        }

        return result;
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
                if (File.Exists(
                    Path.Combine(directory.FullName, "Reliant.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate Reliant.slnx from the test process.");
    }

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
            .WithEnvironment("Queue__Endpoint", queueEndpoint)
            .WithEnvironment("Queue__Region", "us-west-1")
            .WithEnvironment("Queue__QueueName", queueName)
            .WithEnvironment(
                "Queue__MaxReceiveCount",
                maxReceiveCount.ToString(CultureInfo.InvariantCulture))
            .WithEnvironment(
                "Worker__VisibilityTimeoutSeconds",
                visibilityTimeoutSeconds.ToString(
                    CultureInfo.InvariantCulture))
            .WithEnvironment(
                "Worker__LeaseSeconds",
                leaseSeconds.ToString(CultureInfo.InvariantCulture))
            .WithEnvironment(
                "Worker__HeartbeatIntervalMs",
                "500")
            .WithEnvironment("Worker__Outbox__IntervalMs", "100")
            .WithEnvironment(
                "Worker__Reconciliation__IntervalMs",
                "1000")
            .WithEnvironment(
                "Worker__Maintenance__IntervalMs",
                "100")
            .WithEnvironment("Provider__Mode", "Success")
            .WithEnvironment(
                "Provider__Secret",
                "phase2-exp5-secret")
            .WithCommand("dotnet", "Reliant.Worker.dll")
            .Build();

    private static async Task<string> GetContainerLogsAsync(
        IContainer container)
    {
        var (standardOutput, standardError) =
            await container.GetLogsAsync(
                DateTime.MinValue,
                DateTime.MaxValue,
                timestampsEnabled: false,
                CancellationToken.None);
        return standardOutput + Environment.NewLine + standardError;
    }

    private static async Task<bool> WaitForContainerLogAsync(
        IContainer container,
        string expected,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var logs = await GetContainerLogsAsync(container);
            if (logs.Contains(expected, StringComparison.Ordinal))
            {
                return true;
            }

            await Task.Delay(250);
        }

        return false;
    }

    private static int CountOccurrences(
        string source,
        string value)
    {
        var count = 0;
        var start = 0;
        while ((start = source.IndexOf(
            value,
            start,
            StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += value.Length;
        }

        return count;
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

            await Task.Delay(300);
        }

        return await condition();
    }

    private static ReliantDbContext CreateDbContext(
        string postgresConnectionString)
    {
        var options = new DbContextOptionsBuilder<ReliantDbContext>()
            .UseNpgsql(postgresConnectionString)
            .Options;
        return new ReliantDbContext(options);
    }

    private static IConfiguration CreateQueueConfiguration(
        string queueEndpoint,
        int maxReceiveCount)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Queue:Endpoint"] = queueEndpoint,
                ["Queue:Region"] = "us-west-1",
                ["Queue:MaxReceiveCount"] =
                    maxReceiveCount.ToString(CultureInfo.InvariantCulture)
            })
            .Build();

    [Fact]
    public async Task ConcurrentLeaseAcquisition_ShouldHaveExactlyOneWinner()
    {
        await using var fixture = new PostgreSqlFixture();
        await fixture.InitializeAsync();

        var jobRunId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using (var seedDb = CreateDbContext(
            fixture.ConnectionString))
        {
            seedDb.JobRuns.Add(new JobRun
            {
                Id = jobRunId,
                OrganizationId = Guid.NewGuid(),
                JobDefinitionId =
                    KnownJobDefinitions.ContributionProcessingId,
                QueueUrl =
                    KnownJobDefinitions.ContributionProcessingQueue,
                MessageId = jobRunId.ToString(),
                Payload = "{}",
                Status = JobStatus.Pending,
                CreatedAt = now
            });
            await seedDb.SaveChangesAsync();
        }

        var leaseA = new Lease
        {
            Id = Guid.NewGuid(),
            JobRunId = jobRunId,
            WorkerId = "worker-a",
            AcquiredAt = now,
            ExpiresAt = now.AddSeconds(30)
        };
        var leaseB = new Lease
        {
            Id = Guid.NewGuid(),
            JobRunId = jobRunId,
            WorkerId = "worker-b",
            AcquiredAt = now,
            ExpiresAt = now.AddSeconds(30)
        };

        await using var dbA = CreateDbContext(
            fixture.ConnectionString);
        await using var dbB = CreateDbContext(
            fixture.ConnectionString);
        var repoA = new LeaseRepository(dbA);
        var repoB = new LeaseRepository(dbB);

        var results = await Task.WhenAll(
            repoA.TryAcquireAsync(leaseA),
            repoB.TryAcquireAsync(leaseB));

        Assert.Single(results, acquired => acquired);
        Assert.Single(results, acquired => !acquired);

        await using var verifyDb = CreateDbContext(
            fixture.ConnectionString);
        var activeOwners = await verifyDb.Leases
            .IgnoreQueryFilters()
            .Where(l => l.JobRunId == jobRunId && l.IsActive)
            .Select(l => l.WorkerId)
            .ToListAsync();
        Assert.Single(activeOwners);
        Assert.Contains(
            activeOwners[0],
            new[] { "worker-a", "worker-b" });

        output.WriteLine(
            "ATOMIC ACQUIRE | JobId={0} | Contenders=2 | " +
            "Winners=1 | ActiveOwners=1 | Winner={1}",
            jobRunId,
            activeOwners[0]);
    }

    [Fact]
    public async Task Migration_ShouldBackfillLegacyLeaseAndAttemptJobRun()
    {
        await using var fixture = new PostgreSqlFixture();
        await fixture.InitializeAsync();

        var jobRunId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = CreateDbContext(
            fixture.ConnectionString);
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(
            "20260731220752_AddProviderIdempotencyConstraints");

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO job_attempts (
                "Id",
                "JobRunId",
                "AttemptNumber",
                "StartedAt",
                "CompletedAt",
                "Succeeded",
                "ErrorCategory",
                "ErrorMessage")
            VALUES (
                {attemptId},
                {jobRunId},
                1,
                {now.AddMinutes(-1)},
                {now},
                TRUE,
                NULL,
                NULL);

            INSERT INTO leases (
                "Id",
                "JobRunId",
                "WorkerId",
                "AcquiredAt",
                "ExpiresAt",
                "LastHeartbeatAt",
                "IsActive")
            VALUES (
                {leaseId},
                {jobRunId},
                {"legacy-worker"},
                {now.AddMinutes(-1)},
                {now.AddMinutes(1)},
                {now},
                TRUE);
            """);

        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();

        var jobRun = await db.JobRuns
            .IgnoreQueryFilters()
            .SingleAsync(j => j.Id == jobRunId);
        var attempt = await db.JobAttempts
            .IgnoreQueryFilters()
            .SingleAsync(a => a.Id == attemptId);
        var lease = await db.Leases
            .IgnoreQueryFilters()
            .SingleAsync(l => l.Id == leaseId);

        Assert.Equal(
            KnownJobDefinitions.ContributionProcessingId,
            jobRun.JobDefinitionId);
        Assert.Equal(Guid.Empty, jobRun.OrganizationId);
        Assert.Equal(JobStatus.Succeeded, jobRun.Status);
        Assert.Equal(1, jobRun.AttemptCount);
        Assert.NotNull(jobRun.StartedAt);
        Assert.NotNull(jobRun.CompletedAt);
        Assert.Equal(JobAttemptStatus.Succeeded, attempt.Status);
        Assert.Equal(string.Empty, attempt.WorkerId);
        Assert.Equal(jobRun.Id, attempt.JobRunId);
        Assert.Equal(jobRun.Id, lease.JobRunId);

        output.WriteLine(
            "MIGRATION | LegacyJobId={0} | JobRunBackfilled=true | " +
            "JobStatus=Succeeded | AttemptCount=1 | " +
            "AttemptStatus=Succeeded | LeasePreserved=true",
            jobRunId);
    }

    [Fact]
    public async Task ExpiredLease_ShouldBeReleased_AndSecondWorkerShouldTakeOver()
    {
        var startedAt = DateTime.UtcNow;
        var repositoryRoot = FindRepositoryRoot();
        var runId = Guid.NewGuid().ToString("N")[..10];
        var workerAName = $"reliant-exp5-worker-a-{runId}";
        var workerBName = $"reliant-exp5-worker-b-{runId}";
        var publishDirectory = Path.Combine(
            Path.GetTempPath(),
            $"reliant-phase2-exp5-{runId}");
        const int visibilityTimeoutSeconds = 2;
        const int leaseSeconds = 10;
        const int maxReceiveCount = 20;

        await using var fixture = new WorkerHostFixture();
        IContainer? workerA = null;
        IContainer? workerB = null;
        DbConnection? attemptTableLockConnection = null;
        DbTransaction? attemptTableLockTransaction = null;

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
            var queueUrl = await queueAdapter.GetOrCreateQueueAsync(
                fixture.QueueName);

            var organizationId = Guid.NewGuid();
            var campaignId = Guid.NewGuid();
            var contributionId = Guid.NewGuid();
            var outboxMessageId = Guid.NewGuid();
            var logicalMessageId = outboxMessageId.ToString();
            var correlationId = "phase2-exp5-lease-expiry";
            var payload = JsonSerializer.Serialize(
                new ContributionProcessingMessage(
                    Version: 1,
                    ContributionId: contributionId,
                    OrganizationId: organizationId,
                    Trigger: "Created",
                    CorrelationId: correlationId));

            await using (var db = CreateDbContext(
                fixture.PgConnectionString))
            {
                db.Organizations.Add(new Organization
                {
                    Id = organizationId,
                    Name = "Phase 2 Lease Expiry Lab",
                    Status = OrganizationStatus.Active,
                    Version = 0
                });
                db.Campaigns.Add(new Campaign
                {
                    Id = campaignId,
                    OrganizationId = organizationId,
                    Name = "Experiment 5",
                    Status = CampaignStatus.Active,
                    Version = 0
                });
                db.Contributions.Add(new Contribution
                {
                    Id = contributionId,
                    OrganizationId = organizationId,
                    CampaignId = campaignId,
                    ExternalReference = "PHASE2-EXP5-001",
                    Amount = 100m,
                    Currency = "NZD",
                    State = ContributionState.Created,
                    Version = 0
                });
                var outboxMessage = new OutboxMessage
                {
                    Id = outboxMessageId,
                    OrganizationId = organizationId,
                    MessageType = "ContributionCreated",
                    Payload = payload,
                    CorrelationId = correlationId,
                    OccurredAt = DateTime.UtcNow,
                    SentAt = DateTime.UtcNow,
                    Status = OutboxStatus.Sent,
                    Version = 0
                };
                db.OutboxMessages.Add(outboxMessage);
                db.JobRuns.Add(
                    JobRun.ForContributionProcessing(
                        outboxMessage));
                await db.SaveChangesAsync();
            }

            await queueAdapter.SendAsync(
                queueUrl,
                payload,
                logicalMessageId,
                "ContributionCreated");

            // Worker A may commit the initial Processing state and its Lease,
            // but it cannot create a provider attempt while this table lock is
            // held. That gives the test a deterministic crash window.
            attemptTableLockConnection = new NpgsqlConnection(
                fixture.PgConnectionString);
            await attemptTableLockConnection.OpenAsync();
            attemptTableLockTransaction =
                await attemptTableLockConnection
                    .BeginTransactionAsync();
            await using (var lockCommand =
                attemptTableLockConnection.CreateCommand())
            {
                lockCommand.Transaction =
                    attemptTableLockTransaction;
                lockCommand.CommandText =
                    "LOCK TABLE processing_attempts " +
                    "IN ACCESS EXCLUSIVE MODE";
                await lockCommand.ExecuteNonQueryAsync();
            }

            var postgresForContainer =
                new NpgsqlConnectionStringBuilder(
                    fixture.PgConnectionString)
                {
                    Host = "host.docker.internal",
                    GssEncryptionMode = GssEncryptionMode.Disable
                }.ConnectionString;
            var localStackUri = new Uri(fixture.SqsEndpoint);
            var queueEndpointForContainer =
                $"{localStackUri.Scheme}://host.docker.internal:" +
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

            Lease? workerALease = null;
            var workerAOwnsProcessingJob =
                await WaitUntilAsync(async () =>
                {
                    await using var db = CreateDbContext(
                        fixture.PgConnectionString);
                    var state = await db.Contributions
                        .IgnoreQueryFilters()
                        .Where(c => c.Id == contributionId)
                        .Select(c => c.State)
                        .SingleAsync();
                    workerALease = await db.Leases
                        .IgnoreQueryFilters()
                        .SingleOrDefaultAsync(l =>
                            l.JobRunId == outboxMessageId &&
                            l.IsActive);
                    var jobRun = await db.JobRuns
                        .IgnoreQueryFilters()
                        .SingleAsync(j =>
                            j.Id == outboxMessageId);
                    var runningAttempt = await db.JobAttempts
                        .IgnoreQueryFilters()
                        .SingleOrDefaultAsync(a =>
                            a.JobRunId == outboxMessageId &&
                            a.Status == JobAttemptStatus.Running);
                    return state == ContributionState.Processing &&
                        workerALease is not null &&
                        jobRun.Status == JobStatus.Running &&
                        jobRun.AttemptCount == 1 &&
                        runningAttempt is not null &&
                        runningAttempt.LeaseId == workerALease.Id &&
                        workerALease.WorkerId.StartsWith(
                            workerAName,
                            StringComparison.Ordinal);
                }, TimeSpan.FromSeconds(60));

            Assert.True(
                workerAOwnsProcessingJob,
                "Worker A did not persist Processing + active Lease." +
                Environment.NewLine +
                await GetContainerLogsAsync(workerA));
            Assert.NotNull(workerALease);

            await using (var db = CreateDbContext(
                fixture.PgConnectionString))
            {
                Assert.Equal(
                    1,
                    await db.Leases.IgnoreQueryFilters()
                        .CountAsync(l =>
                            l.JobRunId == outboxMessageId &&
                            l.IsActive));
                Assert.Equal(
                    2,
                    await db.StateTransitions
                        .IgnoreQueryFilters()
                        .CountAsync(t =>
                            t.ContributionId == contributionId));
                var jobRun = await db.JobRuns
                    .IgnoreQueryFilters()
                    .SingleAsync(j => j.Id == outboxMessageId);
                Assert.Equal(JobStatus.Running, jobRun.Status);
                Assert.Equal(1, jobRun.AttemptCount);
                var workerAAttempt = await db.JobAttempts
                    .IgnoreQueryFilters()
                    .SingleAsync(a =>
                        a.JobRunId == outboxMessageId);
                Assert.Equal(
                    JobAttemptStatus.Running,
                    workerAAttempt.Status);
                Assert.Equal(workerALease.Id, workerAAttempt.LeaseId);
                Assert.StartsWith(
                    workerAName,
                    workerAAttempt.WorkerId);
            }

            var killedAt = DateTime.UtcNow;
            await RunCommandAsync(
                "docker",
                ["kill", workerAName],
                repositoryRoot,
                TimeSpan.FromSeconds(30));
            var workerAExitCode =
                await workerA.GetExitCodeAsync(
                    CancellationToken.None);
            Assert.Equal(137, workerAExitCode);

            await attemptTableLockTransaction.RollbackAsync();
            await attemptTableLockTransaction.DisposeAsync();
            attemptTableLockTransaction = null;
            await attemptTableLockConnection.DisposeAsync();
            attemptTableLockConnection = null;

            await using (var db = CreateDbContext(
                fixture.PgConnectionString))
            {
                Assert.Equal(
                    0,
                    await db.ProcessingAttempts
                        .IgnoreQueryFilters()
                        .CountAsync(a =>
                            a.ContributionId == contributionId));
                Assert.Equal(
                    0,
                    await db.ProviderReferences
                        .IgnoreQueryFilters()
                        .CountAsync(r =>
                            r.ContributionId == contributionId));
                Assert.Equal(
                    JobStatus.Running,
                    await db.JobRuns.IgnoreQueryFilters()
                        .Where(j => j.Id == outboxMessageId)
                        .Select(j => j.Status)
                        .SingleAsync());
                Assert.Equal(
                    JobAttemptStatus.Running,
                    await db.JobAttempts.IgnoreQueryFilters()
                        .Where(a =>
                            a.JobRunId == outboxMessageId)
                        .Select(a => a.Status)
                        .SingleAsync());
            }

            workerB = BuildWorkerContainer(
                workerBName,
                publishDirectory,
                postgresForContainer,
                queueEndpointForContainer,
                fixture.QueueName,
                visibilityTimeoutSeconds,
                leaseSeconds,
                maxReceiveCount);
            await workerB.StartAsync();

            // Queue visibility expires before the 10-second Lease. Worker B
            // must see the message but refuse ownership while A is still the
            // active owner.
            var workerBDeferred = await WaitForContainerLogAsync(
                workerB,
                $"lease remains owned by {workerALease!.WorkerId}",
                TimeSpan.FromSeconds(30));
            Assert.True(
                workerBDeferred,
                "Worker B did not defer while Worker A's Lease was active." +
                Environment.NewLine +
                await GetContainerLogsAsync(workerB));

            await using (var db = CreateDbContext(
                fixture.PgConnectionString))
            {
                var activeOwners = await db.Leases
                    .IgnoreQueryFilters()
                    .Where(l =>
                        l.JobRunId == outboxMessageId &&
                        l.IsActive)
                    .Select(l => l.WorkerId)
                    .ToListAsync();
                Assert.Single(activeOwners);
                Assert.Equal(
                    workerALease.WorkerId,
                    activeOwners[0]);
                Assert.DoesNotContain(
                    await db.Leases.IgnoreQueryFilters()
                        .Where(l =>
                            l.JobRunId == outboxMessageId)
                        .Select(l => l.WorkerId)
                        .ToListAsync(),
                    id => id.StartsWith(
                        workerBName,
                        StringComparison.Ordinal));
            }

            // Worker B's ScheduledMaintenance service is the scanner that
            // discovers and releases A's expired Lease.
            var expiredLeaseReleased =
                await WaitForContainerLogAsync(
                    workerB,
                    $"Released expired lease {workerALease.Id} " +
                    $"for worker {workerALease.WorkerId}",
                    TimeSpan.FromSeconds(40));
            Assert.True(
                expiredLeaseReleased,
                "Worker B did not scan and release Worker A's expired Lease." +
                Environment.NewLine +
                await GetContainerLogsAsync(workerB));
            var leaseReleasedAt = DateTime.UtcNow;

            var recovered = await WaitUntilAsync(async () =>
            {
                await using var db = CreateDbContext(
                    fixture.PgConnectionString);
                var activeOwnerCount = await db.Leases
                    .IgnoreQueryFilters()
                    .CountAsync(l =>
                        l.JobRunId == outboxMessageId &&
                        l.IsActive);
                Assert.True(
                    activeOwnerCount <= 1,
                    $"Observed {activeOwnerCount} active owners.");

                var state = await db.Contributions
                    .IgnoreQueryFilters()
                    .Where(c => c.Id == contributionId)
                    .Select(c => c.State)
                    .SingleAsync();
                var inboxCount = await db.InboxMessages
                    .IgnoreQueryFilters()
                    .CountAsync(m =>
                        m.MessageId == logicalMessageId);
                return state == ContributionState.Succeeded &&
                    inboxCount == 1;
            }, TimeSpan.FromSeconds(60));
            Assert.True(
                recovered,
                "Worker B did not complete the expired-Lease takeover." +
                Environment.NewLine +
                await GetContainerLogsAsync(workerB));

            var workerBAcquired = await WaitForContainerLogAsync(
                workerB,
                $"for job {outboxMessageId}",
                TimeSpan.FromSeconds(20));
            var workerBProcessed = await WaitForContainerLogAsync(
                workerB,
                $"Message {logicalMessageId} processed",
                TimeSpan.FromSeconds(20));
            Assert.True(workerBAcquired);
            Assert.True(workerBProcessed);

            int stateTransitionCount;
            int workerBDeferralCount;
            int jobAttemptCount;
            await using (var db = CreateDbContext(
                fixture.PgConnectionString))
            {
                var contributions = await db.Contributions
                    .IgnoreQueryFilters()
                    .Where(c => c.Id == contributionId)
                    .ToListAsync();
                var inboxRows = await db.InboxMessages
                    .IgnoreQueryFilters()
                    .Where(m => m.MessageId == logicalMessageId)
                    .ToListAsync();
                var attempts = await db.ProcessingAttempts
                    .IgnoreQueryFilters()
                    .Where(a =>
                        a.ContributionId == contributionId)
                    .ToListAsync();
                var references = await db.ProviderReferences
                    .IgnoreQueryFilters()
                    .Where(r =>
                        r.ContributionId == contributionId)
                    .ToListAsync();
                var transitions = await db.StateTransitions
                    .IgnoreQueryFilters()
                    .Where(t =>
                        t.ContributionId == contributionId)
                    .ToListAsync();
                var leases = await db.Leases
                    .IgnoreQueryFilters()
                    .Where(l =>
                        l.JobRunId == outboxMessageId)
                    .ToListAsync();
                var jobRun = await db.JobRuns
                    .IgnoreQueryFilters()
                    .SingleAsync(j =>
                        j.Id == outboxMessageId);
                var jobAttempts = await db.JobAttempts
                    .IgnoreQueryFilters()
                    .Where(a =>
                        a.JobRunId == outboxMessageId)
                    .OrderBy(a => a.AttemptNumber)
                    .ToListAsync();
                var deadLetters = await db.DeadLetterRecords
                    .IgnoreQueryFilters()
                    .CountAsync();

                Assert.Single(contributions);
                Assert.Equal(
                    ContributionState.Succeeded,
                    contributions[0].State);
                Assert.Single(inboxRows);
                Assert.Single(attempts);
                Assert.Equal(
                    AttemptStatus.Succeeded,
                    attempts[0].Status);
                Assert.Single(references);
                Assert.Equal(JobStatus.Succeeded, jobRun.Status);
                Assert.Equal(2, jobRun.AttemptCount);
                Assert.NotNull(jobRun.StartedAt);
                Assert.NotNull(jobRun.CompletedAt);
                Assert.Equal(2, jobAttempts.Count);
                Assert.Equal(1, jobAttempts[0].AttemptNumber);
                Assert.Equal(
                    JobAttemptStatus.Abandoned,
                    jobAttempts[0].Status);
                Assert.NotNull(jobAttempts[0].CompletedAt);
                Assert.StartsWith(
                    workerAName,
                    jobAttempts[0].WorkerId);
                Assert.Equal(
                    workerALease.Id,
                    jobAttempts[0].LeaseId);
                Assert.Equal(2, jobAttempts[1].AttemptNumber);
                Assert.Equal(
                    JobAttemptStatus.Succeeded,
                    jobAttempts[1].Status);
                Assert.NotNull(jobAttempts[1].CompletedAt);
                Assert.StartsWith(
                    workerBName,
                    jobAttempts[1].WorkerId);
                Assert.Equal(2, leases.Count);
                Assert.All(leases, lease =>
                    Assert.False(lease.IsActive));
                Assert.Single(
                    leases,
                    lease => lease.WorkerId.StartsWith(
                        workerAName,
                        StringComparison.Ordinal));
                Assert.Single(
                    leases,
                    lease => lease.WorkerId.StartsWith(
                        workerBName,
                        StringComparison.Ordinal));
                Assert.Single(
                    transitions,
                    t =>
                        t.FromState ==
                            ContributionState.Created &&
                        t.ToState ==
                            ContributionState.Accepted);
                Assert.Single(
                    transitions,
                    t =>
                        t.FromState ==
                            ContributionState.Accepted &&
                        t.ToState ==
                            ContributionState.Processing);
                Assert.Single(
                    transitions,
                    t =>
                        t.FromState ==
                            ContributionState.Processing &&
                        t.ToState ==
                            ContributionState.Succeeded);
                Assert.Equal(3, transitions.Count);
                Assert.Equal(0, deadLetters);
                stateTransitionCount = transitions.Count;
                jobAttemptCount = jobAttempts.Count;
            }

            var queueEmpty = await WaitUntilAsync(async () =>
            {
                var message = await queueAdapter.ReceiveAsync(
                    queueUrl,
                    visibilityTimeoutSeconds: 0,
                    CancellationToken.None);
                return message is null;
            }, TimeSpan.FromSeconds(20));
            Assert.True(
                queueEmpty,
                "Queue was not empty after Worker B ACK.");

            var workerBLogs = await GetContainerLogsAsync(workerB);
            workerBDeferralCount = CountOccurrences(
                workerBLogs,
                "lease remains owned by");
            Assert.True(workerBDeferralCount >= 1);

            output.WriteLine(
                "WORKER A | Container={0} | JobId={1} | " +
                "JobStatus=Running | Attempt=1/Running | " +
                "BusinessState=Processing | LeaseId={2} | " +
                "ActiveOwners=1 | dockerKillExitCode={3}",
                workerAName,
                outboxMessageId,
                workerALease.Id,
                workerAExitCode);
            output.WriteLine(
                "LEASE EXPIRY | LeaseSeconds={0} | " +
                "WorkerBDeferrals={1} | ScannerReleased=true | " +
                "ExpiredAt={2:O} | ReleasedObservedAt={3:O}",
                leaseSeconds,
                workerBDeferralCount,
                workerALease.ExpiresAt,
                leaseReleasedAt);
            output.WriteLine(
                "WORKER B | Container={0} | Takeover=true | " +
                "Attempt=2/Succeeded | ProcessedAndAcked=true | " +
                "RecoveryAfterKillMs={1:F0}",
                workerBName,
                (DateTime.UtcNow - killedAt).TotalMilliseconds);
            output.WriteLine(
                "FINAL | JobStatus=Succeeded | JobAttempts={0} | " +
                "Attempt1=Abandoned | Attempt2=Succeeded | " +
                "BusinessState=Succeeded | ActiveOwners=0 | " +
                "LeaseHistory=2 | ProcessingAttempts=1 | " +
                "ProviderReferences=1 | StateTransitions={1} | " +
                "DeadLetters=0",
                jobAttemptCount,
                stateTransitionCount);
            output.WriteLine(
                "RESULT | PASS | StartedAt={0:O} | CompletedAt={1:O}",
                startedAt,
                DateTime.UtcNow);
        }
        finally
        {
            if (attemptTableLockTransaction is not null)
            {
                await attemptTableLockTransaction.RollbackAsync();
                await attemptTableLockTransaction.DisposeAsync();
            }

            if (attemptTableLockConnection is not null)
            {
                await attemptTableLockConnection.DisposeAsync();
            }

            if (workerA is not null)
            {
                await workerA.DisposeAsync();
            }

            if (workerB is not null)
            {
                await workerB.DisposeAsync();
            }

            if (Directory.Exists(publishDirectory))
            {
                Directory.Delete(
                    publishDirectory,
                    recursive: true);
            }
        }
    }
}
