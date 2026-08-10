using Reliant.Tests.Integration.Phase2.Exp7;
using Xunit.Abstractions;

namespace Reliant.Tests.Integration.Phase3.Exp13;

[Trait("Category", "Integration")]
[Trait("Dependency", "PostgreSQL")]
[Trait("Dependency", "LocalStack")]
[Trait("Dependency", "WorkerHost")]
public sealed class RetryExhaustionEvidenceE2ETests(
    ITestOutputHelper output)
{
    [Fact]
    public Task SafeRetryBudget_ShouldExhaustAndRemainTerminal()
        => RetryExhaustionE2ETests.RunScenarioAsync(output);
}
