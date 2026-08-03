using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Reliant.Application.Dto;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;
using Reliant.Infrastructure.Queue;
using Reliant.Tests.Integration.Fixtures;
using System.Diagnostics;
using System.Text.Json;
using Xunit.Abstractions;

namespace Reliant.Tests.Integration.Phase2.Exp6;

[Trait("Category", "Integration")]
[Trait("Dependency", "PostgreSQL")]
[Trait("Dependency", "LocalStack")]
public sealed class PoisonMessageE2ETests(ITestOutputHelper output)
{
    private const int MaxReceiveCount = 3;
    private const int VisibilityTimeoutSeconds = 2;

    [Fact]
    public async Task PoisonMessages_ShouldEnterNativeDlq_WithoutBlockingNormalMessage()
    {
        var startedAt = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        await using var fixture = new WorkerHostFixture();
        await fixture.InitializeAsync();
        await fixture.StartWorkersAsync(
            providerMode: "Success",
            includeReconciliation: false,
            visibilityTimeoutSeconds: VisibilityTimeoutSeconds,
            maxReceiveCount: MaxReceiveCount);

        var queueConfiguration = CreateQueueConfiguration(
            fixture.SqsEndpoint);
        var queueAdapter = new SqsQueueAdapter(queueConfiguration);
        using var sqs = CreateSqsClient(fixture.SqsEndpoint);
        var queueUrl = await queueAdapter.GetOrCreateQueueAsync(
            fixture.QueueName);

        var redriveAttributes = await sqs.GetQueueAttributesAsync(
            queueUrl,
            [QueueAttributeName.RedrivePolicy]);
        Assert.True(
            redriveAttributes.Attributes.TryGetValue(
                "RedrivePolicy",
                out var redrivePolicy),
            "Processing queue has no SQS RedrivePolicy");

        using var redriveDocument = JsonDocument.Parse(redrivePolicy);
        var redriveRoot = redriveDocument.RootElement;
        Assert.Equal(
            MaxReceiveCount.ToString(),
            redriveRoot.GetProperty("maxReceiveCount").GetString());
        var deadLetterTargetArn = redriveRoot
            .GetProperty("deadLetterTargetArn")
            .GetString();
        Assert.False(string.IsNullOrWhiteSpace(deadLetterTargetArn));

        var deadLetterQueueName = $"{fixture.QueueName}-dlq";
        var deadLetterQueueUrl = (
            await sqs.GetQueueUrlAsync(deadLetterQueueName))
            .QueueUrl;

        output.WriteLine(
            "CONFIG | Queue={0} | DLQ={1} | MaxReceiveCount={2} | VisibilitySeconds={3}",
            fixture.QueueName,
            deadLetterQueueName,
            MaxReceiveCount,
            VisibilityTimeoutSeconds);

        var organizationId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var contributionId = Guid.NewGuid();
        await SeedNormalContributionAsync(
            fixture.PgConnectionString,
            organizationId,
            campaignId,
            contributionId);

        var malformedMessageId = Guid.NewGuid().ToString();
        var unsupportedMessageId = Guid.NewGuid().ToString();
        var normalMessageId = Guid.NewGuid().ToString();
        const string malformedPayload =
            """{"version":1,"contributionId":""";
        var unsupportedPayload = JsonSerializer.Serialize(
            new ContributionProcessingMessage(
                Version: 99,
                ContributionId: Guid.NewGuid(),
                OrganizationId: organizationId,
                Trigger: "Created",
                CorrelationId: "phase2-exp6-unsupported"));
        var normalPayload = JsonSerializer.Serialize(
            new ContributionProcessingMessage(
                Version: 1,
                ContributionId: contributionId,
                OrganizationId: organizationId,
                Trigger: "Created",
                CorrelationId: "phase2-exp6-normal"));

        // Send poison first, then valid work immediately. The normal message
        // must complete while poison messages are repeatedly made visible.
        await queueAdapter.SendAsync(
            queueUrl,
            malformedPayload,
            malformedMessageId,
            "ContributionCreated");
        await queueAdapter.SendAsync(
            queueUrl,
            unsupportedPayload,
            unsupportedMessageId,
            "ContributionCreated");
        var normalSentAt = DateTime.UtcNow;
        await queueAdapter.SendAsync(
            queueUrl,
            normalPayload,
            normalMessageId,
            "ContributionCreated");

        var normalCompleted = await WaitUntilAsync(
            async () =>
            {
                await using var db = CreateDbContext(
                    fixture.PgConnectionString);
                return await db.Contributions
                    .IgnoreQueryFilters()
                    .AnyAsync(x =>
                        x.Id == contributionId &&
                        x.State == ContributionState.Succeeded);
            },
            TimeSpan.FromSeconds(30));
        Assert.True(
            normalCompleted,
            "Normal message was blocked by poison messages." +
            Environment.NewLine +
            fixture.RecentLogs());
        var normalCompletedAt = DateTime.UtcNow;

        var auditCompleted = await WaitUntilAsync(
            async () =>
            {
                await using var db = CreateDbContext(
                    fixture.PgConnectionString);
                return await db.DeadLetterRecords
                    .IgnoreQueryFilters()
                    .CountAsync(x =>
                        x.OriginalMessageId == malformedMessageId ||
                        x.OriginalMessageId == unsupportedMessageId) == 2;
            },
            TimeSpan.FromSeconds(40));
        Assert.True(
            auditCompleted,
            "Poison messages did not create complete DeadLetterRecord rows." +
            Environment.NewLine +
            fixture.RecentLogs());

        var dlqMessages = await ReceiveLogicalMessagesAsync(
            sqs,
            deadLetterQueueUrl,
            [malformedMessageId, unsupportedMessageId],
            TimeSpan.FromSeconds(40));

        Assert.Contains(malformedMessageId, dlqMessages.Keys);
        Assert.Contains(unsupportedMessageId, dlqMessages.Keys);
        Assert.Equal(
            malformedPayload,
            dlqMessages[malformedMessageId].Body);
        Assert.Equal(
            unsupportedPayload,
            dlqMessages[unsupportedMessageId].Body);

        await using var finalDb = CreateDbContext(
            fixture.PgConnectionString);
        var contribution = await finalDb.Contributions
            .IgnoreQueryFilters()
            .SingleAsync(x => x.Id == contributionId);
        var inboxRows = await finalDb.InboxMessages
            .IgnoreQueryFilters()
            .CountAsync(x => x.MessageId == normalMessageId);
        var processingAttempts = await finalDb.ProcessingAttempts
            .IgnoreQueryFilters()
            .CountAsync(x => x.ContributionId == contributionId);
        var providerReferences = await finalDb.ProviderReferences
            .IgnoreQueryFilters()
            .CountAsync(x => x.ContributionId == contributionId);
        var normalJob = await finalDb.JobRuns
            .IgnoreQueryFilters()
            .SingleAsync(x => x.MessageId == normalMessageId);
        var deadLetters = await finalDb.DeadLetterRecords
            .IgnoreQueryFilters()
            .Where(x =>
                x.OriginalMessageId == malformedMessageId ||
                x.OriginalMessageId == unsupportedMessageId)
            .OrderBy(x => x.OriginalMessageId)
            .ToListAsync();

        Assert.Equal(ContributionState.Succeeded, contribution.State);
        Assert.Equal(JobStatus.Succeeded, normalJob.Status);
        Assert.Equal(1, inboxRows);
        Assert.Equal(1, processingAttempts);
        Assert.Equal(1, providerReferences);
        Assert.Equal(2, deadLetters.Count);
        Assert.All(deadLetters, record =>
        {
            Assert.Equal(ErrorCategory.ValidationFailure, record.ErrorCategory);
            Assert.Equal(MaxReceiveCount, record.AttemptCount);
            Assert.Equal(DeadLetterStatus.Pending, record.Status);
            Assert.Equal("ContributionCreated", record.MessageType);
            Assert.False(string.IsNullOrWhiteSpace(record.ErrorMessage));
            Assert.InRange(
                record.DeadLetteredAt,
                startedAt,
                DateTime.UtcNow);
        });

        var malformedRecord = deadLetters.Single(
            x => x.OriginalMessageId == malformedMessageId);
        Assert.Equal(Guid.Empty, malformedRecord.OrganizationId);
        Assert.Equal(malformedPayload, malformedRecord.Payload);
        Assert.Contains(
            "JSON",
            malformedRecord.ErrorMessage!,
            StringComparison.OrdinalIgnoreCase);

        var unsupportedRecord = deadLetters.Single(
            x => x.OriginalMessageId == unsupportedMessageId);
        Assert.Equal(organizationId, unsupportedRecord.OrganizationId);
        Assert.Equal(unsupportedPayload, unsupportedRecord.Payload);
        Assert.Contains(
            "version 99",
            unsupportedRecord.ErrorMessage!,
            StringComparison.OrdinalIgnoreCase);

        var mainQueueDrained = await WaitUntilAsync(
            async () =>
            {
                var attributes = await sqs.GetQueueAttributesAsync(
                    queueUrl,
                    [
                        QueueAttributeName.ApproximateNumberOfMessages,
                        QueueAttributeName
                            .ApproximateNumberOfMessagesNotVisible
                    ]);
                return attributes.ApproximateNumberOfMessages == 0 &&
                    attributes.ApproximateNumberOfMessagesNotVisible == 0;
            },
            TimeSpan.FromSeconds(15));
        Assert.True(mainQueueDrained);

        var poisonLogs = fixture.LogLines
            .Where(line =>
                line.Contains(malformedMessageId, StringComparison.Ordinal) ||
                line.Contains(unsupportedMessageId, StringComparison.Ordinal))
            .ToList();
        Assert.Contains(
            poisonLogs,
            line => line.Contains(
                $"receive {MaxReceiveCount}/{MaxReceiveCount}",
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            poisonLogs,
            line => line.Contains(
                "recorded for SQS DLQ",
                StringComparison.OrdinalIgnoreCase));

        output.WriteLine(
            "NORMAL | MessageId={0} | State=Succeeded | InboxRows={1} | ProcessingAttempts={2} | ProviderReferences={3} | CompletedMs={4}",
            normalMessageId,
            inboxRows,
            processingAttempts,
            providerReferences,
            (long)(normalCompletedAt - normalSentAt).TotalMilliseconds);
        output.WriteLine(
            "POISON | MalformedId={0} | UnsupportedId={1} | Attempts={2} | AuditRows={3}",
            malformedMessageId,
            unsupportedMessageId,
            MaxReceiveCount,
            deadLetters.Count);
        output.WriteLine(
            "DLQ | NativeMessages={0} | MainQueueDrained={1} | PayloadsPreserved=true",
            dlqMessages.Count,
            mainQueueDrained);
        output.WriteLine(
            "FINAL | BusinessResults=1 | PoisonBusinessEffects=0 | DeadLetterRecords={0} | ErrorCategory=ValidationFailure | ErrorsAuditable=true",
            deadLetters.Count);
        output.WriteLine(
            "RESULT | PASS | StartedAt={0:O} | CompletedAt={1:O} | DurationMs={2}",
            startedAt,
            DateTime.UtcNow,
            stopwatch.ElapsedMilliseconds);
    }

    private static IConfiguration CreateQueueConfiguration(
        string endpoint)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Queue:Endpoint"] = endpoint,
                    ["Queue:Region"] = "us-west-1",
                    ["Queue:MaxReceiveCount"] =
                        MaxReceiveCount.ToString()
                })
            .Build();

    private static AmazonSQSClient CreateSqsClient(string endpoint)
        => new(
            "test",
            "test",
            new AmazonSQSConfig
            {
                ServiceURL = endpoint,
                AuthenticationRegion = "us-west-1"
            });

    private static ReliantDbContext CreateDbContext(
        string connectionString)
        => new(
            new DbContextOptionsBuilder<ReliantDbContext>()
                .UseNpgsql(connectionString)
                .Options);

    private static async Task SeedNormalContributionAsync(
        string connectionString,
        Guid organizationId,
        Guid campaignId,
        Guid contributionId)
    {
        await using var db = CreateDbContext(connectionString);
        db.Organizations.Add(new Organization
        {
            Id = organizationId,
            Name = "Phase 2 Poison Message Lab",
            Status = OrganizationStatus.Active,
            Version = 0
        });
        db.Campaigns.Add(new Campaign
        {
            Id = campaignId,
            OrganizationId = organizationId,
            Name = "Experiment 6",
            Status = CampaignStatus.Active,
            Version = 0
        });
        db.Contributions.Add(new Contribution
        {
            Id = contributionId,
            OrganizationId = organizationId,
            CampaignId = campaignId,
            ExternalReference = "PHASE2-EXP6-NORMAL",
            Amount = 100m,
            Currency = "NZD",
            State = ContributionState.Created,
            Version = 0
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Dictionary<string, Message>>
        ReceiveLogicalMessagesAsync(
            AmazonSQSClient sqs,
            string queueUrl,
            IReadOnlyCollection<string> expectedMessageIds,
            TimeSpan timeout)
    {
        var messages = new Dictionary<string, Message>();
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline &&
            messages.Count < expectedMessageIds.Count)
        {
            var response = await sqs.ReceiveMessageAsync(
                new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = 10,
                    VisibilityTimeout = 30,
                    WaitTimeSeconds = 1,
                    MessageAttributeNames = ["All"],
                    MessageSystemAttributeNames =
                    [
                        MessageSystemAttributeName.ApproximateReceiveCount
                    ]
                });

            foreach (var message in response.Messages)
            {
                if (message.MessageAttributes.TryGetValue(
                    "MessageId",
                    out var logicalId) &&
                    expectedMessageIds.Contains(logicalId.StringValue))
                {
                    messages[logicalId.StringValue] = message;
                }
            }
        }

        return messages;
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
