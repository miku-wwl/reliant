# Evidence — Unknown Outcome (Gate 2)

## Invariant

When the provider processes an operation but its response is lost (or it times
out), the system must not guess: it records an Unknown outcome, parks the
contribution for reconciliation, and converges to the true state with exactly
one provider effect.

## Scenario

`ProcessedButResponseLost` / `TimeoutBeforeProcessing` -> the worker records
Unknown -> `ProviderUnknown` -> `ReconciliationPending` -> reconciliation queries
the provider -> NotFound -> safe retry -> Success -> `Succeeded`.

## Failure injection

- `ProcessedButResponseLost`: the sandbox provider processes then throws
  `TaskCanceledException` (response lost).
- `TimeoutBeforeProcessing`: the sandbox provider never creates an operation.

## Runtime path

`SubmitToProviderCommand` -> Unknown -> state transitions
`Processing -> ProviderUnknown -> ReconciliationPending` (each audited
separately) -> `ReconciliationHandler` -> `ReconcileContributionCommand` ->
provider query -> resolve.

## Exact tests

- `FinalE2ETests.ProcessedResponseLost_WithDuplicateMessageAndCallback_ShouldConverge_WithoutSecondProviderEffect`
- `StateTransitionAuditTests.UnknownOutcome_ShouldRecordTwoDistinctTransitions`
- `ReconciliationDecisionTableTests.ProviderNotFound_ShouldSetRetryPendingAndNextRetryAt`

## Exact assertions

```text
Contribution.State == Succeeded after convergence
ProviderOperationCount == 1
ProviderReferenceCount == 1
Transitions present: Processing->ProviderUnknown->ReconciliationPending->Succeeded
UnresolvedReconciliationCount == 0
No dead letters
```

## Observed result

All PASS. The lost response is reconciled to Succeeded via the provider query
with exactly one provider effect; the unknown path is audited as two distinct
transitions.

## Commit SHA

Commits 11-16 (`54e1491` ... `e5bc1cf`), re-verified in Commit 19 (`39b492c`).

## CI run

GitHub Actions `CI` (Commit 19, see `ci-run.md`).

## Limitations

Unknown-outcome reconciliation assumes the provider `QueryStatus` is eventually
available and reliable; a permanently unavailable provider stays unresolved
(ManualRequired / WaitNextCycle), which is the designed safe behaviour.

## Conclusion

**Gate 2 PASS** — unknown outcomes are handled safely: never guessed, always
reconciled, exactly one provider effect.
