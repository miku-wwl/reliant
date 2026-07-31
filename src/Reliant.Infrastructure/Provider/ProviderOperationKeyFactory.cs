using System.Security.Cryptography;
using System.Text;
using Reliant.Application.Abstractions;

namespace Reliant.Infrastructure.Provider;

public class ProviderOperationKeyFactory : IProviderOperationKeyFactory
{
    public string CreateContributionSubmitKey(Guid organizationId, Guid contributionId, string providerName)
    {
        var raw = $"reliant:{providerName.ToLowerInvariant()}:org:{organizationId}:contribution:{contributionId}:submit:v1";
        if (raw.Length <= 128) return raw;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return $"reliant:{providerName.ToLowerInvariant()}:{Convert.ToHexString(hash).ToLowerInvariant()[..32]}:v1";
    }
}
