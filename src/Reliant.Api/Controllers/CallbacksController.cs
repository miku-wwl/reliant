using Microsoft.AspNetCore.Mvc;
using MediatR;
using Reliant.Application.Abstractions;
using Reliant.Application.Contributions.Commands;
using Reliant.Application.Observability;
using System.Diagnostics;
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
        var started = Stopwatch.GetTimestamp();
        IActionResult Complete(IActionResult response, string result)
        {
            ReliantTelemetry.RecordCallback(
                result,
                Stopwatch.GetElapsedTime(started));
            return response;
        }

        using var reader = new StreamReader(Request.Body);
        var payload = await reader.ReadToEndAsync(ct);

        var signature = Request.Headers["X-Provider-Signature"].ToString();
        var timestamp = Request.Headers["X-Provider-Timestamp"].ToString();

        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(timestamp))
        {
            var reason = string.IsNullOrEmpty(timestamp)
                ? "Missing timestamp"
                : "Missing signature";
            ReliantTelemetry.RecordCallbackVerificationFailure(reason);
            return Complete(
                Unauthorized(new ProblemDetails { Title = "Missing signature or timestamp", Status = 401 }),
                "rejected");
        }

        var verification = verifier.Verify(signature, timestamp, payload);
        if (!verification.IsValid)
        {
            logger.LogWarning("Callback verification failed: {Error}", verification.Error);
            ReliantTelemetry.RecordCallbackVerificationFailure(
                verification.Error ?? "Verification failed");
            return Complete(
                Unauthorized(new ProblemDetails { Title = verification.Error ?? "Verification failed", Status = 401 }),
                "rejected");
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
            return Complete(
                BadRequest(new ProblemDetails { Title = "Invalid payload", Status = 400 }),
                "rejected");
        }

        if (callbackPayload is null)
        {
            return Complete(
                BadRequest(new ProblemDetails { Title = "Empty payload", Status = 400 }),
                "rejected");
        }

        var result = await sender.Send(new HandleProviderCallbackCommand(callbackPayload), ct);

        var response = result.StatusCode switch
        {
            200 => Ok(new { message = result.Message }),
            400 => BadRequest(new ProblemDetails { Title = result.Message, Status = 400 }),
            _ => StatusCode(result.StatusCode, new ProblemDetails { Title = result.Message, Status = result.StatusCode })
        };
        return Complete(
            response,
            result.StatusCode == 200 ? "success" : "failure");
    }
}
