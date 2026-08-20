using MediatR;
using Reliant.Application.Abstractions;
using Reliant.Application.Messaging;
using Reliant.Application.Observability;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using System.Text.Json;

namespace Reliant.Application.Contributions.Commands;

public record SubmitToProviderCommand(
    Guid ContributionId,
    Guid OrganizationId,
    decimal Amount,
    string Currency,
    string ExternalReference,
    JobExecutionFence? ExecutionFence = null)
    : IRequest<ProviderSubmissionResult>;

public enum ProviderSubmissionDisposition
{
    Succeeded,
    DefinitiveFailure,
    Unknown,
    RetryableFailure,
    DeferredBecauseCircuitOpen
}

public record ProviderSubmissionResult(
    AttemptStatus Status,
    string? ProviderReference,
    ErrorCategory? ErrorCategory,
    string? ErrorMessage,
    ProviderSubmissionDisposition Disposition = ProviderSubmissionDisposition.Succeeded);

public class SubmitToProviderHandler(
    IProvider provider,
    IProcessingAttemptRepository attemptRepo,
    IProviderReferenceRepository referenceRepo,
    IUnitOfWork unitOfWork,
    CircuitBreaker circuitBreaker,
    IProviderOperationKeyFactory keyFactory,
    IWorkerFaultInjector? faultInjector = null,
    ILeaseRepository? leaseRepository = null)
    : IRequestHandler<SubmitToProviderCommand, ProviderSubmissionResult>
{
    private const string ProviderName = "sandbox";
    private readonly IWorkerFaultInjector _faultInjector = faultInjector ?? new NoopWorkerFaultInjector();

    public async Task<ProviderSubmissionResult> Handle(SubmitToProviderCommand request, CancellationToken ct)
    {
        var existingRef = await referenceRepo.GetByContributionAsync(request.ContributionId, ct);
        if (existingRef is not null)
        {
            return new ProviderSubmissionResult(AttemptStatus.Succeeded, existingRef.Reference, null, "Already has provider reference");
        }

        var existingAttempts = await attemptRepo.ListByContributionAsync(request.ContributionId, ct);
        var succeededAttempt = existingAttempts.FirstOrDefault(a => a.Status == AttemptStatus.Succeeded);
        if (succeededAttempt is not null)
        {
            return new ProviderSubmissionResult(AttemptStatus.Succeeded, succeededAttempt.ProviderReference, null, "Already succeeded");
        }

        if (!circuitBreaker.CanExecute())
        {
            // Defer while the circuit is open: no business ProcessingAttempt is
            // created, no retry budget is consumed, and no failure is recorded.
            // The worker leaves the message unacknowledged for redelivery later.
            return new ProviderSubmissionResult(
                AttemptStatus.Pending, null, null, "Circuit breaker is open",
                ProviderSubmissionDisposition.DeferredBecauseCircuitOpen);
        }

        var idempotencyKey = keyFactory.CreateContributionSubmitKey(request.OrganizationId, request.ContributionId, ProviderName);
        var attemptNumber = (existingAttempts.MaxBy(a => a.AttemptNumber)?.AttemptNumber ?? 0) + 1;

        var attempt = new ProcessingAttempt
        {
            Id = Guid.NewGuid(),
            ContributionId = request.ContributionId,
            OrganizationId = request.OrganizationId,
            AttemptNumber = attemptNumber,
            ProviderName = ProviderName,
            ProviderIdempotencyKey = idempotencyKey,
            Status = AttemptStatus.Pending,
            RequestPayload = JsonSerializer.Serialize(new { request.Amount, request.Currency, request.ExternalReference }),
            StartedAt = DateTime.UtcNow
        };

        _faultInjector.Inject(WorkerFaultPoint.BeforeAttemptPersisted, request.ContributionId.ToString());
        await attemptRepo.AddAsync(attempt, ct);

        // Atomic attempt persistence: if a concurrent worker already persisted a
        // same-numbered attempt for this contribution (UNIQUE(ContributionId,
        // AttemptNumber)), this worker loses safely and defers instead of
        // throwing; the redelivered message will observe the winner's reference.
        if (!await TrySaveChangesAsync(
            request.ExecutionFence,
            ct))
        {
            return new ProviderSubmissionResult(
                AttemptStatus.Pending, null, null, "Concurrent submit already in progress",
                ProviderSubmissionDisposition.DeferredBecauseCircuitOpen);
        }

        // Evidence of the pending attempt is durable BEFORE the provider is called.
        _faultInjector.Inject(WorkerFaultPoint.AfterAttemptPersisted, request.ContributionId.ToString());

        try
        {
            var providerRequest = new ProviderRequest(
                idempotencyKey,
                request.Amount,
                request.Currency,
                request.ExternalReference);

            var result = await provider.SubmitAsync(providerRequest, ct);

            _faultInjector.Inject(WorkerFaultPoint.AfterProviderProcessed, request.ContributionId.ToString());
            _faultInjector.Inject(WorkerFaultPoint.BeforeProviderResponseHandled, request.ContributionId.ToString());

            attempt.ResponsePayload = result.RawResponse;
            attempt.CompletedAt = DateTime.UtcNow;

            if (result.Status == ProviderStatus.Succeeded)
            {
                attempt.Status = AttemptStatus.Succeeded;
                attempt.ProviderReference = result.ProviderReference;
                circuitBreaker.RecordSuccess();

                if (result.ProviderReference is not null)
                {
                    await referenceRepo.AddAsync(new ProviderReference
                    {
                        Id = Guid.NewGuid(),
                        ContributionId = request.ContributionId,
                        OrganizationId = request.OrganizationId,
                        Reference = result.ProviderReference
                    }, ct);
                }

                if (!await TrySaveChangesAsync(
                    request.ExecutionFence,
                    ct))
                {
                    return new ProviderSubmissionResult(
                        AttemptStatus.Succeeded,
                        result.ProviderReference,
                        null,
                        "Concurrent provider reference " +
                        "already committed");
                }

                return new ProviderSubmissionResult(AttemptStatus.Succeeded, result.ProviderReference, null, null);
            }

            if (result.Status == ProviderStatus.Failed)
            {
                attempt.Status = AttemptStatus.Failed;
                attempt.ErrorCategory = result.ErrorCategory;
                attempt.ErrorMessage = result.ErrorMessage;
                circuitBreaker.RecordFailure(result.ErrorCategory);

                await SaveChangesAsync(
                    request.ExecutionFence,
                    ct);
                var disposition = result.ErrorCategory is ErrorCategory.PermanentBusinessRejection or ErrorCategory.ValidationFailure or ErrorCategory.AuthenticationFailure
                    ? ProviderSubmissionDisposition.DefinitiveFailure
                    : ProviderSubmissionDisposition.RetryableFailure;
                return new ProviderSubmissionResult(AttemptStatus.Failed, null, result.ErrorCategory, result.ErrorMessage, disposition);
            }

            attempt.Status = AttemptStatus.Unknown;
            attempt.ErrorCategory = result.ErrorCategory;
            attempt.ErrorMessage = result.ErrorMessage;
            circuitBreaker.RecordFailure(result.ErrorCategory);

            await SaveChangesAsync(
                request.ExecutionFence,
                ct);
            ReliantTelemetry.RecordProviderUnknown(
                result.ErrorCategory);
            return new ProviderSubmissionResult(AttemptStatus.Unknown, null, result.ErrorCategory, result.ErrorMessage, ProviderSubmissionDisposition.Unknown);
        }
        catch (InjectedWorkerCrashException)
        {
            // Fault injection represents loss of the worker execution, not a
            // provider failure. Let the worker leave the queue message unacked
            // so a later delivery can recover with the same provider key.
            throw;
        }
        catch (StaleJobOwnerException)
        {
            throw;
        }
        catch (OperationCanceledException)
            when (ct.IsCancellationRequested)
        {
            // The provider outcome cannot be assumed when host shutdown
            // cancels an in-flight submission. Persist the uncertainty with a
            // non-cancelled token, then let the worker release its Job lease
            // and leave the queue message unacknowledged for recovery.
            attempt.Status = AttemptStatus.Unknown;
            attempt.ErrorCategory = ErrorCategory.UnknownOutcome;
            attempt.ErrorMessage =
                "Provider submission interrupted by worker shutdown";
            attempt.CompletedAt = DateTime.UtcNow;
            await SaveChangesAsync(
                request.ExecutionFence,
                CancellationToken.None);
            ReliantTelemetry.RecordProviderUnknown(
                ErrorCategory.UnknownOutcome);
            throw;
        }
        catch (Exception ex)
        {
            attempt.Status = AttemptStatus.Unknown;
            attempt.ErrorCategory = ErrorCategory.Timeout;
            attempt.ErrorMessage = ex.Message;
            attempt.CompletedAt = DateTime.UtcNow;
            circuitBreaker.RecordFailure(ErrorCategory.Timeout);

            await SaveChangesAsync(
                request.ExecutionFence,
                ct);
            ReliantTelemetry.RecordProviderUnknown(
                ErrorCategory.Timeout);
            return new ProviderSubmissionResult(AttemptStatus.Unknown, null, ErrorCategory.Timeout, ex.Message, ProviderSubmissionDisposition.Unknown);
        }
    }

    private async Task<bool> TrySaveChangesAsync(
        JobExecutionFence? fence,
        CancellationToken cancellationToken)
    {
        if (!fence.HasValue)
        {
            return await unitOfWork.TrySaveChangesAsync(
                cancellationToken);
        }

        await unitOfWork.BeginTransactionAsync(
            cancellationToken);
        try
        {
            await LockFenceOrThrowAsync(
                fence.Value,
                cancellationToken);
            var saved = await unitOfWork.TrySaveChangesAsync(
                cancellationToken);
            if (!saved)
            {
                await unitOfWork.RollbackAsync(
                    CancellationToken.None);
                return false;
            }

            await unitOfWork.CommitAsync(
                cancellationToken);
            return true;
        }
        catch
        {
            await unitOfWork.RollbackAsync(
                CancellationToken.None);
            throw;
        }
    }

    private async Task SaveChangesAsync(
        JobExecutionFence? fence,
        CancellationToken cancellationToken)
    {
        if (!fence.HasValue)
        {
            await unitOfWork.SaveChangesAsync(
                cancellationToken);
            return;
        }

        await unitOfWork.BeginTransactionAsync(
            cancellationToken);
        try
        {
            await LockFenceOrThrowAsync(
                fence.Value,
                cancellationToken);
            await unitOfWork.SaveChangesAsync(
                cancellationToken);
            await unitOfWork.CommitAsync(
                cancellationToken);
        }
        catch
        {
            await unitOfWork.RollbackAsync(
                CancellationToken.None);
            throw;
        }
    }

    private async Task LockFenceOrThrowAsync(
        JobExecutionFence fence,
        CancellationToken cancellationToken)
    {
        var requiredLeaseRepository =
            leaseRepository ??
            throw new InvalidOperationException(
                "Fenced provider submission requires " +
                "an ILeaseRepository");
        if (!await requiredLeaseRepository
            .TryLockCurrentOwnerAsync(
            fence,
            DateTime.UtcNow,
            cancellationToken))
        {
            throw new StaleJobOwnerException(fence);
        }
    }
}
