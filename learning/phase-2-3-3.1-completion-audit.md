# Phase 2、Phase 3、Phase 3.1 完成度审计

> 审计日期：2026-08-11
> 审计方法：清单 → 生产代码 → 可执行测试 → 实验报告 → CI Evidence 五向核对。
> 重要原则：机器验证完成不等于 Owner 已完成人工口试或签字。

## 1. 结论

| 范围 | 实验 | 可发现测试 | 报告 | 当前结论 |
| --- | ---: | ---: | ---: | --- |
| Phase 2 | 12/12 | 16 | 12/12 | 软件与 E2 实验完整；Owner Gate 仍需本人签发 |
| Phase 3 | 15/15 | 25 | 15/15 | 软件与 E1/E2 实验完整；Owner Gate 仍需本人签发 |
| Phase 3.1 | 10 Gates | 163/163 | Evidence Pack 完整 | `817436b` CI SUCCESS |

截至本次审计，没有发现“目录存在但零测试”“有清单但没有报告”或“报告存在但
测试命名空间无法发现”的 Phase 2 / Phase 3 实验。

统一检查命令：

    ./scripts/verify-experiments.ps1 -SkipBuild

完整重放命令：

    ./scripts/verify-experiments.ps1 -Run

该脚本逐个检查 Phase 2 Exp1–Exp12 和 Phase 3 Exp1–Exp15。任一实验测试数为 0，
或对应报告不是恰好一份，命令都会失败。

## 2. 为什么清单仍有很多未勾选

原清单混合了四种不同性质的事项，不能用未勾选总数判断软件是否完成：

| 类型 | 示例 | 本次处理 |
| --- | --- | --- |
| 可机器验证的软件 Gate | Outbox 同事务、Fencing、Callback Dedup | 用测试和报告判定 |
| 通用 Evidence 模板 | 每个实验保存日志、DB、Queue 状态 | 由 27 份聚合报告和新脚本覆盖 |
| Owner 人工 Gate | 口头回答、画图、本人复现、签字 | 保留未签，不由 Agent 冒充 |
| 过时流程建议 | 不在 main 工作、创建学习分支、冻结后续 Phase | 标记为历史流程，不倒推当前代码失败 |

因此，Phase 2 Exp12 后面的 10 个空框和 Phase 3 Exp15 后面的 25 个空框，都是
通用 Evidence 模板，不是 Exp12/Exp15 未完成的步骤。

## 3. Phase 2 实验复现矩阵

| Exp | 场景 | 测试数 | Evidence | 状态 |
| ---: | --- | ---: | --- | --- |
| 1 | DB Commit 后 Publisher Crash | 1 | phase-2/exp1-publisher-crash.md | E2 PASS |
| 2 | Duplicate Publish | 1 | phase-2/exp2-duplicate-publish.md | E2 PASS |
| 3 | Duplicate Delivery | 1 | phase-2/exp3-duplicate-delivery.md | E2 PASS |
| 4 | Worker Crash | 1 | phase-2/exp4-worker-crash.md | E2 PASS |
| 5 | Lease Expiry | 3 | phase-2/exp5-lease-expiry.md | E2 PASS |
| 6 | Poison Message + Controlled Replay | 2 | phase-2/exp6-poison-message.md | E2 PASS |
| 7 | Retry Exhaustion | 1 | phase-2/exp7-retry-exhaustion.md | E2 PASS |
| 8 | Broker Temporarily Unavailable | 1 | phase-2/exp8-broker-temporarily-unavailable.md | E2 PASS |
| 9 | Graceful Shutdown + Checkpoint | 1 | phase-2/exp9-graceful-shutdown.md | E2 PASS |
| 10 | Backlog Growth and Recovery | 1 | phase-2/exp10-backlog-growth-and-recovery.md | E2 PASS |
| 11 | Stale Owner Fencing | 1 | phase-2/exp11-stale-owner-fencing.md | E2 PASS |
| 12 | SQS Visibility Heartbeat | 2 | phase-2/exp12-sqs-visibility-heartbeat.md | E2 PASS |

### 本轮补全的真实 Phase 2 缺口

1. Dead-letter 受控 Replay
   - 原状态：实体和 Repository 有字段，CLI 只输出 not implemented。
   - 补全：显式 --confirm、Operator 身份、租户范围、Pending 原子 claim、
     单次 Replay、新 MessageId、Outbox、AuditEvent、Correlation/Causation。
   - 并发/重复保护：数据库唯一 Dead-letter identity；第二次 Replay 返回
     NotPending，不再制造 Outbox。
   - 安全边界：不允许 jobs retry 直接改写终态 JobRun。

2. Graceful Shutdown Checkpoint
   - 原状态：Exp9 明确观察到 Checkpoint rows = 0。
   - 补全：Provider 前保存 ProviderSubmissionPending；SIGTERM 保存
     ProviderOutcomeUnknown；Worker B 读取断点后使用相同 Provider key 恢复；
     最终保存 Completed。
   - 边界：Checkpoint 不替代 Provider 幂等和 Reconciliation。

3. 实验发现 Gate
   - 原状态：全量测试能通过，但不能证明 27 个实验逐个仍有测试。
   - 补全：CI 新增 scripts/verify-experiments.ps1。

### Phase 2 仍未由软件自动完成的内容

| 内容 | 状态 | 原因/下一步 |
| --- | --- | --- |
| Owner 本人执行与口试 | MANUAL | 必须由 Owner 完成，Agent 不能代签 |
| Owner ACCEPT 决策 | MANUAL | 需要姓名、日期和风险接受 |
| Notification Handler 完整实现 | DEFERRED | 当前是独立的后续业务能力，不影响 Processing 实验结论 |
| 多 Worker 全局 Retry Budget | DEFERRED | 当前已有单任务上限、Backoff/Jitter、Circuit 和批量限制；跨实例预算需独立设计 |
| CLI 的业务终态 reopen | INTENTIONALLY BLOCKED | 通用 Job retry 会破坏终态不变量；需按业务类型建立审批状态机 |
| Real AWS SQS | EXTERNAL | 需要 AWS 账户、IAM、KMS、配额和成本授权 |

## 4. Phase 3 实验复现矩阵

| Exp | 场景 | 测试数 | Evidence | 状态 |
| ---: | --- | ---: | --- | --- |
| 1 | Happy Path with Provider Evidence | 1 | phase-3/exp1-happy-path-provider-evidence.md | E2 PASS |
| 2 | Timeout Before Processing | 1 | phase-3/exp2-timeout-before-processing.md | E2 PASS |
| 3 | Processed but Response Lost | 1 | phase-3/exp3-processed-response-lost.md | E2 PASS |
| 4 | Same SQS Message Redelivery | 1 | phase-3/exp4-same-sqs-message-redelivery.md | E2 PASS |
| 5 | Different MessageId, Same Contribution | 1 | phase-3/exp5-different-message-id-same-contribution.md | E2 PASS |
| 6 | Crash after Provider Processed | 3 | phase-3/exp6-worker-crash-after-provider-processed.md | E2 PASS |
| 7 | Callback Security | 8 | phase-3/exp7-callback-security.md | E1 HTTP PASS |
| 8 | Duplicate Callback | 2 | phase-3/exp8-duplicate-callback.md | E1/E2 PASS |
| 9 | Callback Before Submit Response | 1 | phase-3/exp9-callback-before-submit-response.md | E2 PASS |
| 10 | Concurrent Reconciliation | 1 | phase-3/exp10-concurrent-reconciliation.md | E1 PASS |
| 11 | Circuit Open No ACK | 1 | phase-3/exp11-circuit-open-no-ack.md | E2 PASS |
| 12 | Terminal Conflict / ManualRequired | 1 | phase-3/exp12-terminal-conflict-manual-required.md | E1 PASS |
| 13 | Retry Exhaustion | 1 | phase-3/exp13-retry-exhaustion.md | E2 PASS |
| 14 | Provider Backlog and Recovery | 1 | phase-3/exp14-provider-backlog-and-recovery.md | E2 PASS |
| 15 | Operational History Retention | 1 | phase-3/exp15-operational-history-retention.md | E2 PASS |

### Phase 3 本轮补全

- 新增 observability-contract.md，把每个 Metric/Trace 对应到具体故障实验、定义、
  标签和告警意图。
- 保持 OpenTelemetry、Dashboard、SLI/SLO 为 Phase 4 范围；不为勾 Phase 3
  清单而提前引入另一整套运行时。
- 把所有实验纳入按 Exp 编号的 CI discovery gate。

### Phase 3 仍未由软件自动完成的内容

| 内容 | 状态 | 原因/下一步 |
| --- | --- | --- |
| 20 道 Owner 口试与画图 | MANUAL | 知识掌握不能由测试代替 |
| Owner Gate Decision | MANUAL | 需要 Owner 明确 ACCEPT / VALIDATION / BLOCKED |
| OTel/Metric/Dashboard 实现 | PHASE 4 | Phase 3 只冻结观测契约 |
| 真实 Provider 行为 | EXTERNAL | Sandbox 不能证明真实 Provider 的限流、乱序和 SLA |
| Provider Secret Vault / Rotation | DEFERRED | 需要部署平台与密钥系统 |
| Circuit/Retry 动态配置 | DEFERRED | 当前默认值已验证，生产调参接口尚未开放 |

## 5. Phase 3.1 审计

Phase 3.1 的 10 个 Final Gates 已重新绑定到补全实现提交 `817436b`；GitHub
Actions run `31447327012` 成功。CI 验证 163/163、0 compiler warning、0 known
vulnerable package，并上传 TRX、实验发现摘要和依赖审计。原 146-test 与
162-test 结果保留为历史基线，不再作为当前 SHA 的证明。

本轮已完成：

1. 本地与 CI 均运行统一 verify，取得 163/163；
2. 最终补全实现提交已 push；
3. 该 SHA 的 GitHub Actions 已成功；
4. ci-run.md、README.md、current-state.md 已与当前证据同步；
5. 146-test 记录作为 Historical Baseline 保留。

### Phase 3.1 外部或后续范围

- Real AWS E4 smoke：需要外部账户与权限；
- Azure E3、Backup/Restore：属于部署/灾备阶段；
- Notification Handler：仍是后续业务能力；
- OpenTelemetry、Dashboard、SLI/SLO、k6：属于 Phase 4；
- Owner 最终签发：人工事项。

## 6. “没有能复现的 experiments”清单

当前仓库内：无。

需要区分“当前不能运行”和“仓库没有实现”：

| 条件 | 结果 |
| --- | --- |
| Docker Desktop、PostgreSQL Testcontainer、LocalStack 可用 | 27 个实验均可发现并运行 |
| Docker 不可用 | E1/E2 容器实验无法运行，属于环境阻塞 |
| 没有 AWS/Azure 凭证 | E3/E4 不能运行，但它们不是现有 Phase 2/3 Owner Experiment |
| 只运行 Unit Test | 不能替代 Worker crash、SQS visibility、broker outage 等 E2 证据 |

## 7. Gate 建议

- 软件 Gate：PASS，以 `817436b`、CI run `31447327012` 和 163/163 为准。
- Phase 2 Owner Gate：VALIDATION，等待 Owner 本人复现关键实验并签字。
- Phase 3 Owner Gate：VALIDATION，等待 Owner 口试、图和签字。
- Phase 3.1 Engineering Gate：Completed。
- Phase 4 Entry：技术上可以开始，但 Owner 若坚持原清单流程，应先完成两次人工签发。
