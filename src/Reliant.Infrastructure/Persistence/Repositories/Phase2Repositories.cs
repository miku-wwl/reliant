using Microsoft.EntityFrameworkCore;
using Reliant.Application.Abstractions;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;

namespace Reliant.Infrastructure.Persistence.Repositories;

public class OutboxRepository(ReliantDbContext db) : IOutboxRepository
{
    public async Task<List<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        return await db.OutboxMessages
            .IgnoreQueryFilters()
            .Where(x => x.Status == OutboxStatus.Pending)
            .OrderBy(x => x.OccurredAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    public async Task MarkAsSentAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await db.OutboxMessages
            .IgnoreQueryFilters()
            .Where(x => x.Id == id)
            .ExecuteUpdateAsync(x => x
                .SetProperty(p => p.Status, OutboxStatus.Sent)
                .SetProperty(p => p.SentAt, DateTime.UtcNow), cancellationToken);
    }

    public async Task MarkAsFailedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await db.OutboxMessages
            .IgnoreQueryFilters()
            .Where(x => x.Id == id)
            .ExecuteUpdateAsync(x => x
                .SetProperty(p => p.Status, OutboxStatus.Failed), cancellationToken);
    }

    public async Task<int> RecordSendFailureAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await db.OutboxMessages
            .IgnoreQueryFilters()
            .Where(x =>
                x.Id == id &&
                x.Status == OutboxStatus.Pending)
            .ExecuteUpdateAsync(
                x => x.SetProperty(
                    p => p.SendCount,
                    p => p.SendCount + 1),
                cancellationToken);

        return await db.OutboxMessages
            .IgnoreQueryFilters()
            .Where(x => x.Id == id)
            .Select(x => x.SendCount)
            .SingleAsync(cancellationToken);
    }

    public async Task AddAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        await db.OutboxMessages.AddAsync(message, cancellationToken);
    }
}

public class InboxRepository(ReliantDbContext db) : IInboxRepository
{
    public async Task<InboxMessage?> GetByMessageIdAsync(string messageId, CancellationToken cancellationToken = default)
    {
        return await db.InboxMessages
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.MessageId == messageId, cancellationToken);
    }

    public async Task AddAsync(InboxMessage message, CancellationToken cancellationToken = default)
    {
        await db.InboxMessages.AddAsync(message, cancellationToken);
    }
}

public class JobRunRepository(ReliantDbContext db) : IJobRunRepository
{
    public async Task<JobRun?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await db.JobRuns
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task EnsurePendingAsync(
        JobRun jobRun,
        CancellationToken cancellationToken = default)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO job_runs (
                "Id",
                "OrganizationId",
                "JobDefinitionId",
                "QueueUrl",
                "MessageId",
                "Payload",
                "Status",
                "AttemptCount",
                "StartedAt",
                "CompletedAt",
                "CreatedAt",
                "Version")
            VALUES (
                {jobRun.Id},
                {jobRun.OrganizationId},
                {jobRun.JobDefinitionId},
                {jobRun.QueueUrl},
                {jobRun.MessageId},
                {jobRun.Payload},
                {(int)jobRun.Status},
                {jobRun.AttemptCount},
                {jobRun.StartedAt},
                {jobRun.CompletedAt},
                {jobRun.CreatedAt},
                {jobRun.Version})
            ON CONFLICT ("Id") DO NOTHING
            """, cancellationToken);
    }

    public async Task AddAsync(JobRun jobRun, CancellationToken cancellationToken = default)
    {
        await db.JobRuns.AddAsync(jobRun, cancellationToken);
    }

    public async Task UpdateAsync(JobRun jobRun, CancellationToken cancellationToken = default)
    {
        db.JobRuns.Update(jobRun);
        await Task.CompletedTask;
    }

    public async Task<List<JobRun>> GetByStatusAsync(Guid organizationId, JobStatus status, int limit, CancellationToken cancellationToken = default)
    {
        return await db.JobRuns
            .Where(x => x.OrganizationId == organizationId && x.Status == status)
            .OrderByDescending(x => x.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }
}

public class JobAttemptRepository(ReliantDbContext db) : IJobAttemptRepository
{
    public async Task<JobAttempt?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return await db.JobAttempts
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<JobAttempt?> GetRunningByJobRunAsync(
        Guid jobRunId,
        CancellationToken cancellationToken = default)
    {
        return await db.JobAttempts
            .IgnoreQueryFilters()
            .Where(x =>
                x.JobRunId == jobRunId &&
                x.Status == JobAttemptStatus.Running)
            .OrderByDescending(x => x.AttemptNumber)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task AddAsync(
        JobAttempt attempt,
        CancellationToken cancellationToken = default)
    {
        await db.JobAttempts.AddAsync(attempt, cancellationToken);
    }
}

public class LeaseRepository(ReliantDbContext db) : ILeaseRepository
{
    public async Task<Lease?> GetActiveByJobRunAsync(Guid jobRunId, CancellationToken cancellationToken = default)
    {
        return await db.Leases
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.JobRunId == jobRunId && x.IsActive, cancellationToken);
    }

    public async Task<bool> TryAcquireAsync(
        Lease lease,
        CancellationToken cancellationToken = default)
    {
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO leases (
                "Id",
                "JobRunId",
                "WorkerId",
                "AcquiredAt",
                "ExpiresAt",
                "LastHeartbeatAt",
                "IsActive")
            VALUES (
                {lease.Id},
                {lease.JobRunId},
                {lease.WorkerId},
                {lease.AcquiredAt},
                {lease.ExpiresAt},
                {lease.LastHeartbeatAt},
                {lease.IsActive})
            ON CONFLICT ("JobRunId")
                WHERE "IsActive"
                DO NOTHING
            """, cancellationToken);

        return affected == 1;
    }

    public async Task RenewAsync(Guid leaseId, DateTime newExpiresAt, CancellationToken cancellationToken = default)
    {
        await db.Leases
            .IgnoreQueryFilters()
            .Where(x => x.Id == leaseId)
            .ExecuteUpdateAsync(x => x
                .SetProperty(p => p.ExpiresAt, newExpiresAt)
                .SetProperty(p => p.LastHeartbeatAt, DateTime.UtcNow), cancellationToken);
    }

    public async Task ReleaseAsync(Guid leaseId, CancellationToken cancellationToken = default)
    {
        await db.Leases
            .IgnoreQueryFilters()
            .Where(x => x.Id == leaseId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.IsActive, false), cancellationToken);
    }

    public async Task<bool> TryReleaseExpiredAsync(
        Guid leaseId,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        var affected = await db.Leases
            .IgnoreQueryFilters()
            .Where(x =>
                x.Id == leaseId &&
                x.IsActive &&
                x.ExpiresAt < now)
            .ExecuteUpdateAsync(
                x => x.SetProperty(p => p.IsActive, false),
                cancellationToken);
        return affected == 1;
    }

    public async Task<List<Lease>> GetExpiredAsync(
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        return await db.Leases
            .IgnoreQueryFilters()
            .Where(x => x.IsActive && x.ExpiresAt < now)
            .ToListAsync(cancellationToken);
    }
}

public class DeadLetterRepository(ReliantDbContext db) : IDeadLetterRepository
{
    public async Task AddAsync(DeadLetterRecord record, CancellationToken cancellationToken = default)
    {
        await db.DeadLetterRecords.AddAsync(record, cancellationToken);
    }

    public async Task<List<DeadLetterRecord>> ListAsync(Guid organizationId, int limit, CancellationToken cancellationToken = default)
    {
        return await db.DeadLetterRecords
            .Where(x => x.OrganizationId == organizationId)
            .OrderByDescending(x => x.DeadLetteredAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<DeadLetterRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await db.DeadLetterRecords.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task MarkAsReplayedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await db.DeadLetterRecords
            .Where(x => x.Id == id)
            .ExecuteUpdateAsync(x => x
                .SetProperty(p => p.Status, DeadLetterStatus.Replayed)
                .SetProperty(p => p.ReplayedAt, DateTime.UtcNow)
                .SetProperty(p => p.ReplayCount, p => p.ReplayCount + 1), cancellationToken);
    }

    public async Task MarkAsIgnoredAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await db.DeadLetterRecords
            .Where(x => x.Id == id)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, DeadLetterStatus.Ignored), cancellationToken);
    }
}

public class CheckpointRepository(ReliantDbContext db) : ICheckpointRepository
{
    public async Task<Checkpoint?> GetAsync(Guid jobRunId, string key, CancellationToken cancellationToken = default)
    {
        return await db.Checkpoints
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.JobRunId == jobRunId && x.Key == key, cancellationToken);
    }

    public async Task SaveAsync(Checkpoint checkpoint, CancellationToken cancellationToken = default)
    {
        var existing = await GetAsync(checkpoint.JobRunId, checkpoint.Key, cancellationToken);
        if (existing is not null)
        {
            existing.Value = checkpoint.Value;
            existing.SavedAt = DateTime.UtcNow;
        }
        else
        {
            await db.Checkpoints.AddAsync(checkpoint, cancellationToken);
        }
    }
}
