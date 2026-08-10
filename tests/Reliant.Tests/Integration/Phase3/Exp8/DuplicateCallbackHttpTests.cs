using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Reliant.Application.Abstractions;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;
using Reliant.Infrastructure.Provider;
using Reliant.Tests.Integration.Fixtures;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace Reliant.Tests.Integration.Phase3.Exp8;

[Trait("Category", "Integration")]
[Trait("Dependency", "PostgreSQL")]
[Trait("Dependency", "HttpApi")]
public sealed class DuplicateCallbackHttpTests :
    IClassFixture<PostgreSqlFixture>,
    IDisposable
{
    private const string Secret = "phase3-exp8-secret";
    private readonly PostgreSqlFixture _fixture;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly ITestOutputHelper _output;

    public DuplicateCallbackHttpTests(
        PostgreSqlFixture fixture,
        ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting(
                    "ConnectionStrings:PostgreSQL",
                    fixture.ConnectionString);
                builder.UseSetting("Provider:Secret", Secret);
                builder.UseSetting("Provider:Mode", "Success");
            });
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task SameEventIdSequentially_ShouldReturn200Twice_AndApplyOnce()
    {
        var seeded = await SeedProcessingContributionAsync();
        const string eventId = "phase3-exp8-sequential";
        var payload = BuildPayload(eventId, seeded.ProviderReference);
        var providerControl = _factory.Services
            .GetRequiredService<ISandboxProviderControl>();
        var operationsBefore = providerControl.OperationCount;

        using var first = await PostSignedCallbackAsync(payload);
        using var duplicate = await PostSignedCallbackAsync(payload);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        await AssertAppliedExactlyOnceAsync(
            seeded.ContributionId,
            eventId,
            operationsBefore,
            providerControl);

        _output.WriteLine(
            "SEQUENTIAL | EventId={0} | HTTP=200,200 | " +
            "Inbox=1 | SucceededTransitions=1 | " +
            "ProviderOperation={1}",
            eventId,
            providerControl.OperationCount);
    }

    [Fact]
    public async Task SameEventIdConcurrently_ShouldReturn200Twice_AndApplyOnce()
    {
        var seeded = await SeedProcessingContributionAsync();
        const string eventId = "phase3-exp8-concurrent";
        var payload = BuildPayload(eventId, seeded.ProviderReference);
        var providerControl = _factory.Services
            .GetRequiredService<ISandboxProviderControl>();
        var operationsBefore = providerControl.OperationCount;

        var responses = await Task.WhenAll(
            PostSignedCallbackAsync(payload),
            PostSignedCallbackAsync(payload));
        try
        {
            Assert.All(
                responses,
                response => Assert.Equal(
                    HttpStatusCode.OK,
                    response.StatusCode));
            await AssertAppliedExactlyOnceAsync(
                seeded.ContributionId,
                eventId,
                operationsBefore,
                providerControl);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        _output.WriteLine(
            "CONCURRENT | EventId={0} | HTTP=200,200 | " +
            "Inbox=1 | SucceededTransitions=1 | " +
            "ProviderOperation={1}",
            eventId,
            providerControl.OperationCount);
    }

    private async Task AssertAppliedExactlyOnceAsync(
        Guid contributionId,
        string eventId,
        int operationsBefore,
        ISandboxProviderControl providerControl)
    {
        await using var db = CreateDbContext();
        var contribution = await db.Contributions
            .IgnoreQueryFilters()
            .SingleAsync(x => x.Id == contributionId);
        var inboxes = await db.InboxMessages
            .IgnoreQueryFilters()
            .Where(x =>
                x.MessageId == $"callback-{eventId}")
            .ToListAsync();
        var transitions = await db.StateTransitions
            .IgnoreQueryFilters()
            .Where(x =>
                x.ContributionId == contributionId &&
                x.ToState == ContributionState.Succeeded)
            .ToListAsync();

        Assert.Equal(ContributionState.Succeeded, contribution.State);
        Assert.Equal(1, contribution.Version);
        Assert.Single(inboxes);
        Assert.Equal(InboxStatus.Processed, inboxes[0].Status);
        Assert.Single(transitions);
        Assert.Equal(
            ContributionState.Processing,
            transitions[0].FromState);
        Assert.Equal("CallbackHandler", transitions[0].ChangedBy);
        Assert.Equal(operationsBefore, providerControl.OperationCount);
        Assert.Equal(
            0,
            await db.ReconciliationRecords
                .IgnoreQueryFilters()
                .CountAsync(x =>
                    x.ContributionId == contributionId));
        Assert.Equal(
            0,
            await db.OutboxMessages
                .IgnoreQueryFilters()
                .CountAsync(x =>
                    x.OrganizationId == contribution.OrganizationId));
    }

    private async Task<SeededContribution>
        SeedProcessingContributionAsync()
    {
        await _fixture.ResetAsync();
        await using var db = CreateDbContext();
        var organizationId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var contributionId = Guid.NewGuid();
        var providerReference =
            $"ref_exp8_{Guid.NewGuid():N}"[..24];

        db.Organizations.Add(new Organization
        {
            Id = organizationId,
            Name = "Phase 3 Experiment 8 Org",
            Status = OrganizationStatus.Active,
            Version = 0
        });
        db.Campaigns.Add(new Campaign
        {
            Id = campaignId,
            OrganizationId = organizationId,
            Name = "Phase 3 Experiment 8",
            Status = CampaignStatus.Active,
            Version = 0
        });
        db.Contributions.Add(new Contribution
        {
            Id = contributionId,
            OrganizationId = organizationId,
            CampaignId = campaignId,
            ExternalReference = "PHASE3-EXP8-001",
            Amount = 225m,
            Currency = "NZD",
            State = ContributionState.Processing,
            Version = 0
        });
        db.ProviderReferences.Add(new ProviderReference
        {
            Id = Guid.NewGuid(),
            ContributionId = contributionId,
            OrganizationId = organizationId,
            Reference = providerReference,
            ProviderName = "sandbox"
        });
        await db.SaveChangesAsync();

        return new SeededContribution(
            contributionId,
            providerReference);
    }

    private async Task<HttpResponseMessage> PostSignedCallbackAsync(
        string payload)
    {
        var timestamp = DateTime.UtcNow.ToString("O");
        var signature = ComputeSignature(timestamp, payload);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/callbacks/provider")
        {
            Content = new StringContent(
                payload,
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Add("X-Provider-Signature", signature);
        request.Headers.Add("X-Provider-Timestamp", timestamp);
        return await _client.SendAsync(request);
    }

    private static string BuildPayload(
        string eventId,
        string providerReference)
        => JsonSerializer.Serialize(new
        {
            eventId,
            eventType = "contribution.submit",
            providerReference,
            idempotencyKey = (string?)null,
            status = "succeeded",
            occurredAt = DateTime.UtcNow.ToString("O"),
            version = 1
        });

    private static string ComputeSignature(
        string timestamp,
        string payload)
    {
        var data = Encoding.UTF8.GetBytes(timestamp + payload);
        using var hmac =
            new System.Security.Cryptography.HMACSHA256(
                Encoding.UTF8.GetBytes(Secret));
        return Convert.ToHexString(hmac.ComputeHash(data))
            .ToLowerInvariant();
    }

    private ReliantDbContext CreateDbContext()
    {
        var options =
            new DbContextOptionsBuilder<ReliantDbContext>()
                .UseNpgsql(_fixture.ConnectionString)
                .Options;
        return new ReliantDbContext(options);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private sealed record SeededContribution(
        Guid ContributionId,
        string ProviderReference);
}
