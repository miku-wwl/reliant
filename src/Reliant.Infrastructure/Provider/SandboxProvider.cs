using Microsoft.Extensions.Configuration;
using Reliant.Application.Abstractions;
using Reliant.Domain.Enums;
using System.Collections.Concurrent;

namespace Reliant.Infrastructure.Provider;

public sealed class SandboxProviderOperation
{
    public required string IdempotencyKey { get; init; }
    public required string ProviderReference { get; init; }
    public required ProviderStatus Status { get; set; }
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }
    public required string ExternalReference { get; init; }
    public DateTime CreatedAt { get; init; }
}

public class SandboxProvider : IProvider
{
    private readonly string _secret;
    private readonly string _mode;
    private readonly ConcurrentDictionary<string, SandboxProviderOperation> _byKey = new();
    private readonly ConcurrentDictionary<string, SandboxProviderOperation> _byRef = new();

    public SandboxProvider(IConfiguration configuration)
    {
        _secret = configuration["Provider:Secret"] ?? "sandbox-secret-key";
        _mode = configuration["Provider:Mode"] ?? "Success";
    }

    public Task<ProviderResult> SubmitAsync(ProviderRequest request, CancellationToken ct = default)
    {
        if (_byKey.TryGetValue(request.IdempotencyKey, out var existing))
        {
            if (existing.Amount != request.Amount || existing.Currency != request.Currency)
            {
                return Task.FromResult(new ProviderResult(
                    ProviderStatus.Failed, existing.ProviderReference,
                    ErrorCategory.PermanentBusinessRejection,
                    "Idempotency key conflict: different payload",
                    null));
            }

            return Task.FromResult(new ProviderResult(
                existing.Status, existing.ProviderReference, null,
                "Idempotent replay", null));
        }

        var reference = $"ref_{Guid.NewGuid():N}"[..20];
        var operation = new SandboxProviderOperation
        {
            IdempotencyKey = request.IdempotencyKey,
            ProviderReference = reference,
            Status = ProviderStatus.Succeeded,
            Amount = request.Amount,
            Currency = request.Currency,
            ExternalReference = request.Reference,
            CreatedAt = DateTime.UtcNow
        };

        ProviderResult result;

        switch (_mode)
        {
            case "Success":
                _byKey[request.IdempotencyKey] = operation;
                _byRef[reference] = operation;
                result = new ProviderResult(ProviderStatus.Succeeded, reference, null, null,
                    $"{{\"status\":\"succeeded\",\"reference\":\"{reference}\"}}");
                break;

            case "ProcessedButResponseLost":
                _byKey[request.IdempotencyKey] = operation;
                _byRef[reference] = operation;
                throw new TaskCanceledException("Simulated timeout after processing");

            case "ConnectionResetAfterProcessing":
                _byKey[request.IdempotencyKey] = operation;
                _byRef[reference] = operation;
                throw new IOException("Simulated connection reset after processing");

            case "MalformedResponseAfterProcessing":
                _byKey[request.IdempotencyKey] = operation;
                _byRef[reference] = operation;
                result = new ProviderResult(ProviderStatus.Succeeded, reference, null, null,
                    "<<<malformed>>>");
                break;

            case "TimeoutBeforeProcessing":
                throw new TaskCanceledException("Simulated timeout before processing");

            case "Error5xxBeforeProcessing":
                result = new ProviderResult(ProviderStatus.Failed, null, ErrorCategory.ServerError,
                    "Simulated 500", "500 Internal Server Error");
                break;

            case "RateLimited":
                result = new ProviderResult(ProviderStatus.Failed, null, ErrorCategory.RateLimited,
                    "Simulated 429", "429 Too Many Requests");
                break;

            case "DefinitiveFailure":
                result = new ProviderResult(ProviderStatus.Failed, null, ErrorCategory.PermanentBusinessRejection,
                    "Provider rejected", "{\"status\":\"failed\",\"reason\":\"rejected\"}");
                break;

            case "PendingThenSuccess":
                operation.Status = ProviderStatus.Pending;
                _byKey[request.IdempotencyKey] = operation;
                _byRef[reference] = operation;
                result = new ProviderResult(ProviderStatus.Pending, reference, null,
                    "Provider is processing", null);
                break;

            default:
                _byKey[request.IdempotencyKey] = operation;
                _byRef[reference] = operation;
                result = new ProviderResult(ProviderStatus.Succeeded, reference, null, null,
                    $"{{\"status\":\"succeeded\",\"reference\":\"{reference}\"}}");
                break;
        }

        return Task.FromResult(result);
    }

    public Task<ProviderStatusResult> QueryStatusByReferenceAsync(string providerReference, CancellationToken ct = default)
    {
        if (_byRef.TryGetValue(providerReference, out var op))
        {
            if (_mode == "PendingThenSuccess" && op.Status == ProviderStatus.Pending)
            {
                op.Status = ProviderStatus.Succeeded;
            }

            return Task.FromResult(new ProviderStatusResult(op.Status, op.ProviderReference, null));
        }

        return Task.FromResult(new ProviderStatusResult(ProviderStatus.NotFound, null, "Reference not found"));
    }

    public Task<ProviderStatusResult> QueryStatusByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default)
    {
        if (_byKey.TryGetValue(idempotencyKey, out var op))
        {
            if (_mode == "PendingThenSuccess" && op.Status == ProviderStatus.Pending)
            {
                op.Status = ProviderStatus.Succeeded;
            }

            return Task.FromResult(new ProviderStatusResult(op.Status, op.ProviderReference, null));
        }

        return Task.FromResult(new ProviderStatusResult(ProviderStatus.NotFound, null, "Key not found"));
    }

    public Task<ProviderResult> CancelAsync(string providerReference, CancellationToken ct = default)
    {
        if (_byRef.TryGetValue(providerReference, out var op))
        {
            op.Status = ProviderStatus.Failed;
            return Task.FromResult(new ProviderResult(ProviderStatus.Failed, providerReference, null, "Cancelled", null));
        }

        return Task.FromResult(new ProviderResult(ProviderStatus.NotFound, null, ErrorCategory.ValidationFailure, "Reference not found", null));
    }

    public Task<ProviderHealthResult> CheckHealthAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new ProviderHealthResult(true, "Sandbox provider is healthy"));
    }

    public int OperationCount => _byKey.Count;

    public string ComputeSignature(string timestamp, string payload)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(timestamp + payload);
        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(_secret));
        var hash = hmac.ComputeHash(data);
        return System.Convert.ToHexString(hash).ToLowerInvariant();
    }

    public bool ValidateSignature(string signature, string timestamp, string payload)
    {
        var expected = ComputeSignature(timestamp, payload);
        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expected);
        var actualBytes = System.Text.Encoding.UTF8.GetBytes(signature);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }
}
