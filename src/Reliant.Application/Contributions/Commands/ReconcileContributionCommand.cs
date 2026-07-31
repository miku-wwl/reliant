using MediatR;
using Reliant.Application.Abstractions;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;

namespace Reliant.Application.Contributions.Commands;

public record ReconcileContributionCommand(Guid ContributionId) : IRequest<ReconciliationResult>;

public record ReconciliationResult(bool Resolved, string Resolution);

public class ReconcileContributionHandler(
    IContributionRepository contributionRepo,
    IProcessingAttemptRepository attemptRepo,
    IProviderReferenceRepository referenceRepo,
    IReconciliationRepository reconciliationRepo,
    IProvider provider,
    IStateTransitionRepository stateTransitionRepo,
    IUnitOfWork unitOfWork) : IRequestHandler<ReconcileContributionCommand, ReconciliationResult>
{
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

        var reference = await referenceRepo.GetByContributionAsync(request.ContributionId, ct);
        if (reference is null)
        {
            var record = new ReconciliationRecord
            {
                Id = Guid.NewGuid(),
                ContributionId = request.ContributionId,
                OrganizationId = contribution.OrganizationId,
                LocalState = contribution.State,
                ProviderState = "NoReference",
                Difference = ReconciliationDifference.ProviderNotFound,
                Resolution = "No provider reference found, safe to retry"
            };
            await reconciliationRepo.AddAsync(record, ct);

            contribution.TransitionTo(ContributionState.Processing, "Reconciliation: no reference, retrying");
            await contributionRepo.UpdateAsync(contribution, ct);
            await stateTransitionRepo.AddAsync(new StateTransition
            {
                Id = Guid.NewGuid(),
                ContributionId = contribution.Id,
                FromState = contribution.State,
                ToState = ContributionState.Processing,
                Reason = "Reconciliation retry",
                ChangedBy = "ReconciliationHandler"
            }, ct);
            await unitOfWork.SaveChangesAsync(ct);

            return new ReconciliationResult(true, "No reference found, retrying submit");
        }

        try
        {
            var providerResult = await provider.QueryStatusAsync(reference.Reference, ct);

            var diff = providerResult.Status switch
            {
                ProviderStatus.Succeeded when contribution.State != ContributionState.Succeeded => ReconciliationDifference.StateMismatch,
                ProviderStatus.Failed when contribution.State != ContributionState.Failed => ReconciliationDifference.StateMismatch,
                ProviderStatus.NotFound => ReconciliationDifference.ProviderNotFound,
                _ => ReconciliationDifference.None
            };

            var record = new ReconciliationRecord
            {
                Id = Guid.NewGuid(),
                ContributionId = request.ContributionId,
                OrganizationId = contribution.OrganizationId,
                LocalState = contribution.State,
                ProviderState = providerResult.Status.ToString(),
                Difference = diff,
                Resolution = diff == ReconciliationDifference.None ? "AutoFixed" : "AutoFixed"
            };

            if (providerResult.Status == ProviderStatus.Succeeded)
            {
                contribution.TransitionTo(ContributionState.Succeeded, "Reconciliation: provider confirmed success");
                await stateTransitionRepo.AddAsync(new StateTransition
                {
                    Id = Guid.NewGuid(),
                    ContributionId = contribution.Id,
                    FromState = contribution.State,
                    ToState = ContributionState.Succeeded,
                    Reason = "Reconciliation confirmed",
                    ChangedBy = "ReconciliationHandler"
                }, ct);
            }
            else if (providerResult.Status == ProviderStatus.Failed)
            {
                contribution.TransitionTo(ContributionState.Failed, "Reconciliation: provider confirmed failure");
                await stateTransitionRepo.AddAsync(new StateTransition
                {
                    Id = Guid.NewGuid(),
                    ContributionId = contribution.Id,
                    FromState = contribution.State,
                    ToState = ContributionState.Failed,
                    Reason = "Reconciliation confirmed failure",
                    ChangedBy = "ReconciliationHandler"
                }, ct);
            }

            record.ResolvedAt = DateTime.UtcNow;
            record.ResolvedBy = "ReconciliationHandler";
            await reconciliationRepo.AddAsync(record, ct);
            await contributionRepo.UpdateAsync(contribution, ct);
            await unitOfWork.SaveChangesAsync(ct);

            return new ReconciliationResult(true, $"Provider status: {providerResult.Status}");
        }
        catch (Exception)
        {
            var record = new ReconciliationRecord
            {
                Id = Guid.NewGuid(),
                ContributionId = request.ContributionId,
                OrganizationId = contribution.OrganizationId,
                LocalState = contribution.State,
                ProviderState = "Unavailable",
                Difference = ReconciliationDifference.ProviderUnavailable,
                Resolution = "ManualRequired"
            };
            await reconciliationRepo.AddAsync(record, ct);
            await unitOfWork.SaveChangesAsync(ct);

            return new ReconciliationResult(false, "Provider unavailable, will retry next cycle");
        }
    }
}
