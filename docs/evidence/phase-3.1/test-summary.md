# Phase 3.1 Evidence - Test Summary

> Evidence Level: E1 (Testcontainers PostgreSQL) + E2 (LocalStack SQS + WorkerHost E2E)
> Current baseline: `3dc26f9`, CI run `31446024186`. Counts match
> `scripts/verify.ps1` gates.

## Test Summary

| Test Category | Count | Status |
| --- | --- | --- |
| Unit Tests | 65 | All Passed |
| Architecture Tests | 5 | All Passed |
| Integration Tests (all) | 93 | All Passed |
| Integration Tests (PostgreSQL filter) | 85 | All Passed |
| Integration Tests (LocalStack filter) | 35 | All Passed |
| Integration Tests (HttpApi filter) | 10 | All Passed |
| End-to-End (WorkerHost filter) | 23 | All Passed |
| **Total** | **163** | **All Passed** |

Dependency filters overlap; 85, 35, 10 and 23 are coverage views over the 93
integration tests and are not additive.

## Historical Phase 3.1 closure inventory

The inventory below records the original 146-test Phase 3.1 closure. The
current repository has additional Phase 2/3 experiment tests; the authoritative
current inventory is the CI-generated `test-summary.md` and
`experiment-summary.md` attached to run `31446024186`.

### Test Files (Integration + E2E)

| File | Scope | Dependency |
| --- | --- | --- |
| `CallbackTests.cs` (10) | Callback lookup/persistence/ordering, orphan, dedup, before-response, terminal conflict | PostgreSQL |
| `CallbackSecurityHttpTests.cs` (9) | Real HTTP callback HMAC signature + timestamp verification (401 paths) | HttpApi + PostgreSQL |
| `ProviderIdempotencyTests.cs` (2) | Same contribution single provider operation, attempt-before-call | PostgreSQL |
| `ProviderConcurrencyTests.cs` (6) | Concurrent submit -> 1 op/1 reference, key conflict, unique attempt, key reuse | PostgreSQL |
| `ReconciliationTests.cs` | Processed-but-response-lost, NotFound on reconciliation | PostgreSQL |
| `ReconciliationDecisionTableTests.cs` (7) | Full decision table + ProviderUnavailable + MaxCount (ManualRequired unresolved) | PostgreSQL |
| `ReconciliationClosureTests.cs` (7) | Resolved semantics (ManualRequired/Pending/Unavailable/Succeeded/NotFound) + concurrent apply-once | PostgreSQL |
| `RetryMessageContractTests.cs` (3) | Versioned processing contract, retry-due selection | PostgreSQL |
| `RetrySchedulingTests.cs` (5) | Due/not-due dispatch, concurrent scheduler single dispatch, max -> DLQ, retry count | PostgreSQL |
| `CircuitBreakerIntegrationTests.cs` (4) | Open: no provider/attempt/budget; probe submit | PostgreSQL |
| `CrashRecoveryTests.cs` (3) | Crash after attempt persisted / before response; inbox dedup | PostgreSQL |
| `DatabaseConstraintTests.cs` | Unique idempotency key, tenant isolation, optimistic concurrency | PostgreSQL |
| `LocalStackSqsTests.cs` (5) | Real SQS send/receive/delete, visibility, redelivery, duplicate delivery | LocalStack |
| `FinalE2ETests.cs` (2) | ProcessedResponseLost converge + callback-before-reconciliation + duplicate callback | LocalStack + WorkerHost |
| `DuplicateMessageE2ETests.cs` (5) | Same-MessageId redelivery dedup vs new-message business-state protection | LocalStack + WorkerHost |
| `CrashBeforeAckE2ETests.cs` (1) | Crash after commit before ACK -> redelivery dedup, ReceiveCount >= 2 | LocalStack + WorkerHost |
| `SafeRetryE2ETests.cs` (1) | Timeout -> NotFound -> retry -> success, one provider effect | LocalStack + WorkerHost |
| `CircuitOpenE2ETests.cs` (1) | Circuit open -> no ack -> ApproximateReceiveCount >= 2 -> recover | LocalStack + WorkerHost |

### Unit Test Files

- `CircuitBreakerTests.cs` (11): single probe, open/defer, TimeProvider-based
- `ContributionStateMachineTests.cs` (27): all allowed transitions
- `ProviderOperationKeyFactoryTests.cs` (8): stable key, no attempt number, org isolation
- `RetryPolicyTests.cs`: exponential backoff / retryability classification
- `StateTransitionAuditTests.cs` (4): every change audited before TransitionTo, no collapsed multi-hop

## Notes

- Counts are enforced by `scripts/verify.ps1` count gates (a filter matching 0
  tests fails the run). See `ci-run.md`.

- `PostgreSqlFixture` applies all EF migrations via Testcontainers PostgreSQL.
- `WorkerHostFixture` runs the real worker host (Outbox Publisher, Processing,
  Reconciliation, Scheduled Maintenance) against LocalStack SQS + PostgreSQL.
- SQS adapter fixes verified: MessageType attribute round-trips, ApproximateReceiveCount
  is read from system attributes.
- 预期：第二个抛 DbUpdateConcurrencyException
- 实际：PASS

## Known Limitations

以下限制不影响 Phase 3.1 Final Gate：

- 尚未执行真实 AWS E4 Smoke Test；当前 AWS 队列语义通过 LocalStack E2 验证。
- Notification Handler 仍为 Skeleton，不属于 Phase 3.1 Reliability Gate 范围。
- Provider Secret 尚未接入 Azure Key Vault 或 AWS Secrets Manager。
- Retry Policy、Circuit Breaker 阈值和并发限制仍需要在后续负载测试中调优。
- SandboxProvider 仅用于测试和故障模拟，不代表真实第三方 Provider 的全部行为。
