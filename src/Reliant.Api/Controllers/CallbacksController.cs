using Microsoft.AspNetCore.Mvc;
using MediatR;
using Reliant.Application.Abstractions;
using Reliant.Application.Contributions.Commands;
using System.Text.Json;

namespace Reliant.Api.Controllers;

[ApiController]
[Route("api/callbacks")]
public class CallbacksController(
    IProviderCallbackVerifier verifier,
    ISender sender,
    ILogger<CallbacksController> logger) : ControllerBase
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
            return Unauthorized(new ProblemDetails { Title = "Missing signature or timestamp", Status = 401 });
        }

        var verification = verifier.Verify(signature, timestamp, payload);
        if (!verification.IsValid)
        {
            logger.LogWarning("Callback verification failed: {Error}", verification.Error);
            return Unauthorized(new ProblemDetails { Title = verification.Error ?? "Verification failed", Status = 401 });
        }

        ProviderCallbackPayload? callbackPayload;
        try
        {
            callbackPayload = JsonSerializer.Deserialize<ProviderCallbackPayload>(payload, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse callback payload");
            return BadRequest(new ProblemDetails { Title = "Invalid payload", Status = 400 });
        }

        if (callbackPayload is null)
        {
            return BadRequest(new ProblemDetails { Title = "Empty payload", Status = 400 });
        }

        var result = await sender.Send(new HandleProviderCallbackCommand(callbackPayload), ct);

        return result.StatusCode switch
        {
            200 => Ok(new { message = result.Message }),
            400 => BadRequest(new ProblemDetails { Title = result.Message, Status = 400 }),
            _ => StatusCode(result.StatusCode, new ProblemDetails { Title = result.Message, Status = result.StatusCode })
        };
    }
}
