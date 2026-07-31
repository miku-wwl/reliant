using Microsoft.EntityFrameworkCore;
using Reliant.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Reliant.Tests.Integration.Fixtures;

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17")
        .WithDatabase("reliant_test")
        .WithUsername("reliant")
        .WithPassword("reliant-dev")
        .Build();

    public string ConnectionString => _container.GetConnectionString();
    public ReliantDbContext DbContext { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var options = new DbContextOptionsBuilder<ReliantDbContext>()
            .UseNpgsql(ConnectionString)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;

        DbContext = new ReliantDbContext(options);
        await DbContext.Database.EnsureDeletedAsync();
        await DbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await DbContext.DisposeAsync();
        await _container.DisposeAsync();
    }

    public async Task ResetAsync()
    {
        TenantFilterAccessor.Clear();

        // Drop any tracked entities from a previous test so the change tracker
        // never leaks rows into the freshly recreated database.
        DbContext.ChangeTracker.Clear();
        await DbContext.Database.EnsureDeletedAsync();
        await DbContext.Database.MigrateAsync();
    }
}
