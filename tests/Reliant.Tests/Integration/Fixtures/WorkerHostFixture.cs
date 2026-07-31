using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Reliant.Application;
using Reliant.Infrastructure;
using Reliant.Infrastructure.Persistence;
using Reliant.Worker.Handlers;
using Reliant.Worker.Scheduling;
using Testcontainers.LocalStack;
using Testcontainers.PostgreSql;

namespace Reliant.Tests.Integration.Fixtures;

/// <summary>
/// A real worker host wired to PostgreSQL Testcontainer + LocalStack SQS.
/// Starting it launches the Outbox Publisher, Processing Handler, Reconciliation
/// Handler and Scheduled Maintenance services so tests exercise the actual
/// Outbox -> SQS -> Worker -> Provider -> Reconciliation pipeline.
/// </summary>
public sealed class WorkerHostFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder()
        .WithImage("postgres:17")
        .WithDatabase("reliant_e2e")
        .WithUsername("reliant")
        .WithPassword("reliant-dev")
        .Build();

    private readonly LocalStackContainer _localStack = new LocalStackBuilder()
        .WithImage("localstack/localstack:3")
        .Build();

    private IHost? _host;

    public string PgConnectionString => _pg.GetConnectionString();
    public string SqsEndpoint => _localStack.GetConnectionString();
    public IHost Host => _host ?? throw new InvalidOperationException("Worker host not started");

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        await _localStack.StartAsync();

        var options = new DbContextOptionsBuilder<ReliantDbContext>()
            .UseNpgsql(_pg.GetConnectionString())
            .Options;
        await using var db = new ReliantDbContext(options);
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
    }

    public async Task StartWorkersAsync(string providerMode = "Success", bool includeProcessing = true, bool includeReconciliation = true)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { Args = Array.Empty<string>() });

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PostgreSQL"] = _pg.GetConnectionString(),
            ["Queue:Endpoint"] = _localStack.GetConnectionString(),
            ["Queue:Region"] = "us-west-1",
            ["Provider:Mode"] = providerMode,
            ["Provider:Secret"] = "sandbox-secret-key",
            ["Worker:Outbox:IntervalMs"] = "300",
            ["Worker:Reconciliation:IntervalMs"] = "300",
            ["Worker:Maintenance:IntervalMs"] = "300"
        });

        builder.Services.AddReliantApplication();
        builder.Services.AddReliantInfrastructure(builder.Configuration);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<IRetryScheduler, RetrySchedulerService>();
        builder.Services.AddHostedService<OutboxPublisherService>();
        if (includeProcessing) builder.Services.AddHostedService<ProcessingHandlerService>();
        if (includeReconciliation) builder.Services.AddHostedService<ReconciliationHandlerService>();
        builder.Services.AddHostedService<ScheduledMaintenanceHandlerService>();

        _host = builder.Build();
        await _host.StartAsync();
    }

    public async Task StopWorkersAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(10));
            _host.Dispose();
            _host = null;
        }
    }

    public async Task DisposeAsync()
    {
        await StopWorkersAsync();
        await _pg.DisposeAsync();
        await _localStack.DisposeAsync();
    }
}
