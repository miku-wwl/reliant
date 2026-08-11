using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
                "FencingToken",
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
                {jobRun.FencingToken},
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
        var ownsTransaction =
            db.Database.CurrentTransaction is null;
        if (ownsTransaction)
        {
            await db.Database.BeginTransactionAsync(
                cancellationToken);
        }

        try
        {
            var connection = db.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
            command.Transaction = db.Database
                .CurrentTransaction!
                .GetDbTransaction();
            command.CommandText = """
                WITH candidate AS (
                    SELECT
                        "Id",
                        "FencingToken" + 1 AS next_token
                    FROM job_runs
                    WHERE "Id" = @job_run_id
                    FOR UPDATE
                ),
                inserted AS (
                    INSERT INTO leases (
                        "Id",
                        "JobRunId",
                        "FencingToken",
                        "WorkerId",
                        "AcquiredAt",
                        "ExpiresAt",
                        "LastHeartbeatAt",
                        "IsActive")
                    SELECT
                        @lease_id,
                        candidate."Id",
                        candidate.next_token,
                        @worker_id,
                        @acquired_at,
                        @expires_at,
                        @last_heartbeat_at,
                        @is_active
                    FROM candidate
                    ON CONFLICT ("JobRunId")
                        WHERE "IsActive"
                        DO NOTHING
                    RETURNING "FencingToken"
                ),
                advanced AS (
                    UPDATE job_runs AS job
                    SET
                        "FencingToken" =
                            inserted."FencingToken",
                        "Version" = job."Version" + 1
                    FROM inserted
                    WHERE job."Id" = @job_run_id
                    RETURNING inserted."FencingToken"
                )
                SELECT "FencingToken"
                FROM advanced
                """;
            AddParameter(
                command,
                "job_run_id",
                lease.JobRunId);
            AddParameter(command, "lease_id", lease.Id);
            AddParameter(
                command,
                "worker_id",
                lease.WorkerId);
            AddParameter(
                command,
                "acquired_at",
                lease.AcquiredAt);
            AddParameter(
                command,
                "expires_at",
                lease.ExpiresAt);
            AddParameter(
                command,
                "last_heartbeat_at",
                lease.LastHeartbeatAt ?? (object)DBNull.Value);
            AddParameter(
                command,
                "is_active",
                lease.IsActive);

            var result = await command.ExecuteScalarAsync(
                cancellationToken);
            var acquired =
                result is not null &&
                result is not DBNull;
            if (acquired)
            {
                lease.FencingToken =
                    Convert.ToInt64(result);
            }

            if (ownsTransaction)
            {
                if (acquired)
                {
                    await db.Database.CommitTransactionAsync(
                        cancellationToken);
                }
                else
                {
                    await db.Database.RollbackTransactionAsync(
                        cancellationToken);
                }
            }

            return acquired;
        }
        catch
        {
            if (ownsTransaction &&
                db.Database.CurrentTransaction is not null)
            {
                await db.Database.RollbackTransactionAsync(
                    CancellationToken.None);
            }

            throw;
        }
    }

    public async Task<bool> TryLockCurrentOwnerAsync(
        JobExecutionFence fence,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "A transaction is required before locking a job fence");
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction =
            transaction.GetDbTransaction();
        command.CommandText = """
            SELECT 1
            FROM job_runs AS job
            INNER JOIN leases AS lease
                ON lease."JobRunId" = job."Id"
            WHERE job."Id" = @job_run_id
              AND job."FencingToken" = @fencing_token
              AND lease."Id" = @lease_id
              AND lease."FencingToken" = @fencing_token
              AND lease."IsActive"
              AND lease."ExpiresAt" > @now
            FOR UPDATE OF job, lease
            """;
        AddParameter(
            command,
            "job_run_id",
            fence.JobRunId);
        AddParameter(
            command,
            "lease_id",
            fence.LeaseId);
        AddParameter(
            command,
            "fencing_token",
            fence.FencingToken);
        AddParameter(command, "now", now);

        var result = await command.ExecuteScalarAsync(
            cancellationToken);
        return result is not null && result is not DBNull;
    }

    public async Task<bool> RenewAsync(
        JobExecutionFence fence,
        DateTime heartbeatAt,
        DateTime newExpiresAt,
        CancellationToken cancellationToken = default)
    {
        var affected = await db.Leases
            .IgnoreQueryFilters()
            .Where(x =>
                x.Id == fence.LeaseId &&
                x.JobRunId == fence.JobRunId &&
                x.FencingToken == fence.FencingToken &&
                x.IsActive &&
                x.ExpiresAt > heartbeatAt)
            .ExecuteUpdateAsync(x => x
                .SetProperty(p => p.ExpiresAt, newExpiresAt)
                .SetProperty(p => p.LastHeartbeatAt, heartbeatAt), cancellationToken);
        return affected == 1;
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

    private static void AddParameter(
        System.Data.Common.DbCommand command,
        string name,
        object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
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

    public async Task<bool> TryMarkAsReplayedAsync(
        Guid id,
        string replayMessageId,
        string requestedBy,
        DateTime replayedAt,
        CancellationToken cancellationToken = default)
    {
        var updated = await db.DeadLetterRecords
            .Where(x =>
                x.Id == id &&
                x.Status == DeadLetterStatus.Pending &&
                x.ReplayCount < 3)
            .ExecuteUpdateAsync(x => x
                .SetProperty(p => p.Status, DeadLetterStatus.Replayed)
                .SetProperty(p => p.ReplayedAt, replayedAt)
                .SetProperty(p => p.ReplayCount, p => p.ReplayCount + 1)
                .SetProperty(p => p.ReplayMessageId, replayMessageId)
                .SetProperty(p => p.ReplayRequestedBy, requestedBy),
                cancellationToken);
        return updated == 1;
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
