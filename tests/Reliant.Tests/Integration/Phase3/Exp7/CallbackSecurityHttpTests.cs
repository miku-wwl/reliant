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

namespace Reliant.Tests.Integration.Phase3.Exp7;

[Trait("Category", "Integration")]
[Trait("Dependency", "PostgreSQL")]
[Trait("Dependency", "HttpApi")]
public class CallbackSecurityHttpTests : IClassFixture<PostgreSqlFixture>, IDisposable
{
    private const string Secret = "test-secret-key";
    private readonly PostgreSqlFixture _fixture;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly ITestOutputHelper _output;

    public CallbackSecurityHttpTests(
        PostgreSqlFixture fixture,
        ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:PostgreSQL", fixture.ConnectionString);
                builder.UseSetting("Provider:Secret", Secret);
                builder.UseSetting("Provider:Mode", "Success");
            });
    }

    private static string ComputeSignature(string timestamp, string payload)
    {
        var data = Encoding.UTF8.GetBytes(timestamp + payload);
        using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        return Convert.ToHexString(hmac.ComputeHash(data)).ToLowerInvariant();
    }

    private async Task<HttpResponseMessage> PostCallbackAsync(string? signature, string? timestamp, string payload)
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/callbacks/provider")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        if (signature is not null) request.Headers.Add("X-Provider-Signature", signature);
        if (timestamp is not null) request.Headers.Add("X-Provider-Timestamp", timestamp);
        return await client.SendAsync(request);
    }

    private ReliantDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ReliantDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;
        return new ReliantDbContext(options);
    }

    private async Task<(Guid orgId, Guid contributionId, string reference)> SeedContributionWithReferenceAsync()
    {
        await using var db = CreateDbContext();
        var orgId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var contributionId = Guid.NewGuid();
        var reference = "ref_http_" + Guid.NewGuid().ToString("N")[..10];

        db.Set<Organization>().Add(new Organization
        {
            Id = orgId,
            Name = "HTTP Org",
            Status = OrganizationStatus.Active,
            Version = 0
        });
        db.Set<Campaign>().Add(new Campaign
        {
            Id = campaignId,
            OrganizationId = orgId,
            Name = "HTTP",
            Status = CampaignStatus.Active,
            Version = 0
        });
        db.Set<Contribution>().Add(new Contribution
        {
            Id = contributionId,
            OrganizationId = orgId,
            CampaignId = campaignId,
            ExternalReference = "HTTP-001",
            Amount = 100m,
            Currency = "USD",
            State = ContributionState.Processing,
            Version = 0
        });
        db.Set<ProviderReference>().Add(new ProviderReference
        {
            Id = Guid.NewGuid(),
            ContributionId = contributionId,
            OrganizationId = orgId,
            Reference = reference,
            ProviderName = "sandbox"
        });
        await db.SaveChangesAsync();
        return (orgId, contributionId, reference);
    }

    private static string BuildPayload(string eventId, string status, string? providerReference = "ref_http_001", string? idempotencyKey = null)
    {
        return JsonSerializer.Serialize(new
        {
            eventId,
            eventType = "contribution.submit",
            providerReference,
            idempotencyKey,
            status,
            occurredAt = DateTime.UtcNow.ToString("O"),
            version = 1
        });
    }

    private async Task AssertRejectedWithoutMutationAsync(
        Guid contributionId,
        string eventId,
        HttpResponseMessage response,
        string scenario)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        await using var db = CreateDbContext();
        var contribution = await db.Set<Contribution>()
            .IgnoreQueryFilters()
            .SingleAsync(c => c.Id == contributionId);
        Assert.Equal(ContributionState.Processing, contribution.State);
        Assert.Equal(
            0,
            await db.Set<InboxMessage>()
                .IgnoreQueryFilters()
                .CountAsync(m =>
                    m.MessageId == $"callback-{eventId}"));
        Assert.Equal(
            0,
            await db.Set<StateTransition>()
                .IgnoreQueryFilters()
                .CountAsync(t =>
                    t.ContributionId == contributionId));
        Assert.Equal(
            0,
            await db.Set<OrphanProviderCallback>()
                .IgnoreQueryFilters()
                .CountAsync(o => o.EventId == eventId));

        _output.WriteLine(
            "REJECTED | Scenario={0} | Status=401 | " +
            "Contribution=Processing | Inbox=0 | " +
            "StateTransition=0 | Orphan=0",
            scenario);
    }

    [Fact]
    public async Task ValidSignature_ShouldReturn200()
    {
        var (_, contributionId, reference) = await SeedContributionWithReferenceAsync();
        var payload = BuildPayload("http-evt-1", "succeeded", reference);
        var timestamp = DateTime.UtcNow.ToString("O");
        var signature = ComputeSignature(timestamp, payload);

        var response = await PostCallbackAsync(signature, timestamp, payload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var db = CreateDbContext();
        var contribution = await db.Set<Contribution>().IgnoreQueryFilters().SingleAsync(c => c.Id == contributionId);
        Assert.Equal(ContributionState.Succeeded, contribution.State);
        Assert.Equal(
            1,
            await db.Set<InboxMessage>()
                .IgnoreQueryFilters()
                .CountAsync(m =>
                    m.MessageId == "callback-http-evt-1"));
        Assert.Equal(
            1,
            await db.Set<StateTransition>()
                .IgnoreQueryFilters()
                .CountAsync(t =>
                    t.ContributionId == contributionId &&
                    t.ToState == ContributionState.Succeeded));
        _output.WriteLine(
            "ACCEPTED | Scenario=ValidHmac | Status=200 | " +
            "Contribution=Succeeded | Inbox=1 | StateTransition=1");
    }

    [Fact]
    public async Task MissingSignature_ShouldReturn401()
    {
        var (_, contributionId, reference) =
            await SeedContributionWithReferenceAsync();
        const string eventId = "http-evt-2";
        var payload = BuildPayload(eventId, "succeeded", reference);
        var timestamp = DateTime.UtcNow.ToString("O");

        var response = await PostCallbackAsync(null, timestamp, payload);

        await AssertRejectedWithoutMutationAsync(
            contributionId,
            eventId,
            response,
            "MissingSignature");
    }

    [Fact]
    public async Task MissingTimestamp_ShouldReturn401()
    {
        var (_, contributionId, reference) =
            await SeedContributionWithReferenceAsync();
        const string eventId = "http-evt-3";
        var payload = BuildPayload(eventId, "succeeded", reference);
        var signature = ComputeSignature("", payload);

        var response = await PostCallbackAsync(signature, null, payload);

        await AssertRejectedWithoutMutationAsync(
            contributionId,
            eventId,
            response,
            "MissingTimestamp");
    }

    [Fact]
    public async Task InvalidSignature_ShouldReturn401_WithoutStateChange()
    {
        var (_, contributionId, reference) = await SeedContributionWithReferenceAsync();
        const string eventId = "http-evt-4";
        var payload = BuildPayload(eventId, "succeeded", reference);
        var timestamp = DateTime.UtcNow.ToString("O");
        var badSignature = "deadbeef" + ComputeSignature(timestamp, payload)[..8];

        var response = await PostCallbackAsync(badSignature, timestamp, payload);

        await AssertRejectedWithoutMutationAsync(
            contributionId,
            eventId,
            response,
            "InvalidSignature");
    }

    [Fact]
    public async Task InvalidTimestampFormat_ShouldReturn401()
    {
        var (_, contributionId, reference) =
            await SeedContributionWithReferenceAsync();
        const string eventId = "http-evt-5";
        var payload = BuildPayload(eventId, "succeeded", reference);
        var signature = ComputeSignature("not-a-timestamp", payload);

        var response = await PostCallbackAsync(signature, "not-a-timestamp", payload);

        await AssertRejectedWithoutMutationAsync(
            contributionId,
            eventId,
            response,
            "InvalidTimestampFormat");
    }

    [Fact]
    public async Task ExpiredTimestamp_ShouldReturn401()
    {
        var (_, contributionId, reference) =
            await SeedContributionWithReferenceAsync();
        const string eventId = "http-evt-6";
        var payload = BuildPayload(eventId, "succeeded", reference);
        var oldTimestamp = DateTime.UtcNow.AddMinutes(-10).ToString("O");
        var signature = ComputeSignature(oldTimestamp, payload);

        var response = await PostCallbackAsync(signature, oldTimestamp, payload);

        await AssertRejectedWithoutMutationAsync(
            contributionId,
            eventId,
            response,
            "ExpiredTimestamp");
    }

    [Fact]
    public async Task FutureTimestampOutsideClockSkew_ShouldReturn401()
    {
        var (_, contributionId, reference) =
            await SeedContributionWithReferenceAsync();
        const string eventId = "http-evt-7";
        var payload = BuildPayload(eventId, "succeeded", reference);
        var futureTimestamp = DateTime.UtcNow.AddMinutes(10).ToString("O");
        var signature = ComputeSignature(futureTimestamp, payload);

        var response = await PostCallbackAsync(signature, futureTimestamp, payload);

        await AssertRejectedWithoutMutationAsync(
            contributionId,
            eventId,
            response,
            "FutureTimestamp");
    }

    [Fact]
    public async Task NonUtcTimestamp_ShouldBeRejected()
    {
        var (_, contributionId, reference) =
            await SeedContributionWithReferenceAsync();
        const string eventId = "http-evt-8";
        var payload = BuildPayload(eventId, "succeeded", reference);
        // A non-UTC offset (+08:00) must be rejected even with a valid signature.
        var localTimestamp = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("O");
        var signature = ComputeSignature(localTimestamp, payload);

        var response = await PostCallbackAsync(signature, localTimestamp, payload);

        await AssertRejectedWithoutMutationAsync(
            contributionId,
            eventId,
            response,
            "NonUtcTimestamp");
    }

    public void Dispose()
    {
        _factory.Dispose();
    }
}
