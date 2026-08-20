using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Reliant.Application;
using Reliant.Application.Abstractions;
using Reliant.Application.Messaging;
using Reliant.Infrastructure;
using Reliant.Infrastructure.Persistence;
using Reliant.Infrastructure.Observability;
using Reliant.Worker.Handlers;
using Reliant.Worker.Scheduling;
using System.Collections.Concurrent;
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

    private readonly InMemoryLoggerProvider _loggerProvider = new();
    private IHost? _host;

    public string PgConnectionString => _pg.GetConnectionString();
    public string SqsEndpoint => _localStack.GetConnectionString();

    /// <summary>
    /// LocalStack persists SQS state in a shared volume that survives container
    /// disposal, so queues left by a previous fixture in the same test process leak
    /// into this one. A per-instance queue name guarantees the workers only ever see
    /// their own messages regardless of LocalStack persistence.
    /// </summary>
    public string QueueName { get; } = $"reliant-processing-{Guid.NewGuid():N}";

    public IHost Host => _host ?? throw new InvalidOperationException("Worker host not started");

    // Pausing makes the broker unreachable while preserving the container's
    // host-port binding. A stop/start cycle can receive a new random host
    // port in Testcontainers, which is not representative of a production
    // broker endpoint and would leave the already-built worker host pointing
    // at a stale test-only URL.
    public Task StopBrokerAsync(
        CancellationToken cancellationToken = default)
        => _localStack.PauseAsync(cancellationToken);

    public Task StartBrokerAsync(
        CancellationToken cancellationToken = default)
        => _localStack.UnpauseAsync(cancellationToken);

    /// <summary>Recent worker log lines (for failure diagnostics).</summary>
    public IReadOnlyList<string> LogLines => _loggerProvider.Lines.ToArray();

    /// <summary>Last <paramref name="count"/> log lines, oldest first.</summary>
    public string RecentLogs(int count = 40)
        => string.Join(Environment.NewLine, _loggerProvider.Lines.TakeLast(count));

    /// <summary>Minimal in-memory logger provider so tests can inspect worker activity.</summary>
    private sealed class InMemoryLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Lines);
        public void Dispose() { }

        private sealed class Logger(string categoryName, ConcurrentQueue<string> lines) : ILogger
        {
            private static readonly string[] _noisePrefixes =
            [
                "Microsoft.EntityFrameworkCore",
                "Microsoft.Hosting",
                "Microsoft.Extensions.Hosting"
            ];

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                // Skip EF/hosting noise so the buffer holds only worker-relevant logs.
                if (_noisePrefixes.Any(p => categoryName.StartsWith(p, StringComparison.Ordinal)))
                    return;

                var line = $"{DateTime.UtcNow:HH:mm:ss.fff} [{logLevel}] {categoryName}: {formatter(state, exception)}";
                if (exception is not null) line += $" :: {exception.GetType().Name}: {exception.Message}";
                while (lines.Count > 5000) lines.TryDequeue(out _);
                lines.Enqueue(line);
            }
        }
    }

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

    public async Task StartWorkersAsync(
        string providerMode = "Success",
        bool includeProcessing = true,
        bool includeReconciliation = true,
        IWorkerFaultInjector? faultInjector = null,
        int visibilityTimeoutSeconds = 35,
        IQueueAdapter? queueAdapterOverride = null,
        IInterceptor? dbInterceptor = null,
        ILeaseRepository? leaseRepositoryOverride = null,
        IJobRunRepository? jobRunRepositoryOverride = null,
        IJobAttemptRepository? jobAttemptRepositoryOverride = null,
        int maxReceiveCount = 5,
        int outboxRetryBaseMs = 1000,
        int outboxRetryCapMs = 30000,
        int outboxRetryJitterMs = 250,
        int queueRequestTimeoutSeconds = 5,
        int queuePublishTimeoutSeconds = 5,
        int queueMaxErrorRetry = 1,
        int leaseSeconds = 30,
        int heartbeatIntervalMs = 10000,
        int processingConcurrency = 10,
        int providerSubmitDelayMs = 0,
        CircuitBreaker? circuitBreakerOverride = null,
        IOperationalHistoryCleanupFaultInjector?
            cleanupFaultInjectorOverride = null,
        IReadOnlyDictionary<string, string?>?
            configurationOverrides = null,
        bool includeOutboxPublisher = true)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { Args = Array.Empty<string>() });

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PostgreSQL"] = _pg.GetConnectionString(),
            ["Queue:Endpoint"] = _localStack.GetConnectionString(),
            ["Queue:Region"] = "us-west-1",
            ["Queue:QueueName"] = QueueName,
            ["Queue:MaxReceiveCount"] = maxReceiveCount.ToString(),
            ["Queue:RequestTimeoutSeconds"] =
                queueRequestTimeoutSeconds.ToString(),
            ["Queue:PublishTimeoutSeconds"] =
                queuePublishTimeoutSeconds.ToString(),
            ["Queue:MaxErrorRetry"] =
                queueMaxErrorRetry.ToString(),
            ["Provider:Mode"] = providerMode,
            ["Provider:Secret"] = "sandbox-secret-key",
            ["Provider:SubmitDelayMs"] =
                providerSubmitDelayMs.ToString(),
            ["Worker:Outbox:IntervalMs"] = "300",
            ["Worker:Outbox:RetryBaseMs"] =
                outboxRetryBaseMs.ToString(),
            ["Worker:Outbox:RetryCapMs"] =
                outboxRetryCapMs.ToString(),
            ["Worker:Outbox:RetryJitterMs"] =
                outboxRetryJitterMs.ToString(),
            ["Worker:Reconciliation:IntervalMs"] = "300",
            ["Worker:Maintenance:IntervalMs"] = "300",
            ["Worker:VisibilityTimeoutSeconds"] =
                visibilityTimeoutSeconds.ToString(),
            ["Worker:LeaseSeconds"] = leaseSeconds.ToString(),
            ["Worker:HeartbeatIntervalMs"] =
                heartbeatIntervalMs.ToString(),
            ["Worker:ProcessingConcurrency"] =
                processingConcurrency.ToString()
        });
        if (configurationOverrides is not null)
            builder.Configuration.AddInMemoryCollection(
                configurationOverrides);

        builder.AddReliantObservability("Reliant.Worker.Test");

        builder.Services.AddReliantApplication();
        builder.Services.AddReliantInfrastructure(builder.Configuration);
        if (circuitBreakerOverride is not null)
            builder.Services.AddSingleton(circuitBreakerOverride);
        if (dbInterceptor is not null)
            builder.Services.ConfigureDbContext<ReliantDbContext>(
                options => options.AddInterceptors(dbInterceptor));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<IRetryScheduler, RetrySchedulerService>();
        builder.Services.AddSingleton<OperationalHistoryTelemetry>();
        builder.Services.AddSingleton<
            IOperationalHistoryCleanupFaultInjector,
            NoopOperationalHistoryCleanupFaultInjector>();
        builder.Services.AddScoped<OperationalHistoryCleanupService>();
        if (cleanupFaultInjectorOverride is not null)
            builder.Services.AddSingleton(
                cleanupFaultInjectorOverride);
        if (faultInjector is not null)
            builder.Services.AddSingleton<IWorkerFaultInjector>(faultInjector);
        if (queueAdapterOverride is not null)
            builder.Services.AddSingleton<IQueueAdapter>(queueAdapterOverride);
        if (leaseRepositoryOverride is not null)
            builder.Services.AddSingleton<ILeaseRepository>(
                leaseRepositoryOverride);
        if (jobRunRepositoryOverride is not null)
            builder.Services.AddSingleton<IJobRunRepository>(
                jobRunRepositoryOverride);
        if (jobAttemptRepositoryOverride is not null)
            builder.Services.AddSingleton<IJobAttemptRepository>(
                jobAttemptRepositoryOverride);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(_loggerProvider);
        if (includeOutboxPublisher)
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
