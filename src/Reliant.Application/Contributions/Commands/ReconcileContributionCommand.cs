using MediatR;
using Reliant.Application.Abstractions;
using Reliant.Application.Messaging;
using Reliant.Application.Observability;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using System.Diagnostics;
using System.Text.Json;

namespace Reliant.Application.Contributions.Commands;

public record ReconcileContributionCommand(Guid ContributionId) : IRequest<ReconciliationResult>;

/// <summary>
/// Outcome of one reconciliation cycle.
/// <see cref="Resolved"/> is true ONLY when this cycle resolved the local/remote
/// difference (Succeeded / Failed confirmed, or NotFound scheduled a safe retry).
/// It is FALSE when the difference could not be auto-resolved: ManualRequired
/// (needs human intervention), provider Pending / Unavailable / InvalidResponse,
/// or a transient error - the contribution stays unresolved for a later cycle.
/// </summary>
public record ReconciliationResult(bool Resolved, string Resolution, ReconciliationDifference? Difference = null);

public class ReconcileContributionHandler(
    IContributionRepository contributionRepo,
    IProcessingAttemptRepository attemptRepo,
    IProviderReferenceRepository referenceRepo,
    IReconciliationRepository reconciliationRepo,
    IOutboxRepository outboxRepo,
    IProvider provider,
    IStateTransitionRepository stateTransitionRepo,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : IRequestHandler<ReconcileContributionCommand, ReconciliationResult>
{
    private const int MaxReconciliationCount = 20;
    private static readonly RetryPolicy RetryPolicy = new();

    public async Task<ReconciliationResult> Handle(ReconcileContributionCommand request, CancellationToken ct)
    {
        using var activity = ReliantTelemetry.StartActivity(
            "reconciliation evaluate");
        activity?.SetTag(
            "reliant.contribution_id",
            request.ContributionId);
        // The worker collects pending contribution IDs across all tenants
        // (IgnoreQueryFilters), so the handler must load by ID without the
        // ambient tenant filter and operate on the entity's own OrganizationId.
        var contribution = await contributionRepo.GetByIdIgnoreTenantAsync(request.ContributionId, ct);
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
            // Max count: stop infinite scanning with an operator alert and a
            // ManualRequired reconciliation record; move to Failed (terminal).
            var fromState = contribution.State;
            contribution.TransitionTo(ContributionState.Failed, "Max reconciliation count exceeded");
            contribution.NextRetryAt = null;
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
            await reconciliationRepo.AddAsync(new ReconciliationRecord
            {
                Id = Guid.NewGuid(),
                ContributionId = contribution.Id,
                OrganizationId = contribution.OrganizationId,
                LocalState = contribution.State,
                ProviderState = "MaxAttempts",
                Difference = ReconciliationDifference.ProviderUnavailable,
                Resolution = "ManualRequired",
                ResolvedAt = null
            }, ct);
            await outboxRepo.AddAsync(new OutboxMessage
            {
                Id = Guid.NewGuid(),
                OrganizationId = contribution.OrganizationId,
                MessageType = "OperatorAlert",
                Payload = JsonSerializer.Serialize(new
                {
                    alert = "Contribution stuck in reconciliation after max cycles, manual required",
                    contributionId = contribution.Id
                }),
                CorrelationId = Guid.NewGuid().ToString(),
                CausationId = request.ContributionId.ToString(),
                TraceParent = Activity.Current?.Id,
                TraceState = Activity.Current?.TraceStateString,
                OccurredAt = timeProvider.GetUtcNow().UtcDateTime,
                Status = OutboxStatus.Pending,
                Version = 0
            }, ct);
            await unitOfWork.SaveChangesAsync(ct);
            ReliantTelemetry.RecordReconciliationResolution(
                "ManualRequired");
            // ManualRequired means the difference is NOT resolved - an operator must
            // act. Resolved must be false so consumers do not treat this as converged.
            return new ReconciliationResult(false, "Max reconciliation count exceeded, ManualRequired");
        }

        var reference = await referenceRepo.GetByContributionAsync(request.ContributionId, ct);
        ProviderStatusResult? providerResult = null;
        Exception? providerError = null;

        if (reference is not null)
        {
            try
            {
                providerResult = await provider.QueryStatusByReferenceAsync(reference.Reference, ct);
            }
            catch (Exception ex)
            {
                providerError = ex;
            }
        }
        else
        {
            var latestAttempt = await attemptRepo.GetLatestByContributionAsync(request.ContributionId, ct);
            if (latestAttempt is not null)
            {
                try
                {
                    providerResult = await provider.QueryStatusByIdempotencyKeyAsync(latestAttempt.ProviderIdempotencyKey, ct);
                }
                catch (Exception ex)
                {
                    providerError = ex;
                }
            }
            else
            {
                // No local attempt: cannot prove safety, require manual intervention.
                await reconciliationRepo.AddAsync(new ReconciliationRecord
                {
                    Id = Guid.NewGuid(),
                    ContributionId = request.ContributionId,
                    OrganizationId = contribution.OrganizationId,
                    LocalState = contribution.State,
                    ProviderState = "NoEvidence",
                    Difference = ReconciliationDifference.ProviderNotFound,
                    Resolution = "ManualRequired",
                    ResolvedAt = null
                }, ct);
                await unitOfWork.SaveChangesAsync(ct);
                ReliantTelemetry.RecordReconciliationResolution(
                    "ManualRequired");
                return new ReconciliationResult(false, "No local evidence, ManualRequired");
            }
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // ProviderUnavailable / transient query failure: stay unresolved, wait.
        if (providerResult is null)
        {
            await reconciliationRepo.AddAsync(new ReconciliationRecord
            {
                Id = Guid.NewGuid(),
                ContributionId = request.ContributionId,
                OrganizationId = contribution.OrganizationId,
                LocalState = contribution.State,
                ProviderState = "Unavailable",
                Difference = ReconciliationDifference.ProviderUnavailable,
                Resolution = "WaitNextCycle",
                ResolvedAt = null,
                CreatedAt = now
            }, ct);
            await unitOfWork.SaveChangesAsync(ct);
            ReliantTelemetry.RecordReconciliationResolution(
                "WaitNextCycle");
            return new ReconciliationResult(false, $"Provider unavailable: {providerError?.Message}");
        }

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
            Resolution = "WaitNextCycle",
            CreatedAt = now
        };

        switch (providerResult.Status)
        {
            case ProviderStatus.Succeeded:
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
                    record.Resolution = "AutoFixed";
                    record.ResolvedAt = now;
                    record.ResolvedBy = "ReconciliationHandler";
                    await PersistProviderReferenceIfMissingAsync(contribution, reference, providerResult.ProviderReference, ct);
                    break;
                }

            case ProviderStatus.Failed:
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
                    record.Resolution = "AutoFixed";
                    record.ResolvedAt = now;
                    record.ResolvedBy = "ReconciliationHandler";
                    await PersistProviderReferenceIfMissingAsync(contribution, reference, providerResult.ProviderReference, ct);
                    break;
                }

            case ProviderStatus.NotFound:
                {
                    var fromState = contribution.State;
                    contribution.TransitionTo(ContributionState.RetryPending, "Reconciliation: provider not found, safe to retry");
                    contribution.NextRetryAt = now.Add(RetryPolicy.GetDelay(contribution.RetryCount + 1));
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
                    record.Resolution = "SafeRetry";
                    record.ResolvedAt = now;
                    record.ResolvedBy = "ReconciliationHandler";
                    break;
                }

            case ProviderStatus.Pending:
            default:
                // Pending / InvalidResponse: stay ReconciliationPending, unresolved.
                record.Resolution = "WaitNextCycle";
                record.ResolvedAt = null;
                break;
        }

        await reconciliationRepo.AddAsync(record, ct);

        // Concurrent reconciliation: if another worker already applied the same
        // resolution, the update conflicts and we treat it as already handled.
        var saved = await unitOfWork.TrySaveChangesAsync(ct);
        if (!saved)
        {
            return new ReconciliationResult(true, "Concurrent reconciliation already applied");
        }

        ReliantTelemetry.RecordReconciliationResolution(
            record.Resolution);

        return new ReconciliationResult(
            providerResult.Status is ProviderStatus.Succeeded or ProviderStatus.Failed or ProviderStatus.NotFound,
            record.Resolution,
            diff);
    }

    private async Task PersistProviderReferenceIfMissingAsync(
        Contribution contribution,
        ProviderReference? existingReference,
        string? providerReference,
        CancellationToken ct)
    {
        // When the original submit response was lost, the provider query is what
        // reveals the reference. Persist it so the local state has full evidence.
        if (existingReference is null && providerReference is not null)
        {
            await referenceRepo.AddAsync(new ProviderReference
            {
                Id = Guid.NewGuid(),
                ContributionId = contribution.Id,
                OrganizationId = contribution.OrganizationId,
                Reference = providerReference,
                ProviderName = "sandbox"
            }, ct);
        }
    }
}
