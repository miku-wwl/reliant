using MediatR;
using Reliant.Application.Abstractions;
using Reliant.Application.Messaging;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using System.Text.Json;

namespace Reliant.Application.Contributions.Commands;

public record SubmitToProviderCommand(Guid ContributionId, Guid OrganizationId, decimal Amount, string Currency, string ExternalReference) : IRequest<ProviderSubmissionResult>;

public record ProviderSubmissionResult(AttemptStatus Status, string? ProviderReference, ErrorCategory? ErrorCategory, string? ErrorMessage);

public class SubmitToProviderHandler(
    IProvider provider,
    IProcessingAttemptRepository attemptRepo,
    IProviderReferenceRepository referenceRepo,
    IContributionRepository contributionRepo,
    IUnitOfWork unitOfWork,
    CircuitBreaker circuitBreaker) : IRequestHandler<SubmitToProviderCommand, ProviderSubmissionResult>
{
    public async Task<ProviderSubmissionResult> Handle(SubmitToProviderCommand request, CancellationToken ct)
    {
        if (!circuitBreaker.CanExecute())
        {
            return new ProviderSubmissionResult(AttemptStatus.Failed, null, ErrorCategory.ServerError, "Circuit breaker is open");
        }

        var latestAttempt = await attemptRepo.GetLatestByContributionAsync(request.ContributionId, ct);
        var attemptNumber = (latestAttempt?.AttemptNumber ?? 0) + 1;
        var idempotencyKey = $"{request.ContributionId}-{attemptNumber}";

        var attempt = new ProcessingAttempt
        {
            Id = Guid.NewGuid(),
            ContributionId = request.ContributionId,
            OrganizationId = request.OrganizationId,
            AttemptNumber = attemptNumber,
            ProviderIdempotencyKey = idempotencyKey,
            Status = AttemptStatus.Pending,
            RequestPayload = JsonSerializer.Serialize(new { request.Amount, request.Currency, request.ExternalReference }),
            StartedAt = DateTime.UtcNow
        };
        await attemptRepo.AddAsync(attempt, ct);

        try
        {
            var providerRequest = new ProviderRequest(
                idempotencyKey,
                request.Amount,
                request.Currency,
                request.ExternalReference);

            var result = await provider.SubmitAsync(providerRequest, ct);

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

                await unitOfWork.SaveChangesAsync(ct);
                return new ProviderSubmissionResult(AttemptStatus.Succeeded, result.ProviderReference, null, null);
            }

            if (result.Status == ProviderStatus.Failed)
            {
                attempt.Status = AttemptStatus.Failed;
                attempt.ErrorCategory = result.ErrorCategory;
                attempt.ErrorMessage = result.ErrorMessage;
                circuitBreaker.RecordFailure(result.ErrorCategory);

                await unitOfWork.SaveChangesAsync(ct);
                return new ProviderSubmissionResult(AttemptStatus.Failed, null, result.ErrorCategory, result.ErrorMessage);
            }

            attempt.Status = AttemptStatus.Unknown;
            attempt.ErrorCategory = result.ErrorCategory;
            attempt.ErrorMessage = result.ErrorMessage;
            circuitBreaker.RecordFailure(result.ErrorCategory);

            await unitOfWork.SaveChangesAsync(ct);
            return new ProviderSubmissionResult(AttemptStatus.Unknown, null, result.ErrorCategory, result.ErrorMessage);
        }
        catch (Exception ex)
        {
            attempt.Status = AttemptStatus.Unknown;
            attempt.ErrorCategory = ErrorCategory.Timeout;
            attempt.ErrorMessage = ex.Message;
            attempt.CompletedAt = DateTime.UtcNow;
            circuitBreaker.RecordFailure(ErrorCategory.Timeout);

            await unitOfWork.SaveChangesAsync(ct);
            return new ProviderSubmissionResult(AttemptStatus.Unknown, null, ErrorCategory.Timeout, ex.Message);
        }
    }
}
