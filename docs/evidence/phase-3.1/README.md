# Phase 3.1 Evidence Pack

> Evidence Level: E1 (Testcontainers PostgreSQL) + E2 (LocalStack SQS + WorkerHost E2E)
> Baseline: `fc345f5` (Round 2 closure). Verified at Commit 19 (`39b492c`).
> Status: **Phase 3.1 — Completed** (all 10 Final Gates PASS, CI evidence present).

## Test Counts (scripts/verify.ps1 gates)

| Category | Filter | Count | Status |
| --- | --- | --- | --- |
| Unit | `Category=Unit` | 65 | PASS |
| Architecture | `Category=Architecture` | 5 | PASS |
| PostgreSQL Integration | `Category=Integration&Dependency=PostgreSQL` | 52 | PASS |
| LocalStack Integration | `Category=Integration&Dependency=LocalStack` | 15 | PASS |
| HttpApi Integration | `Category=Integration&Dependency=HttpApi` | 9 | PASS |
| WorkerHost E2E | `Category=Integration&Dependency=WorkerHost` | 10 | PASS |
| **Integration total** | `Category=Integration` | **76** | PASS |
| **Total** | | **146** | **PASS** |

## Gate Results

| Gate | Result | Evidence doc | Exact Evidence |
| --- | --- | --- | --- |
| 1 Provider Idempotency | PASS | `provider-idempotency.md` | `ProviderConcurrencyTests` (6) + `ProviderIdempotencyTests` + `DuplicateMessageE2ETests` (Scenario B) + `SafeRetryE2ETests`: 1 op / 1 reference under concurrency, retry, redelivery, new message |
| 2 Unknown Outcome | PASS | `unknown-outcome.md` | `FinalE2ETests.ProcessedResponseLost...` + `StateTransitionAuditTests.UnknownOutcome...`: response lost -> Succeeded, effect = 1 |
| 3 Safe Retry | PASS | `retry-scheduling.md` | `SafeRetryE2ETests.TimeoutBeforeProcessing...`: NotFound -> RetryPending -> scheduler -> worker -> Success, same key |
| 4 Reconciliation | PASS | `reconciliation.md` | `ReconciliationDecisionTableTests` (7) + `ReconciliationClosureTests` (7) incl `ConcurrentReconciliation_ShouldApplyResolutionOnlyOnce`; ManualRequired -> Resolved=false |
| 5 Callback | PASS | `callback-security.md`, `callback-ordering.md` | `CallbackSecurityHttpTests` (9 HTTP) + `CallbackTests` (10) + `FinalE2ETests` duplicate callback/ordering |
| 6 Retry Scheduling | PASS | `retry-scheduling.md` | `RetrySchedulingTests` (5) + `RetryMessageContractTests` (3): due/not-due, concurrent single dispatch, max -> DLQ |
| 7 Circuit Breaker | PASS | `circuit-breaker.md` | `CircuitBreakerTests` (11) + `CircuitBreakerIntegrationTests` (4) + `CircuitOpenE2ETests`: open no-ack + ApproximateReceiveCount >= 2 |
| 8 Crash Recovery | PASS | `crash-recovery.md` | `CrashRecoveryTests` (3) + `CrashBeforeAckE2ETests` + `DuplicateMessageE2ETests`: crash-before-ACK redelivery dedup, effect = 1 |
| 9 Integration Evidence | PASS | `localstack-sqs.md`, `final-e2e.md` | `LocalStackSqsTests` (5) + 5 E2E classes (10 tests): real SQS send/receive/delete/visibility/redelivery/counters |
| 10 Documentation & CI | PASS | `ci-run.md`, this pack, `current-state.md` | verify.ps1 test-count gate (0-match fails), TRX artifacts, test-summary, ADR-0017~0022 Accepted |

## Evidence index

- [`provider-idempotency.md`](provider-idempotency.md) — Gate 1
- [`unknown-outcome.md`](unknown-outcome.md) — Gate 2
- [`reconciliation.md`](reconciliation.md) — Gate 4
- [`callback-security.md`](callback-security.md) — Gate 5 (security)
- [`callback-ordering.md`](callback-ordering.md) — Gate 5 (ordering)
- [`retry-scheduling.md`](retry-scheduling.md) — Gates 3 & 6
- [`circuit-breaker.md`](circuit-breaker.md) — Gate 7
- [`crash-recovery.md`](crash-recovery.md) — Gate 8
- [`localstack-sqs.md`](localstack-sqs.md) — Gate 9
- [`final-e2e.md`](final-e2e.md) — Definition of Done
- [`ci-run.md`](ci-run.md) — CI evidence (commit, workflow, counts)
- [`known-limitations.md`](known-limitations.md) — non-blocking limitations
- [`test-summary.md`](test-summary.md) — full test inventory

## Known Limitations

See [`known-limitations.md`](known-limitations.md). Non-blocking: real AWS (E4)
smoke not performed; Notification Handler is a skeleton (out of gate scope);
secret vault rotation, retry-policy config and circuit tuning not exposed.
