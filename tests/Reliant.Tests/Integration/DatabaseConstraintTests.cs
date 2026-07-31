using Microsoft.EntityFrameworkCore;
using Reliant.Infrastructure.Persistence;
using Reliant.Infrastructure.Provider;
using Reliant.Tests.Integration.Fixtures;

namespace Reliant.Tests.Integration;

[Trait("Category", "Integration")]
public class DatabaseConstraintTests : IClassFixture<PostgreSqlFixture>
{
    private readonly PostgreSqlFixture _fixture;

    public DatabaseConstraintTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task UniqueIndex_ShouldPreventDuplicateIdempotencyKey()
    {
        await _fixture.ResetAsync();
        var db = _fixture.DbContext;
        var orgId = Guid.NewGuid();

        db.Set<Reliant.Domain.Entities.Organization>().Add(new Reliant.Domain.Entities.Organization
        {
            Id = orgId,
            Name = "Test",
            Status = Reliant.Domain.Enums.OrganizationStatus.Active,
            Version = 0
        });

        db.Set<Reliant.Domain.Entities.IdempotencyRecord>().Add(new Reliant.Domain.Entities.IdempotencyRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            IdempotencyKey = "key-1",
            RequestHash = "hash",
            ExpiresAt = DateTime.UtcNow.AddHours(24)
        });

        TenantFilterAccessor.SetOrganizationId(orgId);
        await db.SaveChangesAsync();

        db.Set<Reliant.Domain.Entities.IdempotencyRecord>().Add(new Reliant.Domain.Entities.IdempotencyRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            IdempotencyKey = "key-1",
            RequestHash = "hash2",
            ExpiresAt = DateTime.UtcNow.AddHours(24)
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        TenantFilterAccessor.Clear();
    }

    [Fact]
    public async Task TenantFilter_ShouldIsolateData()
    {
        await _fixture.ResetAsync();
        var db = _fixture.DbContext;

        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        db.Set<Reliant.Domain.Entities.Organization>().AddRange(
            new Reliant.Domain.Entities.Organization { Id = orgA, Name = "A", Status = Reliant.Domain.Enums.OrganizationStatus.Active, Version = 0 },
            new Reliant.Domain.Entities.Organization { Id = orgB, Name = "B", Status = Reliant.Domain.Enums.OrganizationStatus.Active, Version = 0 }
        );

        var campaignA = Guid.NewGuid();
        db.Set<Reliant.Domain.Entities.Campaign>().Add(new Reliant.Domain.Entities.Campaign
        {
            Id = campaignA,
            OrganizationId = orgA,
            Name = "Camp A",
            Status = Reliant.Domain.Enums.CampaignStatus.Active,
            Version = 0
        });

        TenantFilterAccessor.SetOrganizationId(orgA);
        await db.SaveChangesAsync();

        TenantFilterAccessor.SetOrganizationId(orgB);
        var campaigns = await db.Set<Reliant.Domain.Entities.Campaign>().ToListAsync();
        Assert.Empty(campaigns);

        TenantFilterAccessor.Clear();
    }

    [Fact]
    public async Task OptimisticConcurrency_ShouldPreventLostUpdate()
    {
        await _fixture.ResetAsync();
        var db = _fixture.DbContext;

        var orgId = Guid.NewGuid();
        db.Set<Reliant.Domain.Entities.Organization>().Add(new Reliant.Domain.Entities.Organization
        {
            Id = orgId,
            Name = "Test",
            Status = Reliant.Domain.Enums.OrganizationStatus.Active,
            Version = 1
        });

        TenantFilterAccessor.SetOrganizationId(orgId);
        await db.SaveChangesAsync();

        var options2 = new DbContextOptionsBuilder<ReliantDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;
        using var db2 = new ReliantDbContext(options2);

        TenantFilterAccessor.SetOrganizationId(orgId);
        var org1 = await db.Set<Reliant.Domain.Entities.Organization>().FirstAsync(o => o.Id == orgId);
        var org2 = await db2.Set<Reliant.Domain.Entities.Organization>().FirstAsync(o => o.Id == orgId);

        org1.Name = "Updated by 1";
        org1.Version = 2;
        await db.SaveChangesAsync();

        org2.Name = "Updated by 2";
        org2.Version = 2;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());

        TenantFilterAccessor.Clear();
    }
}
