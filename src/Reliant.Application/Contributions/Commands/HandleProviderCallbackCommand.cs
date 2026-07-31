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
    IUnitOfWork unitOfWork) : IRequestHandler<HandleProviderCallbackCommand, CallbackHandleResult>
{
    public async Task<CallbackHandleResult> Handle(HandleProviderCallbackCommand request, CancellationToken ct)
    {
        var payload = request.Payload;

        if (string.IsNullOrEmpty(payload.EventId))
            return new CallbackHandleResult(400, "Missing eventId");

        if (payload.ProviderReference is null && payload.IdempotencyKey is null)
            return new CallbackHandleResult(400, "Missing providerReference or idempotencyKey");

        var existing = await inboxRepo.GetByMessageIdAsync($"callback-{payload.EventId}", ct);
        if (existing is { Status: InboxStatus.Processed })
            return new CallbackHandleResult(200, "Already processed");

        Contribution? contribution = null;

        if (payload.ProviderReference is not null)
        {
            var reference = await referenceRepo.GetByContributionAsync(Guid.Empty, ct);
        }

        if (contribution is null && payload.IdempotencyKey is not null)
        {
            var attempts = await attemptRepo.ListByContributionAsync(Guid.Empty, ct);
        }

        if (contribution is null)
        {
            return new CallbackHandleResult(200, "Orphan callback recorded");
        }

        var callbackStatus = payload.Status.ToLowerInvariant();

        if (callbackStatus == "succeeded")
        {
            if (contribution.State == ContributionState.Succeeded)
            {
                return new CallbackHandleResult(200, "Already succeeded");
            }

            if (contribution.State is ContributionState.Failed or ContributionState.Completed)
            {
                await reconciliationRepo.AddAsync(new ReconciliationRecord
                {
                    Id = Guid.NewGuid(),
                    ContributionId = contribution.Id,
                    OrganizationId = contribution.OrganizationId,
                    LocalState = contribution.State,
                    ProviderState = "Succeeded",
                    Difference = ReconciliationDifference.StateMismatch,
                    Resolution = "ManualRequired"
                }, ct);
                await unitOfWork.SaveChangesAsync(ct);
                return new CallbackHandleResult(200, "Conflict: local terminal state, ManualRequired");
            }

            var fromState = contribution.State;
            if (contribution.State is ContributionState.Processing or ContributionState.ProviderUnknown or ContributionState.ReconciliationPending or ContributionState.RetryPending)
            {
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
                return new CallbackHandleResult(200, "Already failed");
            }

            if (contribution.State is ContributionState.Succeeded or ContributionState.Completed)
            {
                await reconciliationRepo.AddAsync(new ReconciliationRecord
                {
                    Id = Guid.NewGuid(),
                    ContributionId = contribution.Id,
                    OrganizationId = contribution.OrganizationId,
                    LocalState = contribution.State,
                    ProviderState = "Failed",
                    Difference = ReconciliationDifference.StateMismatch,
                    Resolution = "ManualRequired"
                }, ct);
                await unitOfWork.SaveChangesAsync(ct);
                return new CallbackHandleResult(200, "Conflict: local terminal state, ManualRequired");
            }

            var fromState = contribution.State;
            if (contribution.State is ContributionState.Processing or ContributionState.ProviderUnknown or ContributionState.ReconciliationPending)
            {
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
        else if (callbackStatus == "pending")
        {
            return new CallbackHandleResult(200, "Pending, no state change");
        }

        var inboxMessage = new InboxMessage
        {
            Id = Guid.NewGuid(),
            MessageId = $"callback-{payload.EventId}",
            OrganizationId = contribution.OrganizationId,
            MessageType = "ProviderCallback",
            HandlerName = "CallbackHandler",
            HandlerVersion = "1.0",
            ProcessedAt = DateTime.UtcNow,
            Status = InboxStatus.Processed
        };
        await inboxRepo.AddAsync(inboxMessage, ct);
        await unitOfWork.SaveChangesAsync(ct);

        return new CallbackHandleResult(200, "Processed");
    }
}
