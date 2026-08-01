# Evidence — Callback Ordering (Gate 5)

## Invariant

Callbacks are processed in a way that converges to the correct terminal state
regardless of ordering relative to the worker, and duplicate callbacks (the same
EventId delivered twice) apply at most once with no second state change.

## Scenario

- Callback arrives BEFORE the worker's provider response is handled: the worker
  must not overwrite the callback-applied terminal state.
- Callback arrives BEFORE reconciliation runs: the contribution converges to
  Succeeded and a later reconciliation skips it.
- Duplicate callback: the SAME EventId is delivered twice -> one inbox row, one
  state change.

## Failure injection

- `ProcessedButResponseLost` (response lost) + duplicate callback after terminal.
- `CallbackBeforeReconciliation` variant with reconciliation disabled, then a
  callback, then a manual `ReconcileContributionCommand` ("skipping").
- Same EventId sent twice through the real callback handler.

## Runtime path

`HandleProviderCallbackCommand` -> locate contribution -> DB-unique inbox dedup
by MessageId -> apply state only if not already terminal -> `CallbackInboxCount
== 1`; the worker reloads the contribution after the provider call so a
callback-applied terminal state is never overwritten.

## Exact tests

- `FinalE2ETests.ProcessedResponseLost_WithDuplicateMessageAndCallback_ShouldConverge_WithoutSecondProviderEffect` (duplicate callback step)
- `FinalE2ETests.CallbackBeforeReconciliation_WithDuplicateEvent_ShouldConvergeOnce`
- `CallbackTests.CallbackBeforeSubmitResponse_ShouldNotBeOverwrittenByWorker`
- `CallbackTests.CallbackDuringReconciliationPending_ShouldConvergeToSucceeded`
- `CallbackTests.CallbackSucceeded_WhenAlreadySucceeded_ShouldNotAddTransition`
- `CallbackTests.TerminalStateConflict_ShouldCreateManualRequiredReconciliation`

## Exact assertions

```text
first.StatusCode == 200, duplicate.StatusCode == 200
CallbackInboxCount == 1
Terminal duplicate confirmation creates no additional state transition
ReconcileContributionCommand after callback returns "skipping"
ProviderOperationCount == 1
Contribution.State == Succeeded
```

## Observed result

All PASS. Ordering is safe in both directions (callback-before-worker and
callback-before-reconciliation), and duplicates apply exactly once.

## Commit SHA

Commits 12-13 (`2a85f4e`, `03bbeb5`); re-verified in Commit 19 (`39b492c`).

## CI run

GitHub Actions `CI` (Commit 19, see `ci-run.md`).

## Limitations

Callback delivery order vs reconciliation is covered with the sandbox provider
and LocalStack; real provider webhook behaviour (E4) may add delivery-order
edge cases not simulated here.

## Conclusion

**Gate 5 (ordering) PASS** — callbacks converge regardless of ordering and
duplicates are idempotent.
