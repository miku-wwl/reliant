using Reliant.Domain.Enums;

namespace Reliant.Application.Abstractions;

public record ProviderRequest(
    string IdempotencyKey,
    decimal Amount,
    string Currency,
    string Reference,
    Dictionary<string, string>? Metadata = null);

public record ProviderResult(
    ProviderStatus Status,
    string? ProviderReference,
    ErrorCategory? ErrorCategory,
    string? ErrorMessage,
    string? RawResponse);

public record ProviderStatusResult(
    ProviderStatus Status,
    string? ProviderReference,
    string? ErrorMessage);

public record ProviderHealthResult(bool IsHealthy, string? Message);

public interface IProvider
{
    Task<ProviderResult> SubmitAsync(ProviderRequest request, CancellationToken ct = default);
    Task<ProviderStatusResult> QueryStatusByReferenceAsync(string providerReference, CancellationToken ct = default);
    Task<ProviderStatusResult> QueryStatusByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default);
    Task<ProviderResult> CancelAsync(string providerReference, CancellationToken ct = default);
    Task<ProviderHealthResult> CheckHealthAsync(CancellationToken ct = default);
}
