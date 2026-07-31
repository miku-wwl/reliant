namespace Reliant.Application.Abstractions;

public interface IProviderOperationKeyFactory
{
    string CreateContributionSubmitKey(Guid organizationId, Guid contributionId, string providerName);
}
