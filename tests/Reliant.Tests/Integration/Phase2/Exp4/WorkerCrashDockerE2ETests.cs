using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Reliant.Application.Abstractions;
using Reliant.Application.Dto;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;
using Reliant.Infrastructure.Queue;
using Reliant.Tests.Integration.Fixtures;
using Npgsql;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Xunit.Abstractions;

namespace Reliant.Tests.Integration.Phase2.Exp4;

[Trait("Category", "Integration")]
[Trait("Dependency", "DockerCli")]
[Trait("Dependency", "PostgreSQL")]
[Trait("Dependency", "LocalStack")]
public sealed class WorkerCrashDockerE2ETests(ITestOutputHelper output)
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
                // The command may have exited between timeout and cleanup.
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

    private static async Task EnsureRuntimeImageAsync(string repositoryRoot)
    {
        var inspect = await RunDockerAsync(
            repositoryRoot,
            TimeSpan.FromSeconds(30),
            ensureSuccess: false,
            "image",
            "inspect",
            WorkerRuntimeImage);

        if (inspect.ExitCode == 0)
        {
            return;
        }

        await RunDockerAsync(
            repositoryRoot,
            TimeSpan.FromMinutes(5),
            ensureSuccess: true,
            "pull",
            WorkerRuntimeImage);
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
                "Debug",
                "--output",
                publishDirectory,
                "--nologo",
                "--verbosity",
                "quiet"
            ],
            repositoryRoot,
            TimeSpan.FromMinutes(3));
    }

    private static async Task StartWorkerContainerAsync(
        string repositoryRoot,
        string containerName,
        string publishDirectory,
        string postgresConnectionString,
        string queueEndpoint,
        string queueName,
        int visibilityTimeoutSeconds)
    {
        var runResult = await RunDockerAsync(
            repositoryRoot,
            TimeSpan.FromMinutes(2),
            ensureSuccess: true,
            "run",
            "--detach",
            "--name",
            containerName,
            "--hostname",
            containerName,
            "--label",
            "reliant.lab=phase2-exp4",
            "--add-host",
            "host.docker.internal:host-gateway",
            "--mount",
            $"type=bind,source={publishDirectory},target=/app,readonly",
            "--workdir",
            "/app",
            "--env",
            $"ConnectionStrings__PostgreSQL={postgresConnectionString}",
            "--env",
            $"Queue__Endpoint={queueEndpoint}",
            "--env",
            "Queue__Region=us-west-1",
            "--env",
            $"Queue__QueueName={queueName}",
            "--env",
            $"Worker__VisibilityTimeoutSeconds={visibilityTimeoutSeconds}",
            "--env",
            "Worker__Outbox__IntervalMs=100",
            "--env",
            "Worker__Reconciliation__IntervalMs=1000",
            "--env",
            "Worker__Maintenance__IntervalMs=1000",
            "--env",
            "Provider__Mode=Success",
            "--env",
            "Provider__Secret=phase2-exp4-secret",
            WorkerRuntimeImage,
            "dotnet",
            "Reliant.Worker.dll");

        if (string.IsNullOrWhiteSpace(runResult.StandardOutput))
        {
            throw new InvalidOperationException(
                $"docker run returned no container id for {containerName}");
        }
    }

    private static async Task<string> GetContainerLogsAsync(
        string repositoryRoot,
        string containerName)
    {
        var result = await RunDockerAsync(
            repositoryRoot,
            TimeSpan.FromSeconds(30),
            ensureSuccess: false,
            "logs",
            containerName);

        return result.StandardOutput + Environment.NewLine +
            result.StandardError;
    }

    private static async Task<bool> WaitForContainerLogAsync(
        string repositoryRoot,
        string containerName,
        string expected,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var logs = await GetContainerLogsAsync(
                repositoryRoot,
                containerName);
            if (logs.Contains(expected, StringComparison.Ordinal))
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
        string queueEndpoint)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Queue:Endpoint"] = queueEndpoint,
                ["Queue:Region"] = "us-west-1"
            })
            .Build();

    private static async Task<IQueueMessage?> WaitForRedeliveryAsync(
        IQueueAdapter queueAdapter,
        string queueUrl,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var message = await queueAdapter.ReceiveAsync(
                queueUrl,
                visibilityTimeoutSeconds: 0,
                CancellationToken.None);
            if (message is not null)
            {
                return message;
            }
        }

        return null;
    }

    private static async Task RemoveContainerAsync(
        string repositoryRoot,
        string? containerName)
    {
        if (string.IsNullOrWhiteSpace(containerName))
        {
            return;
        }

        await RunDockerAsync(
            repositoryRoot,
            TimeSpan.FromSeconds(30),
            ensureSuccess: false,
            "rm",
            "--force",
            containerName);
    }

    [Fact]
    public async Task DockerKilledWorker_ShouldRedeliverToSecondWorker_WithoutDuplicateEffect()
    {
        var startedAt = DateTime.UtcNow;
        var repositoryRoot = FindRepositoryRoot();
        var runId = Guid.NewGuid().ToString("N")[..10];
        var workerA = $"reliant-exp4-worker-a-{runId}";
        var workerB = $"reliant-exp4-worker-b-{runId}";
        var publishDirectory = Path.Combine(
            Path.GetTempPath(),
            $"reliant-phase2-exp4-{runId}");
        const int visibilityTimeoutSeconds = 5;

        await using var fixture = new WorkerHostFixture();
        DbConnection? lockConnection = null;
        DbTransaction? lockTransaction = null;

        try
        {
            await EnsureRuntimeImageAsync(repositoryRoot);
            await PublishWorkerAsync(repositoryRoot, publishDirectory);
            await fixture.InitializeAsync();

            var queueAdapter = new SqsQueueAdapter(
                CreateQueueConfiguration(fixture.SqsEndpoint));
            var queueUrl = await queueAdapter.GetOrCreateQueueAsync(
                fixture.QueueName);

            var organizationId = Guid.NewGuid();
            var campaignId = Guid.NewGuid();
            var contributionId = Guid.NewGuid();
            var outboxMessageId = Guid.NewGuid();
            var logicalMessageId = outboxMessageId.ToString();
            var correlationId = "phase2-exp4-worker-crash";
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
                    Name = "Phase 2 Worker Crash Lab",
                    Status = OrganizationStatus.Active,
                    Version = 0
                });
                db.Campaigns.Add(new Campaign
                {
                    Id = campaignId,
                    OrganizationId = organizationId,
                    Name = "Experiment 4",
                    Status = CampaignStatus.Active,
                    Version = 0
                });
                db.Contributions.Add(new Contribution
                {
                    Id = contributionId,
                    OrganizationId = organizationId,
                    CampaignId = campaignId,
                    ExternalReference = "PHASE2-EXP4-001",
                    Amount = 100m,
                    Currency = "NZD",
                    State = ContributionState.Created,
                    Version = 0
                });
                db.OutboxMessages.Add(new OutboxMessage
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
                });
                await db.SaveChangesAsync();
            }

            await queueAdapter.SendAsync(
                queueUrl,
                payload,
                logicalMessageId,
                "ContributionCreated");

            // Hold the Contribution row so Worker A can receive the SQS message
            // but cannot commit Created -> Processing before docker kill.
            lockConnection = new NpgsqlConnection(
                fixture.PgConnectionString);
            await lockConnection.OpenAsync();
            lockTransaction = await lockConnection.BeginTransactionAsync();
            await using (var lockCommand = lockConnection.CreateCommand())
            {
                lockCommand.Transaction = lockTransaction;
                lockCommand.CommandText =
                    "SELECT 1 FROM contributions " +
                    "WHERE \"Id\" = @contributionId FOR UPDATE";
                var parameter = lockCommand.CreateParameter();
                parameter.ParameterName = "contributionId";
                parameter.Value = contributionId;
                lockCommand.Parameters.Add(parameter);
                var locked = await lockCommand.ExecuteScalarAsync();
                Assert.Equal(1, Convert.ToInt32(
                    locked,
                    CultureInfo.InvariantCulture));
            }

            var postgresForContainer = new NpgsqlConnectionStringBuilder(
                fixture.PgConnectionString)
            {
                Host = "host.docker.internal"
            }.ConnectionString;
            var localStackUri = new Uri(fixture.SqsEndpoint);
            var queueEndpointForContainer =
                $"{localStackUri.Scheme}://host.docker.internal:" +
                localStackUri.Port;

            await StartWorkerContainerAsync(
                repositoryRoot,
                workerA,
                publishDirectory,
                postgresForContainer,
                queueEndpointForContainer,
                fixture.QueueName,
                visibilityTimeoutSeconds);

            var workerAReceived = await WaitForContainerLogAsync(
                repositoryRoot,
                workerA,
                $"Processing message {logicalMessageId}",
                TimeSpan.FromSeconds(60));
            Assert.True(
                workerAReceived,
                "Worker A did not receive the message." +
                Environment.NewLine +
                await GetContainerLogsAsync(repositoryRoot, workerA));

            await using (var db = CreateDbContext(
                fixture.PgConnectionString))
            {
                var contribution = await db.Contributions
                    .IgnoreQueryFilters()
                    .SingleAsync(c => c.Id == contributionId);
                Assert.Equal(ContributionState.Created, contribution.State);
                Assert.Equal(
                    0,
                    await db.ProcessingAttempts.IgnoreQueryFilters()
                        .CountAsync(a =>
                            a.ContributionId == contributionId));
                Assert.Equal(
                    0,
                    await db.ProviderReferences.IgnoreQueryFilters()
                        .CountAsync(r =>
                            r.ContributionId == contributionId));
                Assert.Equal(
                    0,
                    await db.InboxMessages.IgnoreQueryFilters()
                        .CountAsync(m =>
                            m.MessageId == logicalMessageId));
            }

            var killedAt = DateTime.UtcNow;
            await RunDockerAsync(
                repositoryRoot,
                TimeSpan.FromSeconds(30),
                ensureSuccess: true,
                "kill",
                workerA);

            var inspectWorkerA = await RunDockerAsync(
                repositoryRoot,
                TimeSpan.FromSeconds(30),
                ensureSuccess: true,
                "inspect",
                "--format",
                "{{.State.ExitCode}}",
                workerA);
            var workerAExitCode = int.Parse(
                inspectWorkerA.StandardOutput.Trim(),
                CultureInfo.InvariantCulture);
            Assert.Equal(137, workerAExitCode);

            await lockTransaction.RollbackAsync();
            await lockTransaction.DisposeAsync();
            lockTransaction = null;
            await lockConnection.DisposeAsync();
            lockConnection = null;

            var redeliveredMessage = await WaitForRedeliveryAsync(
                queueAdapter,
                queueUrl,
                TimeSpan.FromSeconds(40));
            Assert.NotNull(redeliveredMessage);
            Assert.Equal(
                logicalMessageId,
                redeliveredMessage!.MessageId);
            Assert.True(
                redeliveredMessage.ApproximateReceiveCount >= 2,
                "Expected SQS ApproximateReceiveCount >= 2 after " +
                $"visibility timeout, got " +
                $"{redeliveredMessage.ApproximateReceiveCount}");

            await StartWorkerContainerAsync(
                repositoryRoot,
                workerB,
                publishDirectory,
                postgresForContainer,
                queueEndpointForContainer,
                fixture.QueueName,
                visibilityTimeoutSeconds);

            var workerBReceived = await WaitForContainerLogAsync(
                repositoryRoot,
                workerB,
                $"Processing message {logicalMessageId}",
                TimeSpan.FromSeconds(60));
            Assert.True(
                workerBReceived,
                "Worker B did not receive the redelivered message." +
                Environment.NewLine +
                await GetContainerLogsAsync(repositoryRoot, workerB));

            var recovered = await WaitUntilAsync(async () =>
            {
                await using var db = CreateDbContext(
                    fixture.PgConnectionString);
                var contributionState = await db.Contributions
                    .IgnoreQueryFilters()
                    .Where(c => c.Id == contributionId)
                    .Select(c => c.State)
                    .SingleAsync();
                var inboxCount = await db.InboxMessages
                    .IgnoreQueryFilters()
                    .CountAsync(m => m.MessageId == logicalMessageId);

                return contributionState == ContributionState.Succeeded &&
                    inboxCount == 1;
            }, TimeSpan.FromSeconds(60));
            Assert.True(
                recovered,
                "Worker B did not complete the recovered task." +
                Environment.NewLine +
                await GetContainerLogsAsync(repositoryRoot, workerB));

            var workerBProcessed = await WaitForContainerLogAsync(
                repositoryRoot,
                workerB,
                $"Message {logicalMessageId} processed",
                TimeSpan.FromSeconds(30));
            Assert.True(
                workerBProcessed,
                "Worker B committed the result but did not log ACK completion." +
                Environment.NewLine +
                await GetContainerLogsAsync(repositoryRoot, workerB));

            int stateTransitionCount;
            int staleLeaseCount;
            await using (var db = CreateDbContext(
                fixture.PgConnectionString))
            {
                var contributions = await db.Contributions
                    .IgnoreQueryFilters()
                    .Where(c => c.Id == contributionId)
                    .ToListAsync();
                var outboxRows = await db.OutboxMessages
                    .IgnoreQueryFilters()
                    .Where(m => m.Id == outboxMessageId)
                    .ToListAsync();
                var inboxRows = await db.InboxMessages
                    .IgnoreQueryFilters()
                    .Where(m => m.MessageId == logicalMessageId)
                    .ToListAsync();
                var attempts = await db.ProcessingAttempts
                    .IgnoreQueryFilters()
                    .Where(a => a.ContributionId == contributionId)
                    .ToListAsync();
                var references = await db.ProviderReferences
                    .IgnoreQueryFilters()
                    .Where(r => r.ContributionId == contributionId)
                    .ToListAsync();
                var transitions = await db.StateTransitions
                    .IgnoreQueryFilters()
                    .Where(t => t.ContributionId == contributionId)
                    .ToListAsync();
                var deadLetters = await db.DeadLetterRecords
                    .IgnoreQueryFilters()
                    .CountAsync();
                staleLeaseCount = await db.Leases
                    .IgnoreQueryFilters()
                    .CountAsync(l =>
                        l.WorkerId.StartsWith(workerA));

                Assert.Single(contributions);
                Assert.Equal(
                    ContributionState.Succeeded,
                    contributions[0].State);
                Assert.Single(outboxRows);
                Assert.Single(inboxRows);
                Assert.Single(attempts);
                Assert.Equal(
                    AttemptStatus.Succeeded,
                    attempts[0].Status);
                Assert.Single(references);
                Assert.Single(
                    transitions,
                    t => t.FromState == ContributionState.Created &&
                        t.ToState == ContributionState.Accepted);
                Assert.Single(
                    transitions,
                    t => t.FromState == ContributionState.Accepted &&
                        t.ToState == ContributionState.Processing);
                Assert.Single(
                    transitions,
                    t => t.FromState == ContributionState.Processing &&
                        t.ToState == ContributionState.Succeeded);
                Assert.Equal(3, transitions.Count);
                Assert.Equal(0, deadLetters);
                stateTransitionCount = transitions.Count;
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

            output.WriteLine(
                "WORKER A | Container={0} | MessageId={1} | " +
                "Received=true | dockerKillExitCode={2}",
                workerA,
                logicalMessageId,
                workerAExitCode);
            output.WriteLine(
                "REDELIVERY | VisibilityTimeoutSeconds={0} | " +
                "ApproximateReceiveCount={1} | " +
                "ElapsedAfterKillMs={2:F0}",
                visibilityTimeoutSeconds,
                redeliveredMessage.ApproximateReceiveCount,
                (DateTime.UtcNow - killedAt).TotalMilliseconds);
            output.WriteLine(
                "WORKER B | Container={0} | ReceivedRedelivery=true | " +
                "ProcessedAndAcked=true",
                workerB);
            output.WriteLine(
                "FINAL | Contributions=1 | BusinessState=Succeeded | " +
                "InboxRows=1 | ProcessingAttempts=1 | " +
                "ProviderReferences=1 | StateTransitions={0} | " +
                "DeadLetters=0 | StaleWorkerALeases={1}",
                stateTransitionCount,
                staleLeaseCount);
            output.WriteLine(
                "RESULT | PASS | StartedAt={0:O} | CompletedAt={1:O}",
                startedAt,
                DateTime.UtcNow);
        }
        finally
        {
            if (lockTransaction is not null)
            {
                await lockTransaction.RollbackAsync();
                await lockTransaction.DisposeAsync();
            }

            if (lockConnection is not null)
            {
                await lockConnection.DisposeAsync();
            }

            await RemoveContainerAsync(repositoryRoot, workerA);
            await RemoveContainerAsync(repositoryRoot, workerB);

            if (Directory.Exists(publishDirectory))
            {
                Directory.Delete(publishDirectory, recursive: true);
            }
        }
    }
}
