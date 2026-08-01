using MediatR;
using Reliant.Application.Abstractions;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using System.Text.Json;

namespace Reliant.Application.Contributions.Commands;

public record HandleProviderCallbackCommand(ProviderCallbackPayload Payload) : IRequest<CallbackHandleResult>;

public record CallbackHandleResult(int StatusCode, string Message);

public class HandleProviderCallbackHandler(
    IContributionRepository contributionRepo,
    IProviderReferenceRepository referenceRepo,
    IProcessingAttemptRepository attemptRepo,
    IInboxRepository inboxRepo,
    IStateTransitionRepository stateTransitionRepo,
    IReconciliationRepository reconciliationRepo,
    IOutboxRepository outboxRepo,
    IOrphanProviderCallbackRepository orphanRepo,
    IUnitOfWork unitOfWork) : IRequestHandler<HandleProviderCallbackCommand, CallbackHandleResult>
{
    private const string ProviderName = "sandbox";

    public async Task<CallbackHandleResult> Handle(HandleProviderCallbackCommand request, CancellationToken ct)
    {
        var payload = request.Payload;

        if (string.IsNullOrEmpty(payload.EventId))
            return new CallbackHandleResult(400, "Missing eventId");

        if (payload.ProviderReference is null && payload.IdempotencyKey is null)
            return new CallbackHandleResult(400, "Missing providerReference or idempotencyKey");

        var callbackStatus = payload.Status?.ToLowerInvariant();
        if (callbackStatus is not ("succeeded" or "failed" or "pending"))
        {
            return new CallbackHandleResult(400, "Unknown callback status");
        }

        // Locate the contribution.
        // Queries deliberately ignore the tenant query filter (a callback carries
        // no tenant header); the resolved contribution is validated by its own
        // OrganizationId which is always non-empty for a real record.
        Contribution? contribution = null;

        if (payload.ProviderReference is not null)
        {
            var reference = await referenceRepo.GetByReferenceAsync(payload.ProviderReference, ct);
            if (reference is not null)
            {
                contribution = await contributionRepo.GetByIdIgnoreTenantAsync(reference.ContributionId, ct);
            }
        }

        if (contribution is null && payload.IdempotencyKey is not null)
        {
            var attempt = await attemptRepo.GetLatestByIdempotencyKeyAsync(payload.IdempotencyKey, ct);
            if (attempt is not null)
            {
                contribution = await contributionRepo.GetByIdIgnoreTenantAsync(attempt.ContributionId, ct);
            }
        }

        // No local evidence -> persist the orphan callback for auditability.
        if (contribution is null)
        {
            await orphanRepo.AddAsync(new OrphanProviderCallback
            {
                Id = Guid.NewGuid(),
                ProviderName = ProviderName,
                EventId = payload.EventId,
                ProviderReference = payload.ProviderReference,
                IdempotencyKey = payload.IdempotencyKey,
                Payload = JsonSerializer.Serialize(payload),
                Reason = "No local contribution found by reference or idempotency key"
            }, ct);

            // Inbox receipt (deduplicated by the unique MessageId constraint).
            await WriteInboxAsync(payload, Guid.Empty, ct);

            // Concurrent duplicate orphan callback -> already recorded, treat as done.
            await unitOfWork.TrySaveChangesAsync(ct);

            return new CallbackHandleResult(200, "Orphan callback recorded");
        }

        // Terminal-state conflict must never silently overwrite local state.
        if (callbackStatus == "succeeded" && contribution.State is ContributionState.Failed or ContributionState.Completed)
        {
            await WriteTerminalConflictAsync(contribution, "Succeeded", ct);
            await WriteInboxAsync(payload, contribution.OrganizationId, ct);

            // Concurrent duplicate callback -> already handled.
            await unitOfWork.TrySaveChangesAsync(ct);

            return new CallbackHandleResult(200, "Conflict: local terminal state, ManualRequired");
        }

        if (callbackStatus == "failed" && contribution.State is ContributionState.Succeeded or ContributionState.Completed)
        {
            await WriteTerminalConflictAsync(contribution, "Failed", ct);
            await WriteInboxAsync(payload, contribution.OrganizationId, ct);

            // Concurrent duplicate callback -> already handled.
            await unitOfWork.TrySaveChangesAsync(ct);

            return new CallbackHandleResult(200, "Conflict: local terminal state, ManualRequired");
        }

        if (callbackStatus == "succeeded")
        {
            if (contribution.State == ContributionState.Succeeded)
            {
                // Terminal confirmation of an already-succeeded contribution:
                // record the processed inbox (deduped by MessageId) but add no
                // new state transition or business effect.
                await WriteInboxAsync(payload, contribution.OrganizationId, ct);
                await unitOfWork.TrySaveChangesAsync(ct);
                return new CallbackHandleResult(200, "Already succeeded");
            }

            if (contribution.State is ContributionState.Processing or ContributionState.ProviderUnknown or
                ContributionState.ReconciliationPending or ContributionState.RetryPending)
            {
                var fromState = contribution.State;
                contribution.TransitionTo(ContributionState.Succeeded, "Callback: provider confirmed success");
                await contributionRepo.UpdateAsync(contribution, ct);
                await stateTransitionRepo.AddAsync(new StateTransition
                {
                    Id = Guid.NewGuid(),
                    ContributionId = contribution.Id,
                    FromState = fromState,
                    ToState = ContributionState.Succeeded,
                    Reason = "Callback confirmed success",
                    ChangedBy = "CallbackHandler"
                }, ct);
            }
        }
        else if (callbackStatus == "failed")
        {
            if (contribution.State == ContributionState.Failed)
            {
                // Terminal confirmation of an already-failed contribution.
                await WriteInboxAsync(payload, contribution.OrganizationId, ct);
                await unitOfWork.TrySaveChangesAsync(ct);
                return new CallbackHandleResult(200, "Already failed");
            }

            if (contribution.State is ContributionState.Processing or ContributionState.ProviderUnknown or
                ContributionState.ReconciliationPending)
            {
                var fromState = contribution.State;
                contribution.TransitionTo(ContributionState.Failed, "Callback: provider confirmed failure");
                await contributionRepo.UpdateAsync(contribution, ct);
                await stateTransitionRepo.AddAsync(new StateTransition
                {
                    Id = Guid.NewGuid(),
                    ContributionId = contribution.Id,
                    FromState = fromState,
                    ToState = ContributionState.Failed,
                    Reason = "Callback confirmed failure",
                    ChangedBy = "CallbackHandler"
                }, ct);
            }
        }
        else // pending
        {
            // Not terminal; do not flip state. Only record that the inbox received it.
        }

        await WriteInboxAsync(payload, contribution.OrganizationId, ct);

        // SaveChanges is atomic: state update + StateTransition + Inbox commit
        // together. If a concurrent duplicate callback already committed the same
        // inbox MessageId, the whole unit rolls back and we treat it as processed.
        var saved = await unitOfWork.TrySaveChangesAsync(ct);
        if (!saved)
        {
            return new CallbackHandleResult(200, "Already processed");
        }

        return new CallbackHandleResult(200, "Processed");
    }

    private async Task WriteTerminalConflictAsync(Contribution contribution, string providerState, CancellationToken ct)
    {
        await reconciliationRepo.AddAsync(new ReconciliationRecord
        {
            Id = Guid.NewGuid(),
            ContributionId = contribution.Id,
            OrganizationId = contribution.OrganizationId,
            LocalState = contribution.State,
            ProviderState = providerState,
            Difference = ReconciliationDifference.StateMismatch,
            Resolution = "ManualRequired"
        }, ct);

        await outboxRepo.AddAsync(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            OrganizationId = contribution.OrganizationId,
            MessageType = "OperatorAlert",
            Payload = JsonSerializer.Serialize(new
            {
                alert = "Callback conflicts with local terminal state",
                contributionId = contribution.Id,
                localState = contribution.State.ToString(),
                providerState
            }),
            CorrelationId = Guid.NewGuid().ToString(),
            OccurredAt = DateTime.UtcNow,
            Status = OutboxStatus.Pending,
            Version = 0
        }, ct);
    }

    private async Task WriteInboxAsync(ProviderCallbackPayload payload, Guid organizationId, CancellationToken ct)
    {
        await inboxRepo.AddAsync(new InboxMessage
        {
            Id = Guid.NewGuid(),
            MessageId = $"callback-{payload.EventId}",
            OrganizationId = organizationId,
            MessageType = "ProviderCallback",
            HandlerName = "CallbackHandler",
            HandlerVersion = "1.0",
            ProcessedAt = DateTime.UtcNow,
            Status = InboxStatus.Processed
        }, ct);
    }
}
