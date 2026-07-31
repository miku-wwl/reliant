using Testcontainers.LocalStack;

namespace Reliant.Tests.Integration.Fixtures;

public sealed class LocalStackFixture : IAsyncLifetime
{
    private readonly LocalStackContainer _container = new LocalStackBuilder()
        .WithImage("localstack/localstack:3")
        .Build();

    /// <summary>Base endpoint for LocalStack services, e.g. http://localhost:4566</summary>
    public string Endpoint => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}
