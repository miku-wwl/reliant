# Reliant - Current State

> 最后更新：Phase 3.1（Reliability Gate Closure）

## Phase 3.1 状态

```text
Phase 3.1 — In Progress

Core reliability mechanisms implemented and verified end-to-end:
Outbox -> SQS -> Worker -> Provider -> Reconciliation -> Callback.
```

## 当前能力状态

| 能力 | Implementation | Verification | Evidence |
| --- | --- | --- | --- |
| 多租户业务核心 | Implemented | Verified | Integration Test: TenantFilter_ShouldIsolateData |
| Contribution 状态机 | Implemented | Verified | Unit Tests: 27 状态机测试 |
| Idempotency | Implemented | Verified | Integration Test: UniqueIndex_ShouldPreventDuplicateIdempotencyKey |
| Outbox/Inbox | Implemented | Verified | Integration Test: AttemptPersisted_BeforeProviderCall |
| Unified Worker Host | Implemented | Verified | E2E: FinalE2ETests (LocalStack SQS + PostgreSQL) |
| Processing Handler | Implemented | Verified | E2E: Outbox -> SQS -> Worker |
| Notification Handler | Skeleton | Not Started | - |
| Reconciliation Handler | Implemented | Verified | Integration: ReconciliationDecisionTableTests (7) |
| Scheduled Maintenance | Implemented | Verified | Integration: RetrySchedulingTests (并发 Scheduler) |
| External Provider Adapter | Implemented | Verified | Integration: ProviderConcurrencyTests |
| Unknown Outcome | Implemented | Verified | E2E: ProcessedResponseLost 收敛 Succeeded, Provider Effect=1 |
| Reconciliation | Implemented | Verified | Decision Table 全覆盖 + ProviderUnavailable + MaxCount |
| Callback | Implemented | Verified | Integration: CallbackTests (Reference/Key/Orphan/Dedup/并发) |
| Provider Idempotency | Implemented | Verified | Atomic GetOrAdd + UNIQUE 约束 + 并发测试 |
| Circuit Breaker | Implemented | Verified | Deferred 语义 + 单 Probe + TimeProvider + Integration |
| Retry Scheduling | Implemented | Verified | 原子 Claim + TimeProvider + 并发 Scheduler |
| Crash Recovery | Implemented | Verified | Fault Injection + CrashRecoveryTests |
| Optimistic Concurrency | Implemented | Verified | Integration Test: OptimisticConcurrency_ShouldPreventLostUpdate |
| OpenTelemetry | Not Started | Not Started | - |
| Metrics / Logs / Dashboard | Not Started | Not Started | - |
| SLI/SLO/Error Budget | Not Started | Not Started | - |
| k6 Release Gate | Not Started | Not Started | - |
| Azure E3 部署 | Not Started | Not Started | - |
| LocalStack E2 AWS 验证 | Implemented | Verified | E2E: FinalE2ETests + LocalStackSqsTests |
| Real AWS E4 Smoke | Not Started | Not Started | - |
| Azure Backup/Restore | Not Started | Not Started | - |
| CI Pipeline | Implemented | Verified | GitHub Actions CI |
| Architecture Tests | Implemented | Verified | 5 Architecture tests |
| Terraform 基线 | Implemented | Verified | Phase 0 Stage D |
| reliantctl CLI | Skeleton | Not Started | - |

## 规则

- 所有未实现能力不得提前声明为已完成；
- 每个能力变为"已实现"时，必须提供 Evidence 链接并标明 E1/E2/E3/E4 级别；
- 此文件在每个 Phase 完成后更新。

## 测试统计

- Unit Tests: 61
- Architecture Tests: 5
- Integration Tests (PostgreSQL): 45
- Integration Tests (LocalStack SQS): 5
- End-to-End (WorkerHost + LocalStack + PostgreSQL): 1
- Total: 117
