# Evidence — Retry Scheduling (Gate 6)

## Invariant

A safe retry is scheduled only after the provider reports the contribution was
NOT processed (NotFound), the retry is dispatched exactly once even under
concurrent schedulers, uses the SAME provider idempotency key, and a retry that
is not yet due is never dispatched.

## Scenario

Full loop through real infrastructure:
`TimeoutBeforeProcessing -> Unknown -> Reconciliation NotFound -> RetryPending ->
Retry Scheduler -> retry outbox -> LocalStack SQS -> Worker -> Success ->
Succeeded`.

## Failure injection

- `TimeoutBeforeProcessing` (first attempt fails before processing).
- Two concurrent retry schedulers racing the same due row.
- Retry not yet due (`NextRetryAt` in the future).
- Max retry attempts exceeded.

## Runtime path

`Reconciliation` NotFound -> `RetryPending` + `NextRetryAt` ->
`RetrySchedulerService.DispatchDueRetriesAsync` (atomic `FOR UPDATE SKIP LOCKED`
claim) -> `ContributionRetryRequested` outbox -> `OutboxPublisher` -> SQS ->
`ProcessingHandler` (`RetryPending -> Processing`) -> provider success.

## Exact tests

- `RetrySchedulingTests.RetryPending_NotDue_ShouldNotBeDispatched`
- `RetrySchedulingTests.RetryPending_Due_ShouldCreateOneOutboxMessage`
- `RetrySchedulingTests.ConcurrentSchedulers_ShouldDispatchOnlyOnce`
- `RetrySchedulingTests.MaxRetryAttempts_ShouldMoveToFailedAndCreateDeadLetter`
- `RetrySchedulingTests.RetryCount_ShouldPersistAcrossDispatch`
- `RetryMessageContractTests.RetryProcessingContract_ShouldCarryOnlyIdentity`
- `RetryMessageContractTests.RetryPending_ShouldBeSelectableWhenDueAndTransitionToProcessing`
- `SafeRetryE2ETests.TimeoutBeforeProcessing_ShouldReconcileNotFound_ThenRetryAndSucceed_WithOneProviderEffect`

## Exact assertions

```text
Not-due retry -> not dispatched
Due retry -> exactly one retry outbox message
Concurrent schedulers -> dispatched once
All attempts share the same ProviderIdempotencyKey
Attempt count >= 2, final provider operation count == 1
Contribution.State == Succeeded, NextRetryAt == null
No dead letters
```

## Observed result

All PASS. The full safe-retry loop converges to Succeeded through real
Outbox -> SQS -> Worker with exactly one provider effect.

## Commit SHA

Commits 14-16 (`cb907da` ... `e5bc1cf`); re-verified in Commit 19 (`39b492c`).

## CI run

GitHub Actions `CI` (Commit 19, see `ci-run.md`).

## Limitations

Retry delays follow the fixed `RetryPolicy` (exponential backoff with jitter);
configuration-driven policy overrides are not yet exposed.

## Conclusion

**Gate 6 PASS** — safe retries are scheduled only after NotFound, dispatched
exactly once, and converge with one provider effect.
