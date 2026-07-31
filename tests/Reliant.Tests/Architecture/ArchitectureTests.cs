using NetArchTest.Rules;

namespace Reliant.Tests.Architecture;

[Trait("Category", "Architecture")]
public class ArchitectureTests
{
    private const string DomainLayer = "Reliant.Domain";
    private const string ApplicationLayer = "Reliant.Application";
    private const string InfrastructureLayer = "Reliant.Infrastructure";

    [Fact]
    public void Domain_ShouldNotDependOn_Infrastructure()
    {
        var result = Types.InAssembly(typeof(Reliant.Domain.AssemblyMarker).Assembly)
            .ShouldNot()
            .HaveDependencyOn(InfrastructureLayer)
            .GetResult();

        Assert.True(result.IsSuccessful, GetFailureMessage(result));
    }

    [Fact]
    public void Domain_ShouldNotDependOn_AspNetCore()
    {
        var result = Types.InAssembly(typeof(Reliant.Domain.AssemblyMarker).Assembly)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.AspNetCore")
            .GetResult();

        Assert.True(result.IsSuccessful, GetFailureMessage(result));
    }

    [Fact]
    public void Domain_ShouldNotDependOn_EntityFrameworkCore()
    {
        var result = Types.InAssembly(typeof(Reliant.Domain.AssemblyMarker).Assembly)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(result.IsSuccessful, GetFailureMessage(result));
    }

    [Fact]
    public void Application_ShouldNotDependOn_Infrastructure()
    {
        var result = Types.InAssembly(typeof(Reliant.Application.AssemblyMarker).Assembly)
            .ShouldNot()
            .HaveDependencyOn(InfrastructureLayer)
            .GetResult();

        Assert.True(result.IsSuccessful, GetFailureMessage(result));
    }

    [Fact]
    public void Application_ShouldNotDependOn_AspNetCore()
    {
        var result = Types.InAssembly(typeof(Reliant.Application.AssemblyMarker).Assembly)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.AspNetCore")
            .GetResult();

        Assert.True(result.IsSuccessful, GetFailureMessage(result));
    }

    private static string GetFailureMessage(TestResult result)
    {
        if (result.IsSuccessful) return string.Empty;
        var failing = result.FailingTypeNames;
        return failing != null && failing.Any()
            ? $"Failing types: {string.Join(", ", failing)}"
            : "Architecture rule violated";
    }
}
