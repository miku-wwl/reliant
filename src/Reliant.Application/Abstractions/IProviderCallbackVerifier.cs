using Reliant.Domain.Enums;

namespace Reliant.Application.Abstractions;

public interface IProviderCallbackVerifier
{
    CallbackVerificationResult Verify(string signature, string timestamp, string payload);
}

public record CallbackVerificationResult(bool IsValid, string? Error);

public record ProviderCallbackPayload(
    string EventId,
    string EventType,
    string? ProviderReference,
    string? IdempotencyKey,
    string Status,
    string? OccurredAt,
    int Version);
