using Reliant.Domain.Enums;

namespace Reliant.Application.Messaging;

public static class ErrorClassifier
{
    public static bool IsRetryable(ErrorCategory category)
    {
        return category switch
        {
            ErrorCategory.Timeout => true,
            ErrorCategory.RateLimited => true,
            ErrorCategory.ServerError => true,
            ErrorCategory.NetworkFailure => true,
            ErrorCategory.ValidationFailure => false,
            ErrorCategory.AuthenticationFailure => false,
            ErrorCategory.PermanentBusinessRejection => false,
            ErrorCategory.UnknownOutcome => false,
            _ => false
        };
    }
}

public class RetryPolicy
{
    public int MaxAttempts { get; init; } = 5;
    public double BaseDelaySeconds { get; init; } = 1.0;
    public double MaxDelaySeconds { get; init; } = 30.0;
    public double JitterSeconds { get; init; } = 1.0;

    public TimeSpan GetDelay(int attempt)
    {
        var exponential = BaseDelaySeconds * Math.Pow(2, attempt - 1);
        var capped = Math.Min(exponential, MaxDelaySeconds);
        var jitter = Random.Shared.NextDouble() * JitterSeconds;
        return TimeSpan.FromSeconds(capped + jitter);
    }

    public bool ShouldRetry(int attempt, ErrorCategory? errorCategory)
    {
        if (attempt >= MaxAttempts) return false;
        if (errorCategory is null) return false;
        return ErrorClassifier.IsRetryable(errorCategory.Value);
    }
}
