using Microsoft.AspNetCore.Mvc;
using MediatR;
using Reliant.Application.Dto;
using Reliant.Application.Campaigns.Commands;

namespace Reliant.Api.Controllers;

[ApiController]
[Route("api/organizations/{orgId}/campaigns")]
public class CampaignsController(ISender sender) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<CampaignResponse>> Create(
        [FromRoute] Guid orgId,
        [FromBody] CreateCampaignRequest request,
        CancellationToken ct)
    {
        var result = await sender.Send(new CreateCampaignCommand(request.Name, request.Description), ct);
        return CreatedAtAction(nameof(GetById), new { orgId, campaignId = result.Id }, result);
    }

    [HttpGet("{campaignId}")]
    public async Task<ActionResult<CampaignResponse>> GetById([FromRoute] Guid orgId, [FromRoute] Guid campaignId)
    {
        await Task.CompletedTask;
        return NotFound();
    }
}
