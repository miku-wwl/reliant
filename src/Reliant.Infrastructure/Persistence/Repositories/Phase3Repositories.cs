using Microsoft.EntityFrameworkCore;
using Reliant.Application.Abstractions;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;

namespace Reliant.Infrastructure.Persistence.Repositories;

public class ProcessingAttemptRepository(ReliantDbContext db) : IProcessingAttemptRepository
{
    public async Task<ProcessingAttempt?> GetLatestByContributionAsync(Guid contributionId, CancellationToken ct = default)
    {
        return await db.Set<ProcessingAttempt>()
            .IgnoreQueryFilters()
            .Where(x => x.ContributionId == contributionId)
            .OrderByDescending(x => x.AttemptNumber)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<List<ProcessingAttempt>> ListByContributionAsync(Guid contributionId, CancellationToken ct = default)
    {
        return await db.Set<ProcessingAttempt>()
            .IgnoreQueryFilters()
            .Where(x => x.ContributionId == contributionId)
            .OrderBy(x => x.AttemptNumber)
            .ToListAsync(ct);
    }

    public async Task<ProcessingAttempt?> GetLatestByIdempotencyKeyAsync(string providerIdempotencyKey, CancellationToken ct = default)
    {
        return await db.Set<ProcessingAttempt>()
            .IgnoreQueryFilters()
            .Where(x => x.ProviderIdempotencyKey == providerIdempotencyKey)
            .OrderByDescending(x => x.AttemptNumber)
            .FirstOrDefaultAsync(ct);
    }

    public async Task AddAsync(ProcessingAttempt attempt, CancellationToken ct = default)
    {
        await db.Set<ProcessingAttempt>().AddAsync(attempt, ct);
    }
}

public class ProviderReferenceRepository(ReliantDbContext db) : IProviderReferenceRepository
{
    public async Task<ProviderReference?> GetByContributionAsync(Guid contributionId, CancellationToken ct = default)
    {
        return await db.Set<ProviderReference>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.ContributionId == contributionId, ct);
    }

    public async Task<ProviderReference?> GetByReferenceAsync(string providerReference, CancellationToken ct = default)
    {
        return await db.Set<ProviderReference>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Reference == providerReference, ct);
    }

    public async Task AddAsync(ProviderReference reference, CancellationToken ct = default)
    {
        await db.Set<ProviderReference>().AddAsync(reference, ct);
    }
}

public class OrphanProviderCallbackRepository(ReliantDbContext db) : IOrphanProviderCallbackRepository
{
    public async Task AddAsync(OrphanProviderCallback callback, CancellationToken ct = default)
    {
        await db.Set<OrphanProviderCallback>().AddAsync(callback, ct);
    }

    public async Task<OrphanProviderCallback?> GetByEventIdAsync(string providerName, string eventId, CancellationToken ct = default)
    {
        return await db.Set<OrphanProviderCallback>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.ProviderName == providerName && x.EventId == eventId, ct);
    }
}

public class ReconciliationRepository(ReliantDbContext db) : IReconciliationRepository
{
    public async Task AddAsync(ReconciliationRecord record, CancellationToken ct = default)
    {
        await db.Set<ReconciliationRecord>().AddAsync(record, ct);
    }

    public async Task<List<ReconciliationRecord>> ListByContributionAsync(Guid contributionId, CancellationToken ct = default)
    {
        return await db.Set<ReconciliationRecord>()
            .IgnoreQueryFilters()
            .Where(x => x.ContributionId == contributionId)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<List<Guid>> GetReconciliationPendingContributionIdsAsync(int limit, CancellationToken ct = default)
    {
        return await db.Set<Contribution>()
            .IgnoreQueryFilters()
            .Where(x => x.State == ContributionState.ReconciliationPending || x.State == ContributionState.ProviderUnknown)
            .Select(x => x.Id)
            .Take(limit)
            .ToListAsync(ct);
    }
}
