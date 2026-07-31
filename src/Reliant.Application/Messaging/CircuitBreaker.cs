using Reliant.Domain.Enums;

namespace Reliant.Application.Messaging;

public class CircuitBreaker
{
    private readonly object _lock = new();
    private CircuitBreakerState _state = CircuitBreakerState.Closed;
    private int _failureCount;
    private DateTimeOffset _openedAt;
    private bool _probeTaken;
    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;
    private readonly TimeProvider _timeProvider;

    public CircuitBreakerState State
    {
        get
        {
            lock (_lock)
            {
                if (_state == CircuitBreakerState.Open && _timeProvider.GetUtcNow() - _openedAt >= _openDuration)
                {
                    _state = CircuitBreakerState.HalfOpen;
                }
                return _state;
            }
        }
    }

    public CircuitBreaker(int failureThreshold = 5, int openDurationSeconds = 30, TimeProvider? timeProvider = null)
    {
        _failureThreshold = failureThreshold;
        _openDuration = TimeSpan.FromSeconds(openDurationSeconds);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Whether this worker may call the provider. In Half-Open only a single
    /// probe is granted - the first caller to transition Open -> Half-Open (or
    /// the first to take the probe) is allowed through; everyone else is told to
    /// defer. The probe is released on RecordSuccess/RecordFailure.
    /// </summary>
    public bool CanExecute()
    {
        lock (_lock)
        {
            var now = _timeProvider.GetUtcNow();

            switch (_state)
            {
                case CircuitBreakerState.Closed:
                    return true;

                case CircuitBreakerState.Open:
                    if (now - _openedAt >= _openDuration)
                    {
                        // This caller becomes the single half-open probe.
                        _state = CircuitBreakerState.HalfOpen;
                        _probeTaken = true;
                        return true;
                    }
                    return false;

                default: // HalfOpen
                    if (!_probeTaken)
                    {
                        _probeTaken = true;
                        return true;
                    }
                    return false;
            }
        }
    }

    public void RecordSuccess()
    {
        lock (_lock)
        {
            _failureCount = 0;
            _state = CircuitBreakerState.Closed;
            _probeTaken = false;
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
                // The single probe failed - reopen the circuit.
                _state = CircuitBreakerState.Open;
                _openedAt = _timeProvider.GetUtcNow();
                _probeTaken = false;
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
                _openedAt = _timeProvider.GetUtcNow();
            }
        }
    }
}
