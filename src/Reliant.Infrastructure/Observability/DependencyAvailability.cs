using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Reliant.Infrastructure.Persistence;

namespace Reliant.Infrastructure.Observability;

public sealed class QueueAvailabilityState
{
    private readonly object _gate = new();
    private DateTimeOffset? _lastSuccess;
    private DateTimeOffset? _lastFailure;
    private string? _lastFailureKind;

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _lastSuccess = DateTimeOffset.UtcNow;
        }
    }

    public void RecordFailure(Exception exception)
    {
        lock (_gate)
        {
            _lastFailure = DateTimeOffset.UtcNow;
            _lastFailureKind = exception.GetType().Name;
        }
    }

    public QueueAvailabilitySnapshot Snapshot()
    {
        lock (_gate)
        {
            return new QueueAvailabilitySnapshot(
                _lastSuccess,
                _lastFailure,
                _lastFailureKind);
        }
    }
}

public sealed record QueueAvailabilitySnapshot(
    DateTimeOffset? LastSuccess,
    DateTimeOffset? LastFailure,
    string? LastFailureKind)
{
    public bool IsReady =>
        LastSuccess.HasValue &&
        (!LastFailure.HasValue || LastSuccess >= LastFailure);
}

public sealed class PostgreSqlReadinessHealthCheck(
    IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var database = scope.ServiceProvider
                .GetRequiredService<ReliantDbContext>();
            return await database.Database.CanConnectAsync(
                cancellationToken)
                ? HealthCheckResult.Healthy("PostgreSQL reachable")
                : HealthCheckResult.Unhealthy(
                    "PostgreSQL connection check returned false");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy(
                "PostgreSQL readiness check failed",
                exception);
        }
    }
}

public sealed class QueueReadinessHealthCheck(
    QueueAvailabilityState availability) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshot = availability.Snapshot();
        if (snapshot.IsReady)
        {
            return Task.FromResult(
                HealthCheckResult.Healthy(
                    "SQS has completed a successful operation"));
        }

        var data = new Dictionary<string, object>
        {
            ["lastSuccess"] = snapshot.LastSuccess?.ToString("O") ??
                "never",
            ["lastFailure"] = snapshot.LastFailure?.ToString("O") ??
                "none",
            ["failureKind"] = snapshot.LastFailureKind ?? "none"
        };
        return Task.FromResult(
            HealthCheckResult.Unhealthy(
                "SQS has not completed a successful operation since its latest failure",
                data: data));
    }
}
