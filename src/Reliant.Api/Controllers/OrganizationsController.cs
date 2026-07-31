using Microsoft.AspNetCore.Mvc;
using MediatR;
using Reliant.Application.Dto;
using Reliant.Application.Organizations.Commands;

namespace Reliant.Api.Controllers;

[ApiController]
[Route("api/organizations")]
public class OrganizationsController(ISender sender) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<OrganizationResponse>> Create([FromBody] CreateOrganizationRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new CreateOrganizationCommand(request.Name, request.OwnerEmail, request.OwnerExternalId), ct);
        return CreatedAtAction(nameof(GetById), new { orgId = result.Id }, result);
    }

    [HttpGet("{orgId}")]
    public async Task<ActionResult<OrganizationResponse>> GetById([FromRoute] Guid orgId)
    {
        await Task.CompletedTask;
        return NotFound();
    }
}
