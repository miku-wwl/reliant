# Evidence — Provider Idempotency (Gate 1)

## Invariant

A single contribution must never produce a second provider-side business effect,
regardless of concurrency, retries, redelivery, or a new logical message for the
same contribution.

## Scenario

- Concurrent submissions of the same contribution.
- Sequential re-submission (idempotent replay).
- Same idempotency key with a different payload (must conflict).
- A NEW SQS message (new MessageId) for an already-succeeded contribution.
- Full safe-retry loop reusing the same provider idempotency key.

## Failure injection

- `ProviderConcurrencyTests` drive many workers at once against the same key.
- `DuplicateMessageE2ETests` insert a brand-new outbox message for a Succeeded
  contribution (Scenario B) and redeliver a message (Scenario A).
- `SafeRetryE2ETests` time out the first attempt then succeed on the retry.

## Runtime path

`SubmitToProviderCommand` -> atomic `ConcurrentDictionary.GetOrAdd(key)` (exactly
one operation object per key) -> `UNIQUE(ContributionId, AttemptNumber)` and
`UNIQUE(ProviderName, Reference)` in PostgreSQL -> business-state guard
(terminal contribution is never re-submitted) -> `ProviderReference` persisted
once.

## Exact tests

- `ProviderConcurrencyTests.SameContribution_SequentialSubmit_ShouldReturnSameReference`
- `ProviderConcurrencyTests.SameContribution_ConcurrentSubmit_ShouldCreateOneProviderOperation`
- `ProviderConcurrencyTests.SameContribution_ConcurrentSubmit_ShouldCreateOneProviderReference`
- `ProviderConcurrencyTests.SameKey_DifferentPayload_ShouldReturnIdempotencyConflict`
- `ProviderConcurrencyTests.AttemptNumber_ShouldBeUniquePerContribution`
- `ProviderConcurrencyTests.WorkerRestart_ShouldReuseSameProviderIdempotencyKey`
- `ProviderIdempotencyTests.SameContribution_ShouldProduceSingleProviderOperation`
- `DuplicateMessageE2ETests.NewMessageForSucceededContribution_ShouldNotInvokeProviderAgain`
- `DuplicateMessageE2ETests.DifferentMessageIdSameContribution_ShouldBeProtectedByBusinessState`
- `SafeRetryE2ETests.TimeoutBeforeProcessing_ShouldReconcileNotFound_ThenRetryAndSucceed_WithOneProviderEffect`

## Exact assertions

```text
ProviderOperationCount == 1
ProviderReferenceCount == 1
All ProcessingAttempts share the same ProviderIdempotencyKey
Same key + different payload -> IdempotencyConflict
AttemptNumber unique per contribution
```

## Observed result

All PASS. Provider operation count stays exactly 1 under concurrency, restart,
redelivery and retry; the business-state guard blocks a new message for a
terminal contribution.

## Commit SHA

Commits 10-16 (`e8b6081` ... `e5bc1cf`), re-verified in Commit 19 (`39b492c`).

## CI run

GitHub Actions `CI` (Commit 19, see `ci-run.md`); locally `scripts/verify.ps1`.

## Limitations

Sandbox provider only (LocalStack/Testcontainers). Real AWS (E4) smoke not
performed. Provider idempotency semantics depend on the provider honouring the
key.

## Conclusion

**Gate 1 PASS** — provider idempotency is enforced at three layers: in-memory
atomic operation creation, database unique constraints, and business-state
protection.
