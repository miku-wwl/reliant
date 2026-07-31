using Reliant.Application.Messaging;
using Reliant.Domain.Enums;

namespace Reliant.Tests.Unit;

[Trait("Category", "Unit")]
public class RetryPolicyTests
{
    [Theory]
    [InlineData(ErrorCategory.Timeout, true)]
    [InlineData(ErrorCategory.RateLimited, true)]
    [InlineData(ErrorCategory.ServerError, true)]
    [InlineData(ErrorCategory.NetworkFailure, true)]
    [InlineData(ErrorCategory.ValidationFailure, false)]
    [InlineData(ErrorCategory.AuthenticationFailure, false)]
    [InlineData(ErrorCategory.PermanentBusinessRejection, false)]
    [InlineData(ErrorCategory.UnknownOutcome, false)]
    public void IsRetryable_ShouldClassifyCorrectly(ErrorCategory category, bool expected)
    {
        var result = ErrorClassifier.IsRetryable(category);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ShouldRetry_ShouldReturnTrue_WhenAttemptBelowMax_AndRetryableError()
    {
        var policy = new RetryPolicy { MaxAttempts = 5 };
        var result = policy.ShouldRetry(1, ErrorCategory.Timeout);
        Assert.True(result);
    }

    [Fact]
    public void ShouldRetry_ShouldReturnFalse_WhenAttemptAtMax()
    {
        var policy = new RetryPolicy { MaxAttempts = 5 };
        var result = policy.ShouldRetry(5, ErrorCategory.Timeout);
        Assert.False(result);
    }

    [Fact]
    public void ShouldRetry_ShouldReturnFalse_WhenNonRetryableError()
    {
        var policy = new RetryPolicy { MaxAttempts = 5 };
        var result = policy.ShouldRetry(1, ErrorCategory.ValidationFailure);
        Assert.False(result);
    }

    [Fact]
    public void ShouldRetry_ShouldReturnFalse_WhenErrorIsNull()
    {
        var policy = new RetryPolicy { MaxAttempts = 5 };
        var result = policy.ShouldRetry(1, null);
        Assert.False(result);
    }

    [Fact]
    public void GetDelay_ShouldIncreaseExponentially()
    {
        var policy = new RetryPolicy { BaseDelaySeconds = 1.0, MaxDelaySeconds = 30.0, JitterSeconds = 0 };
        var delay1 = policy.GetDelay(1);
        var delay2 = policy.GetDelay(2);
        var delay3 = policy.GetDelay(3);

        Assert.True(delay2 > delay1);
        Assert.True(delay3 > delay2);
    }

    [Fact]
    public void GetDelay_ShouldNotExceedMaxDelay()
    {
        var policy = new RetryPolicy { BaseDelaySeconds = 1.0, MaxDelaySeconds = 10.0, JitterSeconds = 0 };
        var delay = policy.GetDelay(20);
        Assert.True(delay.TotalSeconds <= 10.0);
    }

    [Fact]
    public void GetDelay_ShouldIncludeJitter()
    {
        var policy = new RetryPolicy { BaseDelaySeconds = 1.0, MaxDelaySeconds = 30.0, JitterSeconds = 1.0 };
        var delay1 = policy.GetDelay(1);
        var delay2 = policy.GetDelay(1);

        Assert.NotEqual(delay1, delay2);
    }
}
