using Reliant.Infrastructure.Provider;

namespace Reliant.Tests.Unit;

[Trait("Category", "Unit")]
public class ProviderOperationKeyFactoryTests
{
    private readonly ProviderOperationKeyFactory _factory = new();
    private readonly Guid _orgId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly Guid _contributionId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void SameContribution_ShouldProduceSameKey()
    {
        var key1 = _factory.CreateContributionSubmitKey(_orgId, _contributionId, "sandbox");
        var key2 = _factory.CreateContributionSubmitKey(_orgId, _contributionId, "sandbox");
        Assert.Equal(key1, key2);
    }

    [Fact]
    public void DifferentContribution_ShouldProduceDifferentKey()
    {
        var otherContributionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var key1 = _factory.CreateContributionSubmitKey(_orgId, _contributionId, "sandbox");
        var key2 = _factory.CreateContributionSubmitKey(_orgId, otherContributionId, "sandbox");
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void DifferentProvider_ShouldProduceDifferentKey()
    {
        var key1 = _factory.CreateContributionSubmitKey(_orgId, _contributionId, "sandbox");
        var key2 = _factory.CreateContributionSubmitKey(_orgId, _contributionId, "stripe");
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void Key_ShouldNotContainAttemptNumber()
    {
        var key = _factory.CreateContributionSubmitKey(_orgId, _contributionId, "sandbox");
        Assert.DoesNotContain("attempt", key.ToLowerInvariant());
    }

    [Fact]
    public void Key_ShouldBeDeterministic()
    {
        var key1 = _factory.CreateContributionSubmitKey(_orgId, _contributionId, "sandbox");
        var key2 = _factory.CreateContributionSubmitKey(_orgId, _contributionId, "sandbox");
        Assert.Equal(key1, key2);
    }

    [Fact]
    public void Key_ShouldNotContainRandomGuid()
    {
        var key1 = _factory.CreateContributionSubmitKey(_orgId, _contributionId, "sandbox");
        var key2 = _factory.CreateContributionSubmitKey(_orgId, _contributionId, "sandbox");
        Assert.Equal(key1, key2);
    }

    [Fact]
    public void Key_ShouldContainProviderName()
    {
        var key = _factory.CreateContributionSubmitKey(_orgId, _contributionId, "sandbox");
        Assert.Contains("sandbox", key);
    }

    [Fact]
    public void Key_ShouldContainContributionId()
    {
        var key = _factory.CreateContributionSubmitKey(_orgId, _contributionId, "sandbox");
        Assert.Contains(_contributionId.ToString(), key);
    }
}
