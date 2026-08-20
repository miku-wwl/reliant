# Runbook: Provider Unknown Outcome

Owner: Reliant on-call

Trigger: `ReliantProviderUnknownOutcomes`, `ReliantReconciliationStale` or a
rise in unresolved reconciliation work.

1. Confirm the Contribution/ProcessingAttempt is durably `Unknown`; do not
   infer business state from a timeout span alone.
2. Inspect Provider request duration, timeout/error category, circuit state and
   the original trace.
3. Query the Provider using the stable provider idempotency key/reference.
4. Allow Reconciliation to apply Succeeded/Failed/NotFound policy.
5. If evidence conflicts or maximum reconciliation is reached, leave the case
   `ManualRequired`; never blindly submit a new payment operation.
6. Confirm reconciliation age and pending count return to normal.

Use `reliantctl diagnostics collect` for a redacted aggregate snapshot. Use
the scoped inspect commands for a specific authorized tenant case.

