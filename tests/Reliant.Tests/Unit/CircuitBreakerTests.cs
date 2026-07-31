using Reliant.Application.Messaging;
using Reliant.Domain.Enums;
using Reliant.Tests.TestHelpers;

namespace Reliant.Tests.Unit;

[Trait("Category", "Unit")]
public class CircuitBreakerTests
{
    [Fact]
    public void Closed_ShouldAllowExecution()
    {
        var cb = new CircuitBreaker(failureThreshold: 5, openDurationSeconds: 30);
        Assert.True(cb.CanExecute());
    }

    [Fact]
    public void ShouldOpen_AfterConsecutiveFailures()
    {
        var cb = new CircuitBreaker(failureThreshold: 3, openDurationSeconds: 30);

        cb.RecordFailure(ErrorCategory.ServerError);
        cb.RecordFailure(ErrorCategory.ServerError);
        Assert.True(cb.CanExecute());

        cb.RecordFailure(ErrorCategory.ServerError);
        Assert.False(cb.CanExecute());
    }

    [Fact]
    public void ShouldNotOpen_On429()
    {
        var cb = new CircuitBreaker(failureThreshold: 3, openDurationSeconds: 30);

        cb.RecordFailure(ErrorCategory.RateLimited);
        cb.RecordFailure(ErrorCategory.RateLimited);
        cb.RecordFailure(ErrorCategory.RateLimited);

        Assert.True(cb.CanExecute());
    }

    [Fact]
    public void ShouldNotOpen_OnValidationError()
    {
        var cb = new CircuitBreaker(failureThreshold: 3, openDurationSeconds: 30);

        cb.RecordFailure(ErrorCategory.ValidationFailure);
        cb.RecordFailure(ErrorCategory.ValidationFailure);

        Assert.True(cb.CanExecute());
    }

    [Fact]
    public void ShouldClose_OnSuccessAfterHalfOpen()
    {
        var clock = new FakeTimeProvider();
        var cb = new CircuitBreaker(failureThreshold: 1, openDurationSeconds: 1, clock);

        cb.RecordFailure(ErrorCategory.ServerError);
        Assert.False(cb.CanExecute());

        clock.Advance(TimeSpan.FromSeconds(2));

        // The first caller after the window becomes the single half-open probe.
        Assert.True(cb.CanExecute());
        cb.RecordSuccess();
        Assert.True(cb.CanExecute());
    }

    [Fact]
    public void ShouldReOpen_OnFailureDuringHalfOpen()
    {
        var clock = new FakeTimeProvider();
        var cb = new CircuitBreaker(failureThreshold: 1, openDurationSeconds: 1, clock);

        cb.RecordFailure(ErrorCategory.ServerError);

        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.True(cb.CanExecute());
        cb.RecordFailure(ErrorCategory.ServerError);
        Assert.False(cb.CanExecute());
    }

    [Fact]
    public void ShouldOpen_OnTimeout()
    {
        var cb = new CircuitBreaker(failureThreshold: 2, openDurationSeconds: 30);

        cb.RecordFailure(ErrorCategory.Timeout);
        cb.RecordFailure(ErrorCategory.Timeout);

        Assert.False(cb.CanExecute());
    }

    [Fact]
    public void ShouldOpen_OnNetworkFailure()
    {
        var cb = new CircuitBreaker(failureThreshold: 2, openDurationSeconds: 30);

        cb.RecordFailure(ErrorCategory.NetworkFailure);
        cb.RecordFailure(ErrorCategory.NetworkFailure);

        Assert.False(cb.CanExecute());
    }

    [Fact]
    public void HalfOpen_ShouldAllowOnlyOneProbe()
    {
        var clock = new FakeTimeProvider();
        var cb = new CircuitBreaker(failureThreshold: 1, openDurationSeconds: 1, clock);

        cb.RecordFailure(ErrorCategory.ServerError);
        clock.Advance(TimeSpan.FromSeconds(2));

        // First caller becomes the probe; everyone else is deferred until the
        // probe completes.
        Assert.True(cb.CanExecute());
        Assert.False(cb.CanExecute());
        Assert.False(cb.CanExecute());

        // Probe succeeds -> circuit closes -> all callers allowed again.
        cb.RecordSuccess();
        Assert.True(cb.CanExecute());
        Assert.True(cb.CanExecute());
    }

    [Fact]
    public void HalfOpenConcurrentWorkers_ShouldNotAllReachProvider()
    {
        var clock = new FakeTimeProvider();
        var cb = new CircuitBreaker(failureThreshold: 1, openDurationSeconds: 1, clock);

        cb.RecordFailure(ErrorCategory.ServerError);
        clock.Advance(TimeSpan.FromSeconds(2));

        var results = Enumerable.Range(0, 16)
            .AsParallel()
            .WithDegreeOfParallelism(16)
            .Select(_ => cb.CanExecute())
            .ToArray();

        Assert.Equal(1, results.Count(r => r));
    }

    [Fact]
    public void Open_ShouldDeferAll_UntilWindowElapses()
    {
        var clock = new FakeTimeProvider();
        var cb = new CircuitBreaker(failureThreshold: 1, openDurationSeconds: 10, clock);

        cb.RecordFailure(ErrorCategory.ServerError);
        Assert.False(cb.CanExecute());

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(cb.CanExecute());

        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.True(cb.CanExecute());
    }
}
