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

namespace Reliant.Tests.Integration;

[Trait("Category", "Integration")]
[Trait("Dependency", "HttpApi")]
public class CallbackSecurityHttpTests : IClassFixture<PostgreSqlFixture>, IDisposable
{
    private const string Secret = "test-secret-key";
    private readonly PostgreSqlFixture _fixture;
    private readonly WebApplicationFactory<Program> _factory;

    public CallbackSecurityHttpTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
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
    }

    [Fact]
    public async Task MissingSignature_ShouldReturn401()
    {
        var payload = BuildPayload("http-evt-2", "succeeded");
        var timestamp = DateTime.UtcNow.ToString("O");

        var response = await PostCallbackAsync(null, timestamp, payload);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MissingTimestamp_ShouldReturn401()
    {
        var payload = BuildPayload("http-evt-3", "succeeded");
        var signature = ComputeSignature("", payload);

        var response = await PostCallbackAsync(signature, null, payload);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task InvalidSignature_ShouldReturn401_WithoutStateChange()
    {
        var (_, contributionId, reference) = await SeedContributionWithReferenceAsync();
        var payload = BuildPayload("http-evt-4", "succeeded", reference);
        var timestamp = DateTime.UtcNow.ToString("O");
        var badSignature = "deadbeef" + ComputeSignature(timestamp, payload)[..8];

        var response = await PostCallbackAsync(badSignature, timestamp, payload);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        await using var db = CreateDbContext();
        var contribution = await db.Set<Contribution>().IgnoreQueryFilters().SingleAsync(c => c.Id == contributionId);
        Assert.Equal(ContributionState.Processing, contribution.State);

        var inbox = await db.Set<InboxMessage>().IgnoreQueryFilters()
            .Where(m => m.MessageId == "callback-http-evt-4").ToListAsync();
        Assert.Empty(inbox);

        var orphan = await db.Set<OrphanProviderCallback>().IgnoreQueryFilters()
            .Where(o => o.EventId == "http-evt-4").ToListAsync();
        Assert.Empty(orphan);
    }

    [Fact]
    public async Task InvalidTimestampFormat_ShouldReturn401()
    {
        var payload = BuildPayload("http-evt-5", "succeeded");
        var signature = ComputeSignature("not-a-timestamp", payload);

        var response = await PostCallbackAsync(signature, "not-a-timestamp", payload);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExpiredTimestamp_ShouldReturn401()
    {
        var payload = BuildPayload("http-evt-6", "succeeded");
        var oldTimestamp = DateTime.UtcNow.AddMinutes(-10).ToString("O");
        var signature = ComputeSignature(oldTimestamp, payload);

        var response = await PostCallbackAsync(signature, oldTimestamp, payload);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task FutureTimestampOutsideClockSkew_ShouldReturn401()
    {
        var payload = BuildPayload("http-evt-7", "succeeded");
        var futureTimestamp = DateTime.UtcNow.AddMinutes(10).ToString("O");
        var signature = ComputeSignature(futureTimestamp, payload);

        var response = await PostCallbackAsync(signature, futureTimestamp, payload);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task NonUtcTimestamp_ShouldBeRejected()
    {
        var payload = BuildPayload("http-evt-8", "succeeded");
        // A non-UTC offset (+08:00) must be rejected even with a valid signature.
        var localTimestamp = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("O");
        var signature = ComputeSignature(localTimestamp, payload);

        var response = await PostCallbackAsync(signature, localTimestamp, payload);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ValidSignedPayload_ShouldReachCallbackHandler_AndApplyStateOnce()
    {
        var (_, contributionId, reference) = await SeedContributionWithReferenceAsync();
        var payload = BuildPayload("http-evt-9", "succeeded", reference);
        var timestamp = DateTime.UtcNow.ToString("O");
        var signature = ComputeSignature(timestamp, payload);

        var first = await PostCallbackAsync(signature, timestamp, payload);
        var duplicate = await PostCallbackAsync(signature, timestamp, payload);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);

        await using var db = CreateDbContext();
        var contribution = await db.Set<Contribution>().IgnoreQueryFilters().SingleAsync(c => c.Id == contributionId);
        Assert.Equal(ContributionState.Succeeded, contribution.State);

        var inboxes = await db.Set<InboxMessage>().IgnoreQueryFilters()
            .Where(m => m.MessageId == "callback-http-evt-9").ToListAsync();
        Assert.Single(inboxes);

        // Duplicate terminal confirmation creates no additional state change.
        var succeededTransitions = await db.Set<StateTransition>().IgnoreQueryFilters()
            .Where(t => t.ContributionId == contributionId && t.ToState == ContributionState.Succeeded).ToListAsync();
        Assert.Single(succeededTransitions);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }
}
