using Microsoft.AspNetCore.Mvc;
using MediatR;
using System.Text.Json;
using Reliant.Infrastructure.Provider;

namespace Reliant.Api.Controllers;

[ApiController]
[Route("api/callbacks")]
public class CallbacksController(ISender sender, SandboxProvider provider, ILogger<CallbacksController> logger) : ControllerBase
{
    [HttpPost("provider")]
    public async Task<IActionResult> HandleCallback(CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var payload = await reader.ReadToEndAsync(ct);

        var signature = Request.Headers["X-Provider-Signature"].ToString();
        var timestamp = Request.Headers["X-Provider-Timestamp"].ToString();

        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(timestamp))
        {
            return Unauthorized(new ProblemDetails { Title = "Missing signature", Status = 401 });
        }

        if (!provider.ValidateSignature(signature, timestamp, payload))
        {
            logger.LogWarning("Invalid callback signature");
            return Unauthorized(new ProblemDetails { Title = "Invalid signature", Status = 401 });
        }

        if (DateTime.TryParse(timestamp, out var ts) && DateTime.UtcNow - ts > TimeSpan.FromMinutes(5))
        {
            return BadRequest(new ProblemDetails { Title = "Expired callback", Status = 400 });
        }

        logger.LogInformation("Callback received and validated");

        return Ok();
    }
}
