using Microsoft.Extensions.Configuration;
using Reliant.Application.Abstractions;
using Reliant.Domain.Enums;
using System.Security.Cryptography;
using System.Text;

namespace Reliant.Infrastructure.Provider;

public class SandboxProvider : IProvider
{
    private readonly string _secret;
    private readonly string _mode;
    private readonly Dictionary<string, ProviderStatus> _referenceStore = new();
    private readonly Dictionary<string, string> _idempotencyStore = new();
    private readonly object _lock = new();

    public SandboxProvider(IConfiguration configuration)
    {
        _secret = configuration["Provider:Secret"] ?? "sandbox-secret-key";
        _mode = configuration["Provider:Mode"] ?? "Success";
    }

    public Task<ProviderResult> SubmitAsync(ProviderRequest request, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_idempotencyStore.TryGetValue(request.IdempotencyKey, out var existingRef))
            {
                return Task.FromResult(new ProviderResult(
                    ProviderStatus.Succeeded,
                    existingRef,
                    null,
                    "Idempotent replay",
                    null));
            }

            var result = _mode switch
            {
                "Success" => CreateSuccess(request),
                "Timeout" => throw new TaskCanceledException("Simulated timeout"),
                "Error5xx" => new ProviderResult(ProviderStatus.Failed, null, ErrorCategory.ServerError, "Simulated 500", "500 Internal Server Error"),
                "Error429" => new ProviderResult(ProviderStatus.Failed, null, ErrorCategory.RateLimited, "Simulated 429", "429 Too Many Requests"),
                "SlowResponse" => Task.Delay(35000, ct).ContinueWith(_ => CreateSuccess(request), ct).Result,
                _ => CreateSuccess(request)
            };

            if (result.Status == ProviderStatus.Succeeded && result.ProviderReference is not null)
            {
                _idempotencyStore[request.IdempotencyKey] = result.ProviderReference;
                _referenceStore[result.ProviderReference] = ProviderStatus.Succeeded;
            }

            return Task.FromResult(result);
        }
    }

    public Task<ProviderStatusResult> QueryStatusAsync(string providerReference, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_referenceStore.TryGetValue(providerReference, out var status))
            {
                return Task.FromResult(new ProviderStatusResult(status, providerReference, null));
            }

            return Task.FromResult(new ProviderStatusResult(ProviderStatus.NotFound, null, "Reference not found"));
        }
    }

    public Task<ProviderResult> CancelAsync(string providerReference, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_referenceStore.ContainsKey(providerReference))
            {
                _referenceStore[providerReference] = ProviderStatus.Failed;
                return Task.FromResult(new ProviderResult(ProviderStatus.Failed, providerReference, null, "Cancelled", null));
            }

            return Task.FromResult(new ProviderResult(ProviderStatus.NotFound, null, ErrorCategory.ValidationFailure, "Reference not found", null));
        }
    }

    public Task<ProviderHealthResult> CheckHealthAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new ProviderHealthResult(true, "Sandbox provider is healthy"));
    }

    public string ComputeSignature(string timestamp, string payload)
    {
        var data = Encoding.UTF8.GetBytes(timestamp + payload);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_secret));
        var hash = hmac.ComputeHash(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public bool ValidateSignature(string signature, string timestamp, string payload)
    {
        var expected = ComputeSignature(timestamp, payload);
        return signature == expected;
    }

    private static ProviderResult CreateSuccess(ProviderRequest request)
    {
        var reference = $"ref_{Guid.NewGuid():N}"[..20];
        return new ProviderResult(ProviderStatus.Succeeded, reference, null, null, $"{{\"status\":\"succeeded\",\"reference\":\"{reference}\"}}");
    }
}
