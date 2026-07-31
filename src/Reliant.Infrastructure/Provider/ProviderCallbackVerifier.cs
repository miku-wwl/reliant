using Microsoft.Extensions.Configuration;
using Reliant.Application.Abstractions;
using System.Security.Cryptography;
using System.Text;

namespace Reliant.Infrastructure.Provider;

public class ProviderCallbackVerifier(IConfiguration configuration) : IProviderCallbackVerifier
{
    private readonly string _secret = configuration["Provider:Secret"] ?? "sandbox-secret-key";
    private const int MaxClockSkewMinutes = 5;

    public CallbackVerificationResult Verify(string signature, string timestamp, string payload)
    {
        if (string.IsNullOrEmpty(signature))
            return new CallbackVerificationResult(false, "Missing signature");

        if (string.IsNullOrEmpty(timestamp))
            return new CallbackVerificationResult(false, "Missing timestamp");

        if (!DateTime.TryParse(timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind, out var ts))
            return new CallbackVerificationResult(false, "Invalid timestamp format");

        var now = DateTime.UtcNow;
        if (Math.Abs((now - ts).TotalMinutes) > MaxClockSkewMinutes)
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
