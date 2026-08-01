# Evidence — Circuit Breaker (Gate 7)

## Invariant

While the circuit is OPEN the worker must not call the provider, must not create
a ProcessingAttempt, must not consume retry budget, must not write a processed
inbox, and must NOT ack the SQS message - it is redelivered after the visibility
timeout and completes once the circuit closes.

## Scenario

Open the circuit -> a contribution is created -> the worker defers (no ack, no
inbox) -> the message redelivers (SQS `ApproximateReceiveCount >= 2`) -> close
the circuit -> the redelivered message processes successfully -> the queue
drains.

## Failure injection

- `RecordFailure(ErrorCategory.ServerError)` x5 opens the circuit.
- `RecordSuccess()` closes it.
- Real visibility timeout (3 s) forces redelivery through LocalStack.

## Runtime path

`ProcessingHandler` -> `SubmitToProviderCommand` ->
`CircuitBreaker.CanExecute()` -> false -> `DeferredBecauseCircuitOpen` -> worker
returns WITHOUT deleting the SQS message and WITHOUT writing an inbox -> message
redelivers -> circuit closed -> worker processes -> Succeeded.

## Exact tests

- `CircuitBreakerTests` (unit, 11): open/close/half-open single-probe/thresholds.
- `CircuitBreakerIntegrationTests` (4): provider not called, no attempt, no
  budget consumed, single probe.
- `CircuitOpenE2ETests.CircuitOpen_ShouldLeaveMessageUnacked_AndRedeliverAfterVisibilityTimeout`

## Exact assertions

Circuit open phase:

```text
ProviderOperationCount == 0
ProcessingAttemptCount == 0
RetryCount == 0
ProcessedInboxCount == 0
Message still exists / redelivers == true
ApproximateReceiveCount >= 2
```

Circuit recovered:

```text
ProviderOperationCount == 1
Contribution.State == Succeeded
ProcessedInboxCount == 1
QueueEventuallyEmpty == true
```

## Observed result

All PASS. The circuit-open E2E proves no-ack + real LocalStack redelivery
(`ApproximateReceiveCount >= 2`), and after `RecordSuccess` the message
completes and the queue drains.

## Commit SHA

Commit 17 (`8b95c0d`); re-verified in Commit 19 (`39b492c`).

## CI run

GitHub Actions `CI` (Commit 19, see `ci-run.md`).

## Limitations

Open duration and failure threshold are constructor defaults (5 failures /
30 s); a half-open probe is granted to a single caller. Real AWS behaviour of
redelivery counters matches SQS semantics via LocalStack.

## Conclusion

**Gate 7 PASS** — the circuit breaker prevents provider calls, leaves messages
unacked, and recovers cleanly.
