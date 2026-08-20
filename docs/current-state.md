# Reliant - Current State

> 最后更新：Phase 4 Completion Audit（2026-08-20）—
> Phase 4 Local E1/E2 Verified；Phase 3.1 GitHub CI Baseline 保留

## Phase 4 状态

```text
Phase 4 — Completed locally

OpenTelemetry -> Collector -> Prometheus / Tempo / Loki -> Grafana
API + PostgreSQL + Outbox + SQS + Worker + Provider + Notification 可区分
Telemetry failure is fail-open; health/readiness/version and diagnostics CLI verified

Evidence pack: docs/evidence/phase-4.md
```

## Phase 3.1 状态

```text
Phase 3.1 — Completed

All 10 Final Gates PASS. Current 163-test baseline:
Outbox -> SQS -> Worker -> Provider -> Reconciliation -> Callback proven
end-to-end over PostgreSQL (Testcontainers) + LocalStack SQS + a real worker host.

Evidence pack: docs/evidence/phase-3.1.md
Phase 4 evidence: docs/evidence/phase-4.md
```

补全实现基线 `817436b` 已通过 GitHub Actions
[run 31447327012](https://github.com/miku-wwl/reliant/actions/runs/31447327012)。
本轮在原基线上补全 Dead-letter Replay、Checkpoint、实验 discovery 和依赖安全
Gate。CI 结果：

```text
163 passed, 0 failed, 0 skipped
0 build warnings
0 known vulnerable packages
Phase 2 Exp1–Exp12: 16 executable tests / 12 reports
Phase 3 Exp1–Exp15: 25 executable tests / 15 reports
```

146-test 和 162-test 记录仅作为 Historical Baseline 保留，不冒充本轮
`817436b` 的 163-test CI Evidence。

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
| Controlled Dead-letter Replay | Implemented | Verified | Exp6：explicit claim + Outbox + Audit |
| Retry Exhaustion / Backoff / Jitter | Implemented | Verified | learning/phase-2/exp7-retry-exhaustion.md |
| Broker Outage / Outbox Recovery | Implemented | Verified | learning/phase-2/exp8-broker-temporarily-unavailable.md |
| Processing Worker Graceful Shutdown | Implemented | Verified | learning/phase-2/exp9-graceful-shutdown.md |
| Processing Checkpoint Resume | Implemented | Verified | Exp9：ProviderOutcomeUnknown → Completed |
| Backlog Growth / Scale-out Recovery | Implemented | Verified | learning/phase-2/exp10-backlog-growth-and-recovery.md |
| Stale Owner Fencing Token | Implemented | Verified | learning/phase-2/exp11-stale-owner-fencing.md |
| SQS Visibility + Lease Heartbeat | Implemented | Verified | learning/phase-2/exp12-sqs-visibility-heartbeat.md |
| Provider Happy Path Evidence | Implemented | Verified | learning/phase-3/exp1-happy-path-provider-evidence.md |
| Provider NotFound Safe Retry | Implemented | Verified | learning/phase-3/exp2-timeout-before-processing.md |
| Provider Response Lost Recovery | Implemented | Verified | learning/phase-3/exp3-processed-response-lost.md |
| Same SQS Message Redelivery | Implemented | Verified | learning/phase-3/exp4-same-sqs-message-redelivery.md |
| Different MessageId Business Dedup | Implemented | Verified | learning/phase-3/exp5-different-message-id-same-contribution.md |
| Worker Crash after Provider Processed | Implemented | Verified | learning/phase-3/exp6-worker-crash-after-provider-processed.md |
| Callback HMAC / Replay-window Security | Implemented | Verified | learning/phase-3/exp7-callback-security.md |
| Duplicate Callback Idempotency | Implemented | Verified | learning/phase-3/exp8-duplicate-callback.md |
| Callback-before-response Lost-update Protection | Implemented | Verified | learning/phase-3/exp9-callback-before-submit-response.md |
| Concurrent Reconciliation Single-winner | Implemented | Verified | learning/phase-3/exp10-concurrent-reconciliation.md |
| Circuit-open No-ACK / No-retry-budget | Implemented | Verified | learning/phase-3/exp11-circuit-open-no-ack.md |
| Terminal Conflict / ManualRequired | Implemented | Verified | learning/phase-3/exp12-terminal-conflict-manual-required.md |
| Phase 3 Retry Exhaustion Evidence | Implemented | Verified | learning/phase-3/exp13-retry-exhaustion.md |
| Provider Outage Backlog / Circuit Recovery | Implemented | Verified | learning/phase-3/exp14-provider-backlog-and-recovery.md |
| Operational History Retention / Capacity Guardrails | Implemented | Verified | learning/phase-3/exp15-operational-history-retention.md |
| Optimistic Concurrency | Implemented | Verified | Integration Test: OptimisticConcurrency_ShouldPreventLostUpdate |
| OpenTelemetry | Implemented | Verified E1/E2 | docs/evidence/phase-4.md |
| Trace Context across Outbox/SQS | Implemented | Verified E2 | QueueTracePropagationE2ETests |
| Structured Logs / Redaction / Cardinality | Implemented | Verified E1 | TelemetryContractTests + ADR-0023 |
| Metrics / Grafana Dashboard / Alerts | Implemented | Verified E1 | ops/observability + docs/evidence/phase-4.md |
| Health / Readiness / Deployment Version | Implemented | Verified E1 | ApiOperationalEndpointTests + manual Worker checks |
| Telemetry Fail-open | Implemented | Verified E2 | TelemetryFailOpenE2ETests |
| SLI/SLO/Error Budget | Not Started | Not Started | - |
| k6 Release Gate | Not Started | Not Started | - |
| Azure E3 部署 | Not Started | Not Started | - |
| LocalStack E2 AWS 验证 | Implemented | Verified | E2E: FinalE2ETests + LocalStackSqsTests |
| Real AWS E4 Smoke | Not Started | Not Started | - |
| Azure Backup/Restore | Not Started | Not Started | - |
| CI Pipeline | Implemented | Verified | GitHub Actions CI |
| Experiment Discovery Gate | Implemented | Verified | scripts/verify-experiments.ps1 |
| Vulnerable Dependency Gate | Implemented | Verified | scripts/verify.ps1 |
| Architecture Tests | Implemented | Verified | 5 Architecture tests |
| Terraform 基线 | Implemented | Verified | Phase 0 Stage D |
| reliantctl CLI | Implemented for current operations | Verified E1 | diagnostics/inspect/list/replay + CLI help |

## 规则

- 所有未实现能力不得提前声明为已完成；
- 每个能力变为"已实现"时，必须提供 Evidence 链接并标明 E1/E2/E3/E4 级别；
- 此文件在每个 Phase 完成后更新。

## 测试统计

- Unit Tests: 68
- Architecture Tests: 5
- PostgreSQL Integration Tests: 86
- LocalStack Integration Tests: 37
- HTTP API Integration Tests: 10
- WorkerHost End-to-End Tests: 24
- Integration Tests Total: 96
- Phase 4 Tests: 6
- Total Tests: 169

Dependency filters overlap（例如 WorkerHost E2E 同时计入 PostgreSQL 和
LocalStack），不能把 86、37、10、24 相加当作 Integration Total。
