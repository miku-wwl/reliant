# Reliant - Current State

> 最后更新：Phase 3 Experiment 4（Same SQS Message Redelivery）—
> Local Verified；Phase 3.1 CI baseline 仍为 Completed

## Phase 3.1 状态

```text
Phase 3.1 — Completed

All 10 Final Gates PASS (146 tests) with CI evidence:
Outbox -> SQS -> Worker -> Provider -> Reconciliation -> Callback proven
end-to-end over PostgreSQL (Testcontainers) + LocalStack SQS + a real worker host.

Evidence pack: docs/evidence/phase-3.1/
CI evidence: docs/evidence/phase-3.1/ci-run.md
```

当前工作树在 Phase 3.1 基线上完成了 Phase 2 Exp1–Exp12，并开始补充 Phase 3
Owner Experiments。最新本地全量结果：

```text
164 passed, 0 failed, 0 skipped
```

新增代码 push 后需要重新取得 GitHub Actions CI evidence；原 146-test CI
记录保留为 Phase 3.1 历史基线。

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
| JobRun / JobAttempt / Lease | Implemented | Verified | learning/phase-2/exp5-lease-expiry.md |
| Poison Message / Native SQS DLQ | Implemented | Verified | learning/phase-2/exp6-poison-message.md |
| Retry Exhaustion / Backoff / Jitter | Implemented | Verified | learning/phase-2/exp7-retry-exhaustion.md |
| Broker Outage / Outbox Recovery | Implemented | Verified | learning/phase-2/exp8-broker-temporarily-unavailable.md |
| Processing Worker Graceful Shutdown | Implemented | Verified | learning/phase-2/exp9-graceful-shutdown.md |
| Backlog Growth / Scale-out Recovery | Implemented | Verified | learning/phase-2/exp10-backlog-growth-and-recovery.md |
| Stale Owner Fencing Token | Implemented | Verified | learning/phase-2/exp11-stale-owner-fencing.md |
| SQS Visibility + Lease Heartbeat | Implemented | Verified | learning/phase-2/exp12-sqs-visibility-heartbeat.md |
| Provider Happy Path Evidence | Implemented | Verified | learning/phase-3/exp1-happy-path-provider-evidence.md |
| Provider NotFound Safe Retry | Implemented | Verified | learning/phase-3/exp2-timeout-before-processing.md |
| Provider Response Lost Recovery | Implemented | Verified | learning/phase-3/exp3-processed-response-lost.md |
| Same SQS Message Redelivery | Implemented | Verified | learning/phase-3/exp4-same-sqs-message-redelivery.md |
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

- Unit Tests: 65
- Architecture Tests: 5
- PostgreSQL Integration Tests: 71
- LocalStack Integration Tests: 31
- HTTP API Integration Tests: 9
- WorkerHost End-to-End Tests: 25
- Integration Tests Total: 94
- Total Tests: 164
