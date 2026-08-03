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

public class SandboxProvider : IProvider, ISandboxProviderControl
{
    private readonly string _secret;
    private readonly int _submitDelayMs;
    private volatile string _mode;
    private readonly ConcurrentDictionary<string, SandboxProviderOperation> _byKey = new();
    private readonly ConcurrentDictionary<string, SandboxProviderOperation> _byRef = new();

    public SandboxProvider(IConfiguration configuration)
    {
        _secret = configuration["Provider:Secret"] ?? "sandbox-secret-key";
        _mode = configuration["Provider:Mode"] ?? "Success";
        _submitDelayMs = Math.Max(
            0,
            int.TryParse(
                configuration["Provider:SubmitDelayMs"],
                out var parsedSubmitDelayMs)
                ? parsedSubmitDelayMs
                : 0);
    }

    public void SetMode(string mode)
    {
        _mode = mode;
    }

    public async Task<ProviderResult> SubmitAsync(
        ProviderRequest request,
        CancellationToken ct = default)
    {
        if (_submitDelayMs > 0)
        {
            await Task.Delay(_submitDelayMs, ct);
        }

        // Fast path: existing operation -> idempotent replay.
        if (_byKey.TryGetValue(request.IdempotencyKey, out var existing))
        {
            return ReplayResult(request, existing);
        }

        // Modes where the provider does NOT process the request: no operation is
        // ever created, so the request can be safely retried later.
        if (NoProcessModes.Contains(_mode))
        {
            return NoProcessResult(request);
        }

        // Atomic create-or-get: exactly one operation object is created per key,
        // even under concurrent submission, so there can never be a second
        // provider-side business effect for the same idempotency key.
        var candidate = BuildOperation(request);
        var operation = _byKey.GetOrAdd(request.IdempotencyKey, candidate);
        var created = ReferenceEquals(operation, candidate);

        if (created)
        {
            _byRef[operation.ProviderReference] = operation;
            return FirstCallResult(request, operation);
        }

        // Lost the race - replay the operation that the winner created.
        return ReplayResult(request, operation);
    }

    private static readonly HashSet<string> NoProcessModes = new(StringComparer.Ordinal)
    {
        "TimeoutBeforeProcessing",
        "Error5xxBeforeProcessing",
        "RateLimited",
        "DefinitiveFailure"
    };

    private SandboxProviderOperation BuildOperation(ProviderRequest request)
    {
        var reference = $"ref_{Guid.NewGuid():N}"[..20];
        return new SandboxProviderOperation
        {
            IdempotencyKey = request.IdempotencyKey,
            ProviderReference = reference,
            Status = ProviderStatus.Succeeded,
            Amount = request.Amount,
            Currency = request.Currency,
            ExternalReference = request.Reference,
            CreatedAt = DateTime.UtcNow
        };
    }

    private ProviderResult FirstCallResult(ProviderRequest request, SandboxProviderOperation operation)
    {
        switch (_mode)
        {
            case "ProcessedButResponseLost":
                throw new TaskCanceledException("Simulated timeout after processing");

            case "ConnectionResetAfterProcessing":
                throw new IOException("Simulated connection reset after processing");

            case "MalformedResponseAfterProcessing":
                return new ProviderResult(ProviderStatus.Succeeded, operation.ProviderReference, null, null,
                    "<<<malformed>>>");

            case "PendingThenSuccess":
            case "PendingForever":
                operation.Status = ProviderStatus.Pending;
                return new ProviderResult(ProviderStatus.Pending, operation.ProviderReference, null,
                    "Provider is processing", null);

            default: // Success and anything unhandled
                return new ProviderResult(ProviderStatus.Succeeded, operation.ProviderReference, null, null,
                    $"{{\"status\":\"succeeded\",\"reference\":\"{operation.ProviderReference}\"}}");
        }
    }

    private ProviderResult NoProcessResult(ProviderRequest request)
    {
        switch (_mode)
        {
            case "TimeoutBeforeProcessing":
                throw new TaskCanceledException("Simulated timeout before processing");

            case "Error5xxBeforeProcessing":
                return new ProviderResult(ProviderStatus.Failed, null, ErrorCategory.ServerError,
                    "Simulated 500", "500 Internal Server Error");

            case "RateLimited":
                return new ProviderResult(ProviderStatus.Failed, null, ErrorCategory.RateLimited,
                    "Simulated 429", "429 Too Many Requests");

            default: // DefinitiveFailure
                return new ProviderResult(ProviderStatus.Failed, null, ErrorCategory.PermanentBusinessRejection,
                    "Provider rejected", "{\"status\":\"failed\",\"reason\":\"rejected\"}");
        }
    }

    private ProviderResult ReplayResult(ProviderRequest request, SandboxProviderOperation existing)
    {
        // Same key but a different payload is an idempotency conflict - never
        // silently reuse the earlier result.
        if (existing.Amount != request.Amount || existing.Currency != request.Currency ||
            existing.ExternalReference != request.Reference)
        {
            return new ProviderResult(
                ProviderStatus.Failed, existing.ProviderReference,
                ErrorCategory.PermanentBusinessRejection,
                "Idempotency key conflict: different payload", null);
        }

        if (_mode == "PendingThenSuccess" && existing.Status == ProviderStatus.Pending)
        {
            existing.Status = ProviderStatus.Succeeded;
        }

        return new ProviderResult(
            existing.Status, existing.ProviderReference, null,
            "Idempotent replay", null);
    }

    public Task<ProviderStatusResult> QueryStatusByReferenceAsync(string providerReference, CancellationToken ct = default)
    {
        if (_mode == "QueryUnavailable")
        {
            throw new TaskCanceledException("Simulated provider query timeout");
        }

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
        if (_mode == "QueryUnavailable")
        {
            throw new TaskCanceledException("Simulated provider query timeout");
        }

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
