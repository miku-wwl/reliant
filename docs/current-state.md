# Reliant - Current State

> 最后更新：Phase 3.1（Reliability Gate Closure）

## 当前能力状态

| 能力 | Implementation | Verification | Evidence |
| --- | --- | --- | --- |
| 多租户业务核心 | Implemented | Verified | Integration Test: TenantFilter_ShouldIsolateData |
| Contribution 状态机 | Implemented | Verified | Unit Tests: 27 状态机测试 |
| Idempotency | Implemented | Verified | Integration Test: UniqueIndex_ShouldPreventDuplicateIdempotencyKey |
| Outbox/Inbox | Implemented | Verified | Integration Test: AttemptPersisted_BeforeProviderCall |
| Unified Worker Host | Implemented | Partial | Unit Tests only, Integration pending |
| Processing Handler | Implemented | Partial | Unit Tests only |
| Notification Handler | Skeleton | Not Started | - |
| Reconciliation Handler | Implemented | Verified | Integration Test: ProcessedButResponseLost |
| Scheduled Maintenance | Implemented | Partial | Unit Tests only |
| External Provider Adapter | Implemented | Verified | Integration Test: SameContribution_ShouldProduceSingleProviderOperation |
| Unknown Outcome | Implemented | Verified | Integration Test: ProcessedButResponseLost_ShouldConverge |
| Reconciliation | Implemented | Verified | Integration Test: ProviderNotFound_OnReconciliation |
| Callback Security | Implemented | Partial | Unit Tests only |
| Provider Idempotency | Implemented | Verified | Integration Test + Unit Test: 8 KeyFactory tests |
| Circuit Breaker | Implemented | Verified | Unit Tests: 8 CircuitBreaker tests |
| Retry Scheduling | Implemented | Partial | Logic implemented, scheduler test pending |
| Optimistic Concurrency | Implemented | Verified | Integration Test: OptimisticConcurrency_ShouldPreventLostUpdate |
| OpenTelemetry | Not Started | Not Started | - |
| Metrics / Logs / Dashboard | Not Started | Not Started | - |
| SLI/SLO/Error Budget | Not Started | Not Started | - |
| k6 Release Gate | Not Started | Not Started | - |
| Azure E3 部署 | Not Started | Not Started | - |
| LocalStack E2 AWS 验证 | Not Started | Not Started | - |
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

- Unit Tests: 58
- Architecture Tests: 5
- Integration Tests: 7 (Testcontainers + PostgreSQL)
- Total: 70
