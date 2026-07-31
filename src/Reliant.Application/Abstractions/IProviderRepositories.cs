using Reliant.Domain.Entities;

namespace Reliant.Application.Abstractions;

public interface IProcessingAttemptRepository
{
    Task<ProcessingAttempt?> GetLatestByContributionAsync(Guid contributionId, CancellationToken ct = default);
    Task<List<ProcessingAttempt>> ListByContributionAsync(Guid contributionId, CancellationToken ct = default);
    Task<ProcessingAttempt?> GetLatestByIdempotencyKeyAsync(string providerIdempotencyKey, CancellationToken ct = default);
    Task AddAsync(ProcessingAttempt attempt, CancellationToken ct = default);
}

public interface IProviderReferenceRepository
{
    Task<ProviderReference?> GetByContributionAsync(Guid contributionId, CancellationToken ct = default);
    Task<ProviderReference?> GetByReferenceAsync(string providerReference, CancellationToken ct = default);
    Task AddAsync(ProviderReference reference, CancellationToken ct = default);
}

public interface IOrphanProviderCallbackRepository
{
    Task AddAsync(OrphanProviderCallback callback, CancellationToken ct = default);
    Task<OrphanProviderCallback?> GetByEventIdAsync(string providerName, string eventId, CancellationToken ct = default);
}

public interface IReconciliationRepository
{
    Task AddAsync(ReconciliationRecord record, CancellationToken ct = default);
    Task<List<ReconciliationRecord>> ListByContributionAsync(Guid contributionId, CancellationToken ct = default);
    Task<List<Guid>> GetReconciliationPendingContributionIdsAsync(int limit, CancellationToken ct = default);
}
