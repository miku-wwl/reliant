# Phase 3.1 Consolidated Evidence

> Evidence Level: E1（Testcontainers PostgreSQL）+ E2（LocalStack SQS + WorkerHost）
> Application baseline: `817436b`
> Current main: `079afcb`
> Current CI: [run 31448301607](https://github.com/miku-wwl/reliant/actions/runs/31448301607)
> Status: **Completed — all 10 Final Gates PASS**

本文件聚合原 Phase 3.1 Evidence 子目录中的 Gate、CI、测试、E2E 和限制说明，
避免同一阶段证据拆成大量小文件。Phase 2/3 的逐实验过程保留在 `learning/`。

## 1. Current Test Baseline

| Category | Filter | Count | Status |
| --- | --- | ---: | --- |
| Unit | `Category=Unit` | 65 | PASS |
| Architecture | `Category=Architecture` | 5 | PASS |
| PostgreSQL Integration | `Category=Integration&Dependency=PostgreSQL` | 85 | PASS |
| LocalStack Integration | `Category=Integration&Dependency=LocalStack` | 35 | PASS |
| HTTP API Integration | `Category=Integration&Dependency=HttpApi` | 10 | PASS |
| WorkerHost E2E | `Category=Integration&Dependency=WorkerHost` | 23 | PASS |
| Integration Total | `Category=Integration` | 93 | PASS |
| **Total** | | **163** | **PASS** |

Dependency filters overlap，不能把 85、35、10、23 相加得到 Integration Total。

CI 同时验证：

- restore 和已知漏洞依赖扫描；
- format；
- clean warning-as-error build；
- test category 非零门禁；
- 65 Unit、5 Architecture、93 Integration；
- Phase 2 Exp1–Exp12 和 Phase 3 Exp1–Exp15 每个至少一个测试、恰好一份报告；
- TRX、test summary、experiment summary 和 vulnerability report 上传。

## 2. Final Gate Results

| Gate | Result | 关键可执行证据 | 核心不变量 |
| ---: | --- | --- | --- |
| 1 Provider Idempotency | PASS | `ProviderConcurrencyTests`, `ProviderIdempotencyTests`, `DuplicateMessageE2ETests`, `SafeRetryE2ETests` | 并发、重投和 Retry 下最多一个 Provider Operation/Reference |
| 2 Unknown Outcome | PASS | `FinalE2ETests`, `StateTransitionAuditTests` | Processed-response-lost 最终收敛且 Provider Effect=1 |
| 3 Safe Retry | PASS | `SafeRetryE2ETests` | NotFound 后使用相同 Stable Key 安全重试 |
| 4 Reconciliation | PASS | `ReconciliationDecisionTableTests`, `ReconciliationClosureTests` | 全决策表、并发 apply-once、ManualRequired 未被错误 Resolved |
| 5 Callback | PASS | `CallbackSecurityHttpTests`, `CallbackTests`, `FinalE2ETests` | HMAC/Timestamp、Dedup、Orphan、Ordering、Terminal Conflict |
| 6 Retry Scheduling | PASS | `RetrySchedulingTests`, `RetryMessageContractTests` | due/not-due、并发单 dispatch、max → DLQ |
| 7 Circuit Breaker | PASS | `CircuitBreakerTests`, `CircuitBreakerIntegrationTests`, `CircuitOpenE2ETests` | Open no-call/no-ACK/no-budget，Half-Open single probe |
| 8 Crash Recovery | PASS | `CrashRecoveryTests`, `CrashBeforeAckE2ETests`, `DuplicateMessageE2ETests` | crash-before-ACK 后重投去重，副作用不重复 |
| 9 Integration Evidence | PASS | 23 个 WorkerHost-filtered tests + LocalStack coverage | 真实 SQS send/receive/delete/visibility/redelivery/counter 路径 |
| 10 Documentation & CI | PASS | `verify.ps1`, `verify-experiments.ps1`, CI Artifacts | 零测试、缺报告、漏洞和编译警告不能静默通过 |

## 3. End-to-End Definition of Done

验证链路：

```text
Outbox
→ LocalStack SQS
→ Real Worker Host
→ Sandbox Provider
→ Reconciliation
→ Callback
→ Final State
```

故障注入覆盖：

- Processed-but-response-lost；
- duplicate callback；
- same MessageId redelivery；
- different MessageId same Contribution；
- crash before ACK；
- timeout before provider processing；
- circuit open / half-open recovery；
- Worker crash after Provider processed。

共同断言：

```text
Contribution 最终收敛
ProviderOperationCount == 1
ProviderReferenceCount == 1
Inbox/Dedup 只应用一次
Crash/Circuit 场景 SQS ReceiveCount >= 2
Queue 最终清空
无静默丢失
无重复 Provider Effect
```

## 4. Current CI Evidence

- Commit: `079afcbc5b4faad9f076eafa2e24133ff9f851eb`
- Workflow: `CI`
- Run: https://github.com/miku-wwl/reliant/actions/runs/31448301607
- Job: `93647153985`
- Result: **SUCCESS**（6m36s）
- Build: 0 compiler warnings, 0 errors
- Tests: 163 passed, 0 failed, 0 skipped
- Vulnerable packages: 0 known
- Artifact: `reliant-test-results`
- Artifact id: `9085484573`
- Artifact size: 50,646 bytes
- Artifact sha256: `c6af2c19d747e94367242645a192825cba6aa4f31aa6210fcca3c784e9270792`

Artifact 内容：

```text
test-results/unit.trx
test-results/architecture.trx
test-results/integration.trx
logs/final-e2e.log
test-summary.md
experiment-summary.md
vulnerable-packages.txt
```

## 5. Historical Baseline

2026-08-02 的 Phase 3.1 初始 Closure 为 146 tests：65 Unit、5 Architecture、
76 Integration（含 10 WorkerHost E2E），GitHub Actions run `30720948018` PASS。

之后 Phase 2/3 实验扩展到 163 tests。146 和 162 仅作为历史快照，不作为当前
SHA 的 Evidence。

## 6. Known Limitations

以下限制不阻塞 Phase 3.1 Engineering Gate：

- 尚未执行真实 AWS E4 Smoke；当前 SQS 语义为 LocalStack E2。
- Notification Handler 仍是后续业务能力。
- Provider Secret 尚未接入真实 Vault/KMS 和轮换。
- Retry Policy 与 Circuit 阈值尚未开放生产动态调参。
- Sandbox Provider 不代表真实第三方的全部限流、乱序、延迟和 SLA。
- 本地容器结果不能冒充 Azure E3、AWS E4 或生产容量认证。
- OpenTelemetry、Dashboard、SLI/SLO 和 k6 属于 Phase 4。

## 7. Evidence Navigation

- 当前能力真值：[`../current-state.md`](../current-state.md)
- Phase 2/3/3.1 审计：[`../../learning/phase-2-3-3.1-completion-audit.md`](../../learning/phase-2-3-3.1-completion-audit.md)
- Phase 2 Checklist：[`../../learning/Reliant-Phase-2-Gate-Review-and-Learning-Checklist.md`](../../learning/Reliant-Phase-2-Gate-Review-and-Learning-Checklist.md)
- Phase 3 Checklist：[`../../learning/Reliant-Phase-3-Gate-Review-and-Learning-Checklist.md`](../../learning/Reliant-Phase-3-Gate-Review-and-Learning-Checklist.md)
- Phase 4 Checklist：[`../../learning/Reliant-Phase-4-Gate-Review-and-Learning-Checklist.md`](../../learning/Reliant-Phase-4-Gate-Review-and-Learning-Checklist.md)
