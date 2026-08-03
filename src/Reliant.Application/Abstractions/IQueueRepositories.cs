using Reliant.Domain.Entities;
using Reliant.Domain.Enums;

namespace Reliant.Application.Abstractions;

public interface IOutboxRepository
{
    Task<List<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default);
    Task MarkAsSentAsync(Guid id, CancellationToken cancellationToken = default);
    Task MarkAsFailedAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddAsync(OutboxMessage message, CancellationToken cancellationToken = default);
}

public interface IInboxRepository
{
    Task<InboxMessage?> GetByMessageIdAsync(string messageId, CancellationToken cancellationToken = default);
    Task AddAsync(InboxMessage message, CancellationToken cancellationToken = default);
}

public interface IJobRunRepository
{
    Task<JobRun?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task EnsurePendingAsync(JobRun jobRun, CancellationToken cancellationToken = default);
    Task AddAsync(JobRun jobRun, CancellationToken cancellationToken = default);
    Task UpdateAsync(JobRun jobRun, CancellationToken cancellationToken = default);
    Task<List<JobRun>> GetByStatusAsync(Guid organizationId, JobStatus status, int limit, CancellationToken cancellationToken = default);
}

public interface IJobAttemptRepository
{
    Task<JobAttempt?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<JobAttempt?> GetRunningByJobRunAsync(Guid jobRunId, CancellationToken cancellationToken = default);
    Task AddAsync(JobAttempt attempt, CancellationToken cancellationToken = default);
}

public interface ILeaseRepository
{
    Task<Lease?> GetActiveByJobRunAsync(Guid jobRunId, CancellationToken cancellationToken = default);
    Task<bool> TryAcquireAsync(Lease lease, CancellationToken cancellationToken = default);
    Task RenewAsync(Guid leaseId, DateTime newExpiresAt, CancellationToken cancellationToken = default);
    Task ReleaseAsync(Guid leaseId, CancellationToken cancellationToken = default);
    Task<bool> TryReleaseExpiredAsync(Guid leaseId, DateTime now, CancellationToken cancellationToken = default);
    Task<List<Lease>> GetExpiredAsync(DateTime now, CancellationToken cancellationToken = default);
}

public interface IDeadLetterRepository
{
    Task AddAsync(DeadLetterRecord record, CancellationToken cancellationToken = default);
    Task<List<DeadLetterRecord>> ListAsync(Guid organizationId, int limit, CancellationToken cancellationToken = default);
    Task<DeadLetterRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task MarkAsReplayedAsync(Guid id, CancellationToken cancellationToken = default);
    Task MarkAsIgnoredAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface ICheckpointRepository
{
    Task<Checkpoint?> GetAsync(Guid jobRunId, string key, CancellationToken cancellationToken = default);
    Task SaveAsync(Checkpoint checkpoint, CancellationToken cancellationToken = default);
}
