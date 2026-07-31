using Microsoft.Extensions.Configuration;
using Reliant.Application.Abstractions;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Reliant.Infrastructure.Provider;

public class ProviderCallbackVerifier(IConfiguration configuration, TimeProvider timeProvider) : IProviderCallbackVerifier
{
    private readonly string _secret = configuration["Provider:Secret"] ?? "sandbox-secret-key";
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    public CallbackVerificationResult Verify(string signature, string timestamp, string payload)
    {
        if (string.IsNullOrEmpty(signature))
            return new CallbackVerificationResult(false, "Missing signature");

        if (string.IsNullOrEmpty(timestamp))
            return new CallbackVerificationResult(false, "Missing timestamp");

        // Strict UTC ISO-8601 round-trip: parseable AND an explicit UTC (Z)
        // offset. Local times without an offset are rejected deterministically.
        if (!DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts))
            return new CallbackVerificationResult(false, "Invalid timestamp format");

        if (!timestamp.EndsWith("Z", StringComparison.OrdinalIgnoreCase) || ts.Offset != TimeSpan.Zero)
            return new CallbackVerificationResult(false, "Timestamp must be UTC (ISO-8601 with Z)");

        var now = timeProvider.GetUtcNow();
        var elapsed = now - ts;
        if (elapsed < -MaxClockSkew || elapsed > MaxClockSkew)
            return new CallbackVerificationResult(false, "Timestamp outside allowed window");

        var expected = ComputeSignature(timestamp, payload);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(signature);

        if (!CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes))
            return new CallbackVerificationResult(false, "Invalid signature");

        return new CallbackVerificationResult(true, null);
    }

    private string ComputeSignature(string timestamp, string payload)
    {
        var data = Encoding.UTF8.GetBytes(timestamp + payload);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_secret));
        var hash = hmac.ComputeHash(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
