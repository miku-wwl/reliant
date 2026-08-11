using System.Text.Json;
using Reliant.Application.Abstractions;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;

namespace Reliant.Application.Operations;

public enum DeadLetterReplayOutcome
{
    Replayed,
    NotFound,
    NotPending
}

public sealed record DeadLetterReplayResult(
    DeadLetterReplayOutcome Outcome,
    Guid? ReplayMessageId = null);

public sealed class DeadLetterReplayService(
    IDeadLetterRepository deadLetterRepository,
    IOutboxRepository outboxRepository,
    IAuditEventRepository auditEventRepository,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task<DeadLetterReplayResult> ReplayAsync(
        Guid organizationId,
        Guid deadLetterId,
        string requestedBy,
        string? replacementPayload = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestedBy))
        {
            throw new ArgumentException(
                "The operator identity is required.",
                nameof(requestedBy));
        }

        var record = await deadLetterRepository.GetByIdAsync(
            deadLetterId,
            cancellationToken);
        if (record is null || record.OrganizationId != organizationId)
        {
            return new DeadLetterReplayResult(
                DeadLetterReplayOutcome.NotFound);
        }

        if (record.Status != DeadLetterStatus.Pending ||
            record.ReplayCount >= 3)
        {
            return new DeadLetterReplayResult(
                DeadLetterReplayOutcome.NotPending,
                ParseReplayMessageId(record.ReplayMessageId));
        }

        var replayMessageId = Guid.NewGuid();
        var replayedAt = timeProvider.GetUtcNow().UtcDateTime;
        var payload = replacementPayload ?? record.Payload;

        await unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            var claimed = await deadLetterRepository
                .TryMarkAsReplayedAsync(
                    deadLetterId,
                    replayMessageId.ToString(),
                    requestedBy,
                    replayedAt,
                    cancellationToken);
            if (!claimed)
            {
                await unitOfWork.RollbackAsync(
                    CancellationToken.None);
                return new DeadLetterReplayResult(
                    DeadLetterReplayOutcome.NotPending);
            }

            await outboxRepository.AddAsync(
                new OutboxMessage
                {
                    Id = replayMessageId,
                    OrganizationId = organizationId,
                    MessageType = record.MessageType,
                    Payload = payload,
                    CorrelationId = string.IsNullOrWhiteSpace(
                        record.CorrelationId)
                        ? record.OriginalMessageId
                        : record.CorrelationId,
                    CausationId = record.OriginalMessageId,
                    OccurredAt = replayedAt,
                    Status = OutboxStatus.Pending,
                    Version = 0
                },
                cancellationToken);

            await auditEventRepository.AddAsync(
                new AuditEvent
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = organizationId,
                    EntityType = nameof(DeadLetterRecord),
                    EntityId = deadLetterId,
                    Action = "Replay",
                    ChangedBy = requestedBy,
                    ChangedAt = replayedAt,
                    CorrelationId = string.IsNullOrWhiteSpace(
                        record.CorrelationId)
                        ? record.OriginalMessageId
                        : record.CorrelationId,
                    OldValues = JsonSerializer.Serialize(new
                    {
                        status = record.Status.ToString(),
                        record.ReplayCount
                    }),
                    NewValues = JsonSerializer.Serialize(new
                    {
                        status = DeadLetterStatus.Replayed.ToString(),
                        replayCount = record.ReplayCount + 1,
                        replayMessageId
                    }),
                    Metadata = JsonSerializer.Serialize(new
                    {
                        record.OriginalMessageId,
                        PayloadReplaced = replacementPayload is not null
                    })
                },
                cancellationToken);

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await unitOfWork.CommitAsync(cancellationToken);
            return new DeadLetterReplayResult(
                DeadLetterReplayOutcome.Replayed,
                replayMessageId);
        }
        catch
        {
            await unitOfWork.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static Guid? ParseReplayMessageId(string? value)
        => Guid.TryParse(value, out var parsed) ? parsed : null;
}
