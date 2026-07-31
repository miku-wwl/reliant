using Reliant.Domain.Enums;

namespace Reliant.Application.Messaging;

public class CircuitBreaker
{
    private readonly object _lock = new();
    private CircuitBreakerState _state = CircuitBreakerState.Closed;
    private int _failureCount;
    private DateTime _openedAt = DateTime.MinValue;
    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;

    public CircuitBreakerState State
    {
        get
        {
            lock (_lock)
            {
                if (_state == CircuitBreakerState.Open && DateTime.UtcNow - _openedAt >= _openDuration)
                {
                    _state = CircuitBreakerState.HalfOpen;
                }
                return _state;
            }
        }
    }

    public CircuitBreaker(int failureThreshold = 5, int openDurationSeconds = 30)
    {
        _failureThreshold = failureThreshold;
        _openDuration = TimeSpan.FromSeconds(openDurationSeconds);
    }

    public bool CanExecute()
    {
        lock (_lock)
        {
            if (_state == CircuitBreakerState.Closed) return true;
            if (_state == CircuitBreakerState.Open)
            {
                if (DateTime.UtcNow - _openedAt >= _openDuration)
                {
                    _state = CircuitBreakerState.HalfOpen;
                    return true;
                }
                return false;
            }
            return true;
        }
    }

    public void RecordSuccess()
    {
        lock (_lock)
        {
            _failureCount = 0;
            _state = CircuitBreakerState.Closed;
        }
    }

    public void RecordFailure(ErrorCategory? errorCategory)
    {
        lock (_lock)
        {
            if (errorCategory is not ErrorCategory.ServerError and not ErrorCategory.Timeout and not ErrorCategory.NetworkFailure)
            {
                return;
            }

            if (_state == CircuitBreakerState.HalfOpen)
            {
                _state = CircuitBreakerState.Open;
                _openedAt = DateTime.UtcNow;
                return;
            }

            if (_state == CircuitBreakerState.Open)
            {
                return;
            }

            _failureCount++;

            if (_failureCount >= _failureThreshold)
            {
                _state = CircuitBreakerState.Open;
                _openedAt = DateTime.UtcNow;
            }
        }
    }
}
