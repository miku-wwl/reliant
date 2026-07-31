# Phase 3.1 Evidence Pack

> Evidence Level: E1 (Testcontainers PostgreSQL) + E2 (LocalStack SQS + WorkerHost E2E)
> Baseline: `70d404f7775856c3a833c9eef879e9a52a0c29a5`

## Test Counts

| Category | Count | Status |
| --- | --- | --- |
| Unit | 61 | PASS |
| Architecture | 5 | PASS |
| Integration (PostgreSQL) | 45 | PASS |
| Integration (LocalStack SQS) | 5 | PASS |
| End-to-End (WorkerHost) | 1 | PASS |
| **Total** | **117** | **PASS** |

## Gate Results

| Gate | Result | Exact Evidence |
| --- | --- | --- |
| 1 Provider Idempotency | PASS | `ProviderConcurrencyTests`: concurrent submit -> 1 op + 1 reference; atomic `GetOrAdd`; `UNIQUE(ContributionId,AttemptNumber)` + `UNIQUE(ProviderName,Reference)`; same key/different payload -> conflict |
| 2 Unknown Outcome | PASS | `ReconciliationDecisionTableTests.ProviderSucceeded...` + `FinalE2ETests`: response lost -> converges to Succeeded, provider effect = 1 |
| 3 Safe Retry | PASS | `RetryMessageContractTests.ProviderNotFound...`: NotFound -> RetryPending + NextRetryAt; retry reuses same idempotency key |
| 4 Reconciliation | PASS | `ReconciliationDecisionTableTests` (7): Succeeded/Failed/Pending/NotFound/Unavailable/Missing/Max + concurrent safe exit |
| 5 Callback | PASS | `CallbackTests` (10): Reference/Key lookup, orphan persisted, DB unique dedup, concurrent dedup, before-response, terminal conflict |
| 6 Retry Scheduling | PASS | `RetrySchedulingTests` (5): not-due no dispatch, due exactly one outbox, concurrent schedulers dispatch once, max -> Failed/DLQ/alert, retry count persists |
| 7 Circuit Breaker | PASS | `CircuitBreakerTests` (11) + `CircuitBreakerIntegrationTests` (4): Open no provider/no attempt/no budget; single half-open probe; TimeProvider |
| 8 Crash Recovery | PASS | `CrashRecoveryTests` (3): crash after attempt persisted, crash before response handled, inbox dedup on redelivery |
| 9 Integration Evidence | PASS | `LocalStackSqsTests` (5) + `FinalE2ETests` (1): real SQS send/receive/delete/visibility/redelivery + full worker host E2E |
| 10 Documentation | PASS | `current-state.md`, this pack, ADR status |

## Final E2E Assertions (Definition of Done)

Test: `ProcessedResponseLost_WithDuplicateMessageAndCallback_ShouldConverge_WithoutSecondProviderEffect`

```text
ProviderOperationCount == 1          (asserted)
ProviderReferenceCount == 1          (asserted)
Contribution.State == Succeeded      (asserted)
Processing->ProviderUnknown->ReconciliationPending->Succeeded transitions present
UnresolvedReconciliationCount == 0   (asserted)
DuplicateBusinessEffectCount == 0    (asserted via op count + reference count)
No unexpected DeadLetterRecord       (asserted)
```

Real pipeline executed: Outbox -> LocalStack SQS -> Processing Worker -> Sandbox
Provider (ProcessedButResponseLost) -> Unknown -> Reconciliation -> Succeeded ->
duplicate callback -> duplicate message (inbox dedup) -> no second provider effect.

## Known Limitations

- Notification Handler is still a skeleton (out of Phase 3.1 gate scope).
- Real AWS (E4) smoke not performed; LocalStack E2 used.
- API-level HTTP callback signature tests (401 paths) are covered by
  `ProviderCallbackVerifier` unit logic; full HTTP surface deferred to E4.
