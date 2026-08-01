# Evidence — Reconciliation (Gate 4)

## Invariant

Reconciliation must resolve a local/remote difference exactly once per cycle,
must not double-apply, and must return truthful `Resolved` semantics:
`Succeeded`/`Failed`/`NotFound-safe-retry` are resolved; `ManualRequired`,
`Pending`, `Unavailable`, `InvalidResponse` are unresolved.

## Scenario

A contribution parked in `ProviderUnknown` / `ReconciliationPending` is queried
against the provider; the decision table (Succeeded / Failed / Pending /
NotFound / Unavailable / no-evidence / max-cycles) drives the resolution.

## Failure injection

- `QueryUnavailable` mode: provider query throws -> stays unresolved.
- `PendingForever`: provider stays pending -> `WaitNextCycle`, unresolved.
- `TimeoutBeforeProcessing`: no operation -> `NotFound` -> safe retry.
- Two scopes run `ReconcileContributionCommand` concurrently.

## Runtime path

`ReconciliationHandler` -> `ReconcileContributionCommand` -> provider
`QueryStatusByReference/IdempotencyKey` -> state transition + `ReconciliationRecord`
-> optimistic-concurrency safe save (`TrySaveChangesAsync`).

## Exact tests

- `ReconciliationDecisionTableTests.ProviderSucceeded_ShouldConvergeToSucceeded`
- `ReconciliationDecisionTableTests.ProviderFailed_ShouldConvergeToFailed`
- `ReconciliationDecisionTableTests.ProviderPending_ShouldRemainReconciliationPending`
- `ReconciliationDecisionTableTests.ProviderNotFound_ShouldSetRetryPendingAndNextRetryAt`
- `ReconciliationDecisionTableTests.ProviderUnavailable_ShouldRemainPendingAndUnresolved`
- `ReconciliationDecisionTableTests.MissingLocalAttempt_ShouldCreateManualRequired`
- `ReconciliationDecisionTableTests.MaxReconciliationCount_ShouldCreateManualRequiredAlert`
- `ReconciliationClosureTests.ConcurrentReconciliation_ShouldApplyResolutionOnlyOnce`
- `ReconciliationClosureTests.ManualRequired_ShouldReturnUnresolved`
- `ReconciliationClosureTests.ManualRequiredMaxCycles_ShouldReturnUnresolved`
- `ReconciliationClosureTests.ProviderPending_ShouldReturnUnresolved`
- `ReconciliationClosureTests.ProviderUnavailable_ShouldReturnUnresolved`
- `ReconciliationClosureTests.Succeeded_ShouldReturnResolved`
- `ReconciliationClosureTests.NotFoundSafeRetry_ShouldReturnResolvedForThisReconciliationCycle`

## Exact assertions

```text
Exactly one state transition to RetryPending under concurrency
Exactly one SafeRetry reconciliation record
Exactly one retry schedule (NextRetryAt set once)
No duplicate ProviderReference
No invalid transition
ManualRequired -> Resolved == false
Pending / Unavailable -> Resolved == false
Succeeded / Failed / NotFound-safe-retry -> Resolved == true
```

## Observed result

All PASS. Concurrency applies the resolution exactly once; the `Resolved`
semantics fix (Commit 18) ensures `ManualRequired` never reports resolved.

## Commit SHA

Commit 18 (`5a10b3a`) fixed the `Resolved` semantics; re-verified in Commit 19
(`39b492c`).

## CI run

GitHub Actions `CI` (Commit 19, see `ci-run.md`).

## Limitations

Reconciliation requires a reliable provider query. No real multi-region or real
AWS reconciliation was exercised (E4 scope).

## Conclusion

**Gate 4 PASS** — reconciliation is safe under concurrency and its result
semantics are explicit and correct.
