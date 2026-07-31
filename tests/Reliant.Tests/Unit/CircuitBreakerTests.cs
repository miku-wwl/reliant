using Reliant.Application.Messaging;
using Reliant.Domain.Enums;

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
        var cb = new CircuitBreaker(failureThreshold: 1, openDurationSeconds: 1);

        cb.RecordFailure(ErrorCategory.ServerError);
        Assert.False(cb.CanExecute());

        System.Threading.Thread.Sleep(1100);

        Assert.True(cb.CanExecute());
        cb.RecordSuccess();
        Assert.True(cb.CanExecute());
    }

    [Fact]
    public void ShouldReOpen_OnFailureDuringHalfOpen()
    {
        var cb = new CircuitBreaker(failureThreshold: 1, openDurationSeconds: 1);

        cb.RecordFailure(ErrorCategory.ServerError);

        System.Threading.Thread.Sleep(1100);

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
}
