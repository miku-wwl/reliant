using MediatR;
using Reliant.Application.Abstractions;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using System.Text.Json;

namespace Reliant.Application.Contributions.Commands;

public record ReconcileContributionCommand(Guid ContributionId) : IRequest<ReconciliationResult>;

public record ReconciliationResult(bool Resolved, string Resolution, ReconciliationDifference? Difference = null);

public class ReconcileContributionHandler(
    IContributionRepository contributionRepo,
    IProcessingAttemptRepository attemptRepo,
    IProviderReferenceRepository referenceRepo,
    IReconciliationRepository reconciliationRepo,
    IProvider provider,
    IStateTransitionRepository stateTransitionRepo,
    IUnitOfWork unitOfWork) : IRequestHandler<ReconcileContributionCommand, ReconciliationResult>
{
    private const int MaxReconciliationCount = 20;

    public async Task<ReconciliationResult> Handle(ReconcileContributionCommand request, CancellationToken ct)
    {
        var contribution = await contributionRepo.GetByIdAsync(request.ContributionId, ct);
        if (contribution is null)
        {
            return new ReconciliationResult(false, "Contribution not found");
        }

        if (contribution.State is not (ContributionState.ProviderUnknown or ContributionState.ReconciliationPending))
        {
            return new ReconciliationResult(true, "Not in reconciliation state, skipping");
        }

        var existingRecords = await reconciliationRepo.ListByContributionAsync(request.ContributionId, ct);
        if (existingRecords.Count >= MaxReconciliationCount)
        {
            var fromState = contribution.State;
            contribution.TransitionTo(ContributionState.Failed, "Max reconciliation count exceeded");
            await contributionRepo.UpdateAsync(contribution, ct);
            await stateTransitionRepo.AddAsync(new StateTransition
            {
                Id = Guid.NewGuid(),
                ContributionId = contribution.Id,
                FromState = fromState,
                ToState = ContributionState.Failed,
                Reason = $"Max reconciliation count ({MaxReconciliationCount}) exceeded",
                ChangedBy = "ReconciliationHandler"
            }, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return new ReconciliationResult(true, "Max reconciliation count exceeded, marked as Failed");
        }

        var reference = await referenceRepo.GetByContributionAsync(request.ContributionId, ct);
        ProviderStatusResult? providerResult = null;

        if (reference is not null)
        {
            providerResult = await provider.QueryStatusByReferenceAsync(reference.Reference, ct);
        }
        else
        {
            var latestAttempt = await attemptRepo.GetLatestByContributionAsync(request.ContributionId, ct);
            if (latestAttempt is not null)
            {
                providerResult = await provider.QueryStatusByIdempotencyKeyAsync(latestAttempt.ProviderIdempotencyKey, ct);
            }
            else
            {
                var missingRecord = new ReconciliationRecord
                {
                    Id = Guid.NewGuid(),
                    ContributionId = request.ContributionId,
                    OrganizationId = contribution.OrganizationId,
                    LocalState = contribution.State,
                    ProviderState = "NoEvidence",
                    Difference = ReconciliationDifference.ProviderNotFound,
                    Resolution = "ManualRequired"
                };
                await reconciliationRepo.AddAsync(missingRecord, ct);
                await unitOfWork.SaveChangesAsync(ct);
                return new ReconciliationResult(false, "No local evidence, ManualRequired");
            }
        }

        var diff = providerResult.Status switch
        {
            ProviderStatus.Succeeded when contribution.State != ContributionState.Succeeded => ReconciliationDifference.StateMismatch,
            ProviderStatus.Failed when contribution.State != ContributionState.Failed => ReconciliationDifference.StateMismatch,
            ProviderStatus.NotFound => ReconciliationDifference.ProviderNotFound,
            _ => ReconciliationDifference.None
        };

        var resolution = providerResult.Status switch
        {
            ProviderStatus.Succeeded or ProviderStatus.Failed => "AutoFixed",
            ProviderStatus.NotFound => "SafeRetry",
            ProviderStatus.Pending => "WaitNextCycle",
            _ => "WaitNextCycle"
        };

        var record = new ReconciliationRecord
        {
            Id = Guid.NewGuid(),
            ContributionId = request.ContributionId,
            OrganizationId = contribution.OrganizationId,
            LocalState = contribution.State,
            ProviderState = providerResult.Status.ToString(),
            Difference = diff,
            Resolution = resolution
        };

        if (providerResult.Status == ProviderStatus.Succeeded)
        {
            var fromState = contribution.State;
            contribution.TransitionTo(ContributionState.Succeeded, "Reconciliation: provider confirmed success");
            await contributionRepo.UpdateAsync(contribution, ct);
            await stateTransitionRepo.AddAsync(new StateTransition
            {
                Id = Guid.NewGuid(),
                ContributionId = contribution.Id,
                FromState = fromState,
                ToState = ContributionState.Succeeded,
                Reason = "Reconciliation confirmed success",
                ChangedBy = "ReconciliationHandler"
            }, ct);
            record.ResolvedAt = DateTime.UtcNow;
            record.ResolvedBy = "ReconciliationHandler";
        }
        else if (providerResult.Status == ProviderStatus.Failed)
        {
            var fromState = contribution.State;
            contribution.TransitionTo(ContributionState.Failed, "Reconciliation: provider confirmed failure");
            await contributionRepo.UpdateAsync(contribution, ct);
            await stateTransitionRepo.AddAsync(new StateTransition
            {
                Id = Guid.NewGuid(),
                ContributionId = contribution.Id,
                FromState = fromState,
                ToState = ContributionState.Failed,
                Reason = "Reconciliation confirmed failure",
                ChangedBy = "ReconciliationHandler"
            }, ct);
            record.ResolvedAt = DateTime.UtcNow;
            record.ResolvedBy = "ReconciliationHandler";
        }
        else if (providerResult.Status == ProviderStatus.NotFound)
        {
            var fromState = contribution.State;
            contribution.TransitionTo(ContributionState.RetryPending, "Reconciliation: provider not found, safe to retry");
            await contributionRepo.UpdateAsync(contribution, ct);
            await stateTransitionRepo.AddAsync(new StateTransition
            {
                Id = Guid.NewGuid(),
                ContributionId = contribution.Id,
                FromState = fromState,
                ToState = ContributionState.RetryPending,
                Reason = "Reconciliation: provider not found, safe retry",
                ChangedBy = "ReconciliationHandler"
            }, ct);
            record.ResolvedAt = DateTime.UtcNow;
            record.ResolvedBy = "ReconciliationHandler";
        }

        await reconciliationRepo.AddAsync(record, ct);
        await unitOfWork.SaveChangesAsync(ct);

        return new ReconciliationResult(
            providerResult.Status is ProviderStatus.Succeeded or ProviderStatus.Failed or ProviderStatus.NotFound,
            $"Provider status: {providerResult.Status}",
            diff);
    }
}
