# Phase 3.1 Evidence - Test Summary

> Evidence Level: E1 (Testcontainers PostgreSQL) + E2 (LocalStack SQS + WorkerHost E2E)

## Test Summary

| Test Category | Count | Status |
| --- | --- | --- |
| Unit Tests | 61 | All Passed |
| Architecture Tests | 5 | All Passed |
| Integration Tests (PostgreSQL) | 45 | All Passed |
| Integration Tests (LocalStack SQS) | 5 | All Passed |
| End-to-End (WorkerHost) | 1 | All Passed |
| **Total** | **117** | **All Passed** |

## Test Files (Integration + E2E)

| File | Scope | Dependency |
| --- | --- | --- |
| `CallbackTests.cs` (10) | Callback lookup/persistence/ordering, orphan, dedup, before-response | PostgreSQL |
| `ProviderIdempotencyTests.cs` | Same contribution single provider operation, attempt-before-call | PostgreSQL |
| `ProviderConcurrencyTests.cs` (6) | Concurrent submit -> 1 op/1 reference, key conflict, unique attempt, key reuse | PostgreSQL |
| `ReconciliationTests.cs` | Processed-but-response-lost, NotFound on reconciliation | PostgreSQL |
| `ReconciliationDecisionTableTests.cs` (7) | Full decision table + ProviderUnavailable + MaxCount | PostgreSQL |
| `RetryMessageContractTests.cs` (3) | Versioned processing contract, retry-due selection | PostgreSQL |
| `RetrySchedulingTests.cs` (5) | Due/not-due dispatch, concurrent scheduler single dispatch, max -> DLQ, retry count | PostgreSQL |
| `CircuitBreakerIntegrationTests.cs` (4) | Open: no provider/attempt/budget; probe submit | PostgreSQL |
| `CrashRecoveryTests.cs` (3) | Crash after attempt persisted / before response; inbox dedup | PostgreSQL |
| `DatabaseConstraintTests.cs` | Unique idempotency key, tenant isolation, optimistic concurrency | PostgreSQL |
| `LocalStackSqsTests.cs` (5) | Real SQS send/receive/delete, visibility, redelivery, duplicate delivery | LocalStack |
| `FinalE2ETests.cs` (1) | Outbox -> SQS -> Worker -> Provider -> Reconciliation -> callback -> dedup | LocalStack + PostgreSQL + WorkerHost |

## Unit Test Files

- `CircuitBreakerTests.cs` (11): single probe, open/defer, TimeProvider-based
- `ContributionStateMachineTests.cs` (27): all allowed transitions
- `ProviderOperationKeyFactoryTests.cs` (8): stable key, no attempt number, org isolation
- `RetryPolicyTests.cs`: exponential backoff / retryability classification

## Notes

- `PostgreSqlFixture` applies all EF migrations via Testcontainers PostgreSQL.
- `WorkerHostFixture` runs the real worker host (Outbox Publisher, Processing,
  Reconciliation, Scheduled Maintenance) against LocalStack SQS + PostgreSQL.
- SQS adapter fixes verified: MessageType attribute round-trips, ApproximateReceiveCount
  is read from system attributes.
- 预期：第二个抛 DbUpdateConcurrencyException
- 实际：PASS

## Known Limitations

- Integration Tests 使用 Testcontainers PostgreSQL，CI 需要支持 Docker
- LocalStack SQS Integration Tests 尚未实现（Phase 7 深入验证）
- Callback 处理的 Integration Tests 尚未实现（需 API 端到端测试）
- Retry Scheduling 的 Integration Tests 尚未实现（需 Worker 端到端测试）
- SandboxProvider 是 Singleton，跨测试共享状态（通过 EnsureDeleted 重置）
