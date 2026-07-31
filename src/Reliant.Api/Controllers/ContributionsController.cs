using Microsoft.AspNetCore.Mvc;
using MediatR;
using Reliant.Application.Dto;
using Reliant.Application.Contributions.Commands;
using Reliant.Application.Contributions.Queries;

namespace Reliant.Api.Controllers;

[ApiController]
[Route("api/organizations/{orgId}/contributions")]
public class ContributionsController(ISender sender) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ContributionResponse>> Create(
        [FromRoute] Guid orgId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] CreateContributionRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(idempotencyKey))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Missing Idempotency-Key",
                Status = 400,
                Detail = "Idempotency-Key header is required"
            });
        }

        var command = new CreateContributionCommand(
            request.CampaignId,
            request.ExternalReference,
            request.Amount,
            request.Currency,
            idempotencyKey);

        var result = await sender.Send(command, ct);

        Response.Headers["Idempotent-Replay"] = result.WasCached.ToString();

        if (result.WasCached && result.StatusCode == 201 && result.Body is not null)
        {
            return Ok(result.Body);
        }

        return result.Body is not null
            ? CreatedAtAction(nameof(GetById), new { orgId, contributionId = result.Body.Id }, result.Body)
            : BadRequest();
    }

    [HttpGet("{contributionId}")]
    public async Task<ActionResult<ContributionResponse>> GetById(
        [FromRoute] Guid orgId,
        [FromRoute] Guid contributionId,
        CancellationToken ct)
    {
        var result = await sender.Send(new GetContributionQuery(contributionId), ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet]
    public async Task<ActionResult<ListResponse<ContributionResponse>>> List(
        [FromRoute] Guid orgId,
        [FromQuery] int limit = 20,
        [FromQuery] string? cursor = null,
        CancellationToken ct = default)
    {
        var result = await sender.Send(new ListContributionsQuery(limit, cursor), ct);
        return Ok(result);
    }
}
