using Reliant.Application.Abstractions;
using Reliant.Application.Dto;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using MediatR;
using System.Diagnostics;
using System.Text.Json;

namespace Reliant.Application.Contributions.Commands;

public record CreateContributionCommand(
    Guid CampaignId,
    string ExternalReference,
    decimal Amount,
    string Currency,
    string IdempotencyKey) : IRequest<IdempotentResponse<ContributionResponse>>;

public class CreateContributionHandler(
    IContributionRepository contributionRepository,
    ICampaignRepository campaignRepository,
    IIdempotencyRepository idempotencyRepository,
    IStateTransitionRepository stateTransitionRepository,
    IAuditEventRepository auditEventRepository,
    IOutboxRepository outboxRepository,
    IJobRunRepository jobRunRepository,
    IUnitOfWork unitOfWork,
    ITenantContext tenantContext) : IRequestHandler<CreateContributionCommand, IdempotentResponse<ContributionResponse>>
{
    public async Task<IdempotentResponse<ContributionResponse>> Handle(
        CreateContributionCommand request,
        CancellationToken cancellationToken)
    {
        var existing = await idempotencyRepository.GetByKeyAsync(
            tenantContext.OrganizationId,
            request.IdempotencyKey,
            cancellationToken);

        if (existing is not null)
        {
            if (existing.ContributionId.HasValue)
            {
                var cachedContribution = await contributionRepository.GetByIdAsync(
                    existing.ContributionId.Value,
                    cancellationToken);

                if (cachedContribution is not null)
                {
                    return new IdempotentResponse<ContributionResponse>(
                        existing.ResponseStatus ?? 201,
                        MapToResponse(cachedContribution),
                        WasCached: true);
                }
            }

            return new IdempotentResponse<ContributionResponse>(
                existing.ResponseStatus ?? 201,
                null,
                WasCached: true);
        }

        var campaign = await campaignRepository.GetByIdAsync(request.CampaignId, cancellationToken);
        if (campaign is null || campaign.Status != CampaignStatus.Active)
        {
            throw new InvalidOperationException("Campaign not found or not active");
        }

        var contribution = new Contribution
        {
            Id = Guid.NewGuid(),
            OrganizationId = tenantContext.OrganizationId,
            CampaignId = request.CampaignId,
            ExternalReference = request.ExternalReference,
            Amount = request.Amount,
            Currency = request.Currency,
            State = ContributionState.Created,
            Version = 0
        };

        var idempotencyRecord = new IdempotencyRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = tenantContext.OrganizationId,
            IdempotencyKey = request.IdempotencyKey,
            ContributionId = contribution.Id,
            RequestHash = $"{request.CampaignId}:{request.ExternalReference}:{request.Amount}:{request.Currency}",
            ResponseStatus = 201,
            ResponseBody = null,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(24)
        };

        var stateTransition = new StateTransition
        {
            Id = Guid.NewGuid(),
            ContributionId = contribution.Id,
            FromState = ContributionState.Created,
            ToState = ContributionState.Created,
            Reason = "Contribution created",
            ChangedBy = tenantContext.UserId?.ToString() ?? "system"
        };

        var correlationId = tenantContext.CorrelationId ??
            Activity.Current?.TraceId.ToString() ??
            Guid.NewGuid().ToString();
        var auditEvent = new AuditEvent
        {
            Id = Guid.NewGuid(),
            OrganizationId = tenantContext.OrganizationId,
            EntityType = "Contribution",
            EntityId = contribution.Id,
            Action = "Create",
            ChangedBy = tenantContext.UserId?.ToString() ?? "system",
            CorrelationId = correlationId,
            NewValues = $"Amount:{contribution.Amount} {contribution.Currency}"
        };

        await contributionRepository.AddAsync(contribution, cancellationToken);
        await idempotencyRepository.AddAsync(idempotencyRecord, cancellationToken);
        await stateTransitionRepository.AddAsync(stateTransition, cancellationToken);
        await auditEventRepository.AddAsync(auditEvent, cancellationToken);

        var outboxMessage = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            OrganizationId = tenantContext.OrganizationId,
            MessageType = "ContributionCreated",
            Payload = JsonSerializer.Serialize(new ContributionProcessingMessage(
                Version: 1,
                ContributionId: contribution.Id,
                OrganizationId: contribution.OrganizationId,
                Trigger: "Created",
                CorrelationId: correlationId)),
            CorrelationId = correlationId,
            TraceParent = Activity.Current?.Id,
            TraceState = Activity.Current?.TraceStateString,
            OccurredAt = DateTime.UtcNow,
            Status = OutboxStatus.Pending,
            Version = 0
        };
        await outboxRepository.AddAsync(outboxMessage, cancellationToken);
        await jobRunRepository.AddAsync(
            JobRun.ForContributionProcessing(outboxMessage),
            cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new IdempotentResponse<ContributionResponse>(201, MapToResponse(contribution), WasCached: false);
    }

    private static ContributionResponse MapToResponse(Contribution c) => new(
        c.Id,
        c.OrganizationId,
        c.CampaignId,
        c.ExternalReference,
        c.Amount,
        c.Currency,
        c.State.ToString(),
        c.CreatedAt,
        c.UpdatedAt,
        c.Version);
}
