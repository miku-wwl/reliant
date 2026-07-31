# Reliant — Phase Plan and Engineering Governance V3.2 Resource-Aware Final

> **Project 2:** Multi-Cloud SaaS Reliability Engineering System  
> **North Star role:** Senior Site Reliability Engineer  
> **Primary stack:** .NET 10, PostgreSQL, Messaging, Terraform, OpenTelemetry, Azure, AWS  
> **Delivery model:** Greenfield + Evidence-driven Phase Gates + AI Pair Engineering

---

# 1. 规划原则

## 1.1 Greenfield 原则

Reliant 是一个全新项目：

- 不继承旧仓库、旧数据库、旧 Terraform State 或旧提交历史；
- 不设置旧项目迁移或兼容任务；
- 所有可靠性能力必须在 Reliant 仓库内重新实现和验证；
- 不因为 AI 已生成代码，就视为功能完成；
- 不把 Mock、截图或聊天记录当作正式运行证据；
- 不把“功能可用”自动等同于“可靠性已验证”。

## 1.2 项目主线

Reliant 的主线不是建设 SRE Control Plane，而是：

```text
Build a real SaaS system
→ Operate it
→ Break it
→ Diagnose it
→ Recover it
→ Prevent recurrence
```

每个 Phase 都必须交付真实业务能力和可靠性证据。

## 1.3 交付单位

采用：

```text
Vision
→ Phase
→ Capability Slice
→ Failure Model
→ Verification
→ Evidence
→ Gate Decision
```

不再使用大量 Day 编号。

## 1.4 工程周定义

- 1 个工程周约为 20–25 小时高质量投入；
- AI 用于脚手架、实现、测试初稿、文档和重复性工作；
- 真实云、性能、故障、恢复和数据一致性验证不能由 AI 替代；
- Phase 未通过 Gate 时继续修复当前 Phase；
- 不通过新增 Phase 制造表面进度。

---

# 2. Release 规划

## 2.1 R1 — Portfolio Release

**推荐工期：10–12 周 + 2 周缓冲**

R1 必须形成完整闭环：

```text
Tenant
→ Contribution Request
→ PostgreSQL Transaction
→ Outbox
→ Queue
→ Unified Worker Host
   ├── Processing Handler
   ├── Reconciliation Handler
   └── Notification / Webhook Handler
→ External Provider / Notification Endpoint
→ SLO
→ Failure
→ Recovery
→ Preventive Gate
```

R1 完成后应足以支撑：

- Senior SRE 面试；
- 分布式系统可靠性讨论；
- 数据库、队列和 Worker 故障分析；
- Azure/AWS 部署与取舍；
- SLO、Incident、RCA 和恢复演练；
- k6、CI/CD 和数据库 Migration 深挖。

## 2.2 R2 — Extended Reliability Release

**额外工期：4–8 个工程周**

可选能力：

- Cross-cloud Restore；
- 更复杂 Provider；
- Advanced Autoscaling；
- Redis / Cache Failure；
- 更复杂 Webhook；
- Multi-region Read-only；
- Advanced Security；
- 更多 Chaos Experiments；
- 更完整前端。

R2 不阻塞 R1 求职价值。

---


## 2.3 已有资源与验证策略

已知资源：

- LocalStack Ultimate：AWS-compatible 本地服务、Terraform 和故障验证；
- Azure Student Credit：100 美元；
- Docker Desktop、Local PostgreSQL、GitHub Actions。

验证优先级：

```text
E1 Testcontainers
→ E2 LocalStack Ultimate
→ E3 Real Azure
→ E4 Optional Real AWS Smoke
```

LocalStack 用于提高频率和覆盖率；Azure 用于真实云证据。LocalStack 结果不得冒充真实 AWS 结果。


# 3. Phase 总览

| Phase | 名称 | 推荐工期 | Release |
| --- | --- | ---: | --- |
| Phase 0 | Greenfield Foundation and Reliability Contracts | 1 周 | R1 |
| Phase 1 | Multi-Tenant SaaS Core and Business Invariants | 1–1.5 周 | R1 |
| Phase 2 | Reliable Asynchronous Processing | 1–1.5 周 | R1 |
| Phase 3 | External Provider and Reconciliation | 1 周 | R1 |
| Phase 4 | Operability and End-to-End Observability | 1–1.5 周 | R1 |
| Phase 5 | SLI, SLO, Performance and Release Safety | 1–1.5 周 | R1 |
| Phase 6 | Azure Production-Like Environment | 1–1.5 周 | R1 |
| Phase 7 | LocalStack Ultimate AWS Validation and Optional Real AWS Smoke | 1 周 | R1 |
| Phase 8 | Failure Engineering and Recovery | 2 周 | R1 |
| Phase 9 | Security, Hardening and Portfolio Release | 1 周 | R1 |
| Phase 10 | Extended Multi-Cloud Resilience | 4–8 周 | R2 |

---

# 4. 每个 Phase 的统一生命周期

| Stage | 目标 | 占比 |
| --- | --- | ---: |
| A. Discovery | 调研约束、业务风险、云行为和失败语义 | 10%–15% |
| B. Design | ADR、数据模型、不变量、接口和验收标准 | 15%–20% |
| C. Build | 实现最小纵向业务与可靠性能力 | 40%–45% |
| D. Verify | 自动化、负向、负载、真实云和恢复验证 | 20%–25% |
| E. Review | Fresh-context Review、风险更新和 Gate | 10% |

---

# 5. Phase 0 — Greenfield Foundation and Reliability Contracts

## 目标

建立全新仓库、工程边界、业务目标、可靠性原则、CI 和 AI 结对协议。

## 推荐工期

**1 个工程周**

## Stage A — Discovery

- 明确业务场景；
- 明确 R1 用户旅程；
- 确定支付模拟边界；
- 确定部署单元；
- 确定 `local`、`localstack-aws`、`azure-real` 和 `aws-real-smoke` Profile；
- 盘点 LocalStack Ultimate 当前实际可用服务；
- 确定 Azure 100 美元额度预算；
- 建立 Azure Budget Threshold、Expiry Tag 和 Cleanup Gate；
- 建立 LocalStack Reset 和真实云防误连保护；
- 建立初始风险登记。

## Stage B — Design

- `docs/current-state.md`（Stage A 已完成）；
- ADR-0001：System Architecture（已完成）；
- ADR-0002：Business Invariants（已完成）；
- ADR-0003：Deployment Boundaries（已完成）；
- ADR-0004：Evidence and State Ownership（已完成）；
- AI Pair Programming Protocol（本文件第17节，已存在）；
- Test and Evidence Level Strategy（ADR-0004 已覆盖）；
- LocalStack Compatibility Policy（ADR-0003 已覆盖）；
- Azure 100 USD Budget Plan（`docs/architecture/environment-baseline.md` 已覆盖）；
- Cost and Cleanup Policy（ADR-0003 + `docs/architecture/environment-baseline.md` 已覆盖）。

Solution 结构以 ADR-0001 为准。

## Stage C — Build

- 创建仓库；
- 创建 Solution；
- PostgreSQL；
- Migration Host；
- Docker Compose；
- LocalStack Ultimate Profile；
- `tflocal` 或集中式 AWS Endpoint Configuration；
- Azure Budget/Expiry Validation；
- CI；
- Architecture Tests；
- 统一验证脚本；
- Evidence Schema；
- 基础 Terraform 目录；
- 基础 CLI；
- OpenAPI 基线。

## Stage D — Verify

- clean restore/build/test；
- Architecture Test 负向夹具；
- Migration 空库和重复执行；
- Terraform fmt/validate；
- LocalStack Health / Apply / Reset；
- 防止 LocalStack Profile 误连真实 AWS；
- Azure Budget Guard 配置验证；
- Secret Scan；
- Dependency Scan；
- Container Build；
- 故意破坏 CI 验证阻断；
- 本地环境完整启动和清理。

## Stage E — Review

- Fresh-context 架构 Review；
- Owner 决定 ACCEPT / VALIDATION / BLOCKED。

## Gate

- 只有一套权威规划；
- 业务不变量已明确；
- Public API Host、Unified Worker Host 和独立 Migrator 边界明确；
- CI 与本地验证一致；
- Startup 不执行 Migration；
- 未实现能力没有提前声明；
- Evidence 目录支持 E1/E2/E3/E4 标签；
- LocalStack Ultimate 可运行最小 AWS Terraform；
- Azure 100 美元额度有预算、预警、Expiry 和 Cleanup 边界；
- 模拟与真实云使用独立 State。

---

# 6. Phase 1 — Multi-Tenant SaaS Core and Business Invariants

## 目标

建立真实可用的多租户业务核心。

## 推荐工期

**1–1.5 个工程周**

## Stage A — Discovery

- 定义 Tenant、Membership、Campaign、Contribution；
- 明确业务状态机；
- 明确 Idempotency；
- 明确并发写入风险；
- 明确 Tenant Boundary。

## Stage B — Design

ADR：

- Tenant and Membership；
- Contribution State Machine；
- Idempotency；
- Optimistic Concurrency；
- Audit Model；
- API Contract。

状态机：

```text
Created
→ Accepted
→ Processing
→ Succeeded
→ ReceiptPending
→ Completed
```

失败和不确定路径：

```text
Processing → RetryPending → Processing
Processing → ProviderUnknown → ReconciliationPending
Processing → Failed
```

## Stage C — Build

- Organization；
- Membership；
- Campaign；
- Contribution；
- IdempotencyRecord；
- StateTransition；
- AuditEvent；
- OIDC；
- TenantContext；
- RBAC；
- Create/Query API；
- Problem Details；
- Rate Limit；
- Database Constraints；
- ETag / Version。

## Stage D — Verify

- Tenant A/B 隔离；
- IDOR；
- Client Tenant Forgery；
- Duplicate Request；
- Concurrent Submit；
- Invalid State Transition；
- Retry/Cancel Race；
- Wrong Role；
- Missing Context；
- Stable Error Contract；
- Migration Tests。

## Stage E — Review

- 安全 Review；
- 数据模型 Review；
- API Contract Review；
- Business Invariant Review。

## Gate

- 重复请求不产生重复 Contribution；
- 状态机不可绕过；
- Tenant 隔离 fail closed；
- 关键写入有审计；
- 并发更新有明确行为；
- API 契约稳定；
- 数据库约束保护核心不变量。

---

# 7. Phase 2 — Reliable Asynchronous Processing

## 目标

建立 Outbox、Queue、Unified Worker Host、Inbox、Retry、Dead-letter 和恢复能力。Processing、Notification、Reconciliation 与 Maintenance 作为同一 Host 内的独立 Handler 运行。

## 推荐工期

**1–1.5 个工程周**

## Stage A — Discovery

- 研究 At-least-once Delivery；
- 确定 Outbox/Inbox；
- 定义 Message ID / Correlation / Causation；
- 定义 Retry Error Category；
- 定义 Lease、Heartbeat 和 Checkpoint。

## Stage B — Design

ADR：

- Outbox and Inbox；
- Worker Execution Model；
- Retry and Dead-letter；
- Graceful Shutdown；
- Message Versioning；
- Backpressure。

## Stage C — Build

- OutboxMessage；
- Outbox Publisher；
- Queue Adapter；
- LocalStack Ultimate SQS Adapter；
- Visibility Timeout / Redelivery / DLQ Test Harness；
- InboxMessage；
- Unified Worker Host；
- Processing Handler；
- Notification / Webhook Handler；
- Reconciliation Handler；
- Scheduled Maintenance Handler；
- Handler-specific Queue、Concurrency、Retry 和 Metrics；
- JobRun；
- JobAttempt；
- Lease；
- Heartbeat；
- Checkpoint；
- Retry with Jitter；
- DeadLetterRecord；
- CLI：
  - `jobs inspect`
  - `jobs retry`
  - `deadletter list`
  - `deadletter replay`

## Stage D — Verify

- DB Commit 后 Publisher Crash；
- Duplicate Publish；
- Duplicate Delivery；
- Worker Crash；
- Lease Expiry；
- Queue Redelivery；
- Poison Message；
- Retry Exhaustion；
- Graceful Shutdown；
- Message Version；
- Broker Temporarily Unavailable；
- LocalStack SQS Visibility Expiry；
- LocalStack DLQ Redrive；
- Backlog Growth；
- Recovery。

## Stage E — Review

- 不能声称 Exactly-once Delivery；
- 验证 Exactly-once Business Effect；
- 验证失败不会静默丢失。

## Gate

- DB 状态和 Outbox 原子提交；
- 重复消息不重复副作用；
- Unified Worker Host Crash 后任务可恢复，且 Handler 之间的失败不会静默污染其他处理链路；
- Poison Message 不阻塞队列；
- Retry 有上限和分类；
- Dead-letter 可审计和 Replay；
- SQS 关键语义已有 E2 LocalStack Evidence；
- Graceful Shutdown 有 Evidence。

---

# 8. Phase 3 — External Provider and Reconciliation

## 目标

建立外部 Provider、Unknown Outcome 和 Reconciliation。

## 推荐工期

**1 个工程周**

## Stage A — Discovery

- 研究 Provider Timeout；
- 明确 Provider Idempotency；
- 定义 Callback；
- 定义 Unknown Outcome；
- 定义 Reconciliation。

## Stage B — Design

ADR：

- Provider Adapter；
- Error Classification；
- Unknown Outcome；
- Callback Security；
- Reconciliation Policy；
- Circuit Breaker。

## Stage C — Build

- Provider Contract；
- Sandbox/Simulation Provider；
- Submit；
- Query Status；
- Callback；
- Signature；
- ProcessingAttempt；
- ProviderReference；
- ReconciliationRecord；
- Scheduled Reconciliation；
- Unknown State；
- Circuit Breaker；
- Retry Budget。

## Stage D — Verify

- 429；
- 5xx；
- Timeout；
- Connection Reset；
- Malformed Response；
- Slow Response；
- Duplicate Callback；
- Callback Before Response；
- Wrong Signature；
- Expired Credential；
- Provider Processed but Response Lost；
- Reconciliation Resolution。

## Stage E — Review

- 验证未知结果不能盲目 Retry；
- 验证 Provider Failure 不拖垮整个系统。

## Gate

- Provider Timeout 不产生重复业务结果；
- Unknown Outcome 有明确状态；
- Reconciliation 可收敛状态；
- Callback 有签名和 Replay Protection；
- Retry 不制造 Storm；
- Circuit Breaker 有可观测状态；
- Provider Error 有稳定分类。

---

# 9. Phase 4 — Operability and End-to-End Observability

## 目标

建立真正可操作、可诊断的系统。

## 推荐工期

**1–1.5 个工程周**

## Stage A — Discovery

- 定义 RED/USE；
- 定义 Business Correctness Metrics；
- 定义 Trace Context；
- 定义敏感信息边界；
- 选择 OTel Collector / Grafana Stack。

## Stage B — Design

ADR：

- Telemetry Architecture；
- Trace Propagation；
- Logging Schema；
- Metric Naming；
- Cardinality；
- Telemetry Failure Behavior；
- Health/Readiness。

## Stage C — Build

- OpenTelemetry；
- API Trace；
- PostgreSQL Trace；
- Queue Produce/Consume Trace；
- Worker Trace；
- Provider Trace；
- Notification Trace；
- Structured Logs；
- Metrics；
- Health；
- Readiness；
- Graceful Shutdown；
- Deployment Version；
- Grafana Dashboard；
- Diagnostics CLI。

## Stage D — Verify

完整 Trace：

```text
HTTP
→ PostgreSQL
→ Outbox
→ Queue
→ Unified Worker / Processing Handler
→ Provider
→ Queue
→ Unified Worker / Notification Handler
→ Webhook
```

验证：

- Trace Context 跨 Queue；
- Correlation/Causation；
- Sensitive Data Redaction；
- High Cardinality；
- OTel Collector Failure；
- Log/Metric/Trace 一致；
- Dashboard 能定位一个真实故障。

## Stage E — Review

- 随机选择一个故障，仅使用 Telemetry 调查；
- 删除不可行动的 Dashboard。

## Gate

- 端到端 Trace 完整；
- API、DB、Queue、Unified Worker 内各 Handler、Provider 可区分；
- Telemetry Failure 不阻塞业务；
- 日志无 Secret/敏感 Payload；
- Dashboard 回答影响、时间、位置、变化和恢复；
- 每个重要告警候选有 Owner 和行动。

---

# 10. Phase 5 — SLI, SLO, Performance and Release Safety

## 目标

建立 SLO、Error Budget、k6 和发布安全门禁。

## 推荐工期

**1–1.5 个工程周**

## Stage A — Discovery

- 选择用户旅程 SLI；
- 定义正确性 SLI；
- 研究 Multi-window Burn-rate；
- 建立容量假设；
- 定义 Release Gate。

## Stage B — Design

ADR：

- SLI/SLO；
- Error Budget Policy；
- Alert Policy；
- k6 Test Model；
- Release Risk；
- Deployment Marker。

## Stage C — Build

SLI：

- API Availability；
- API Latency；
- Processing Freshness；
- Notification Delivery；
- Duplicate Result；
- Stuck Transaction；
- Reconciliation Difference。

实现：

- SLO Recording Rules；
- Error Budget；
- Burn Rate；
- Deployment Marker；
- k6 Smoke/Load/Spike/Soak；
- GitHub Actions Gate；
- Release Summary。

## Stage D — Verify

- Low Traffic；
- Missing Data；
- Window Boundary；
- Deployment 5xx Regression；
- Latency Regression；
- Queue Freshness Burn；
- Error Budget Recovery；
- k6 Threshold Failure；
- Rollback 后恢复；
- 外部 Provider 与内部问题分类。

## Stage E — Review

- 删除 Vanity Metric；
- 验证 Threshold 与 SLO/容量相关。

## Gate

- 至少四个 SLI 可计算；
- 至少一个正确性 SLI；
- Burn Rate 有测试；
- Deployment 与 SLO 变化关联；
- k6 能阻断明确性能回归；
- 告警可行动；
- Error Budget Policy 可解释。

---

# 11. Phase 6 — Azure Production-Like Environment

## 目标

使用 Azure 学生订阅的 100 美元额度建立真实 Production-like 环境，并完成部署、故障、Rollback、Backup/Restore 和清理证据。

## 推荐工期

**1–1.5 个工程周**

## Stage A — Discovery

根据当前官方文档和学生订阅实际可用性验证：

- Container Apps；
- PostgreSQL；
- Service Bus；
- Key Vault；
- Managed Identity；
- OTel / Application Insights；
- Backup / Restore；
- 区域和配额；
- 最低合适 SKU；
- 预计每次实验成本。

Redis 和 Front Door 不是 R1 必需项。

## Stage B — Design

ADR：

- Azure Deployment；
- Identity；
- Network；
- Service Bus Semantics；
- Backup；
- Azure Student Credit Budget；
- Telemetry Sampling and Retention；
- Cost and Cleanup。

预算策略：

- 计划内实验最多使用 70% 额度；
- 至少保留 30% 用于失败重建、Restore 和最终 Demo；
- 设置预算预警；
- 所有资源包含 Owner、Purpose、Expiry、Environment Tag；
- 数据库和应用环境不长期空闲运行；
- 每次实验有预估成本、开始时间和 Destroy 截止时间。

## Stage C — Build

Terraform：

- Resource Group；
- Network；
- Container Apps；
- PostgreSQL；
- Service Bus；
- Key Vault；
- Managed Identity；
- Registry；
- Telemetry；
- Object Storage；
- Backup；
- Budget / Cost Alert；
- Expiry Tags。

CI/CD：

- Build Once；
- Artifact Promotion；
- Migration Step；
- Smoke；
- Canary/Revision；
- Rollback；
- Deployment Marker；
- Scheduled Cleanup Check。

## Stage D — Verify

- Apply；
- Deploy；
- Migrate；
- End-to-End；
- OIDC；
- Managed Identity；
- Queue；
- Database；
- Secret；
- Scale；
- Restart；
- Rollback；
- Backup；
- Restore 到独立环境；
- Restore 后业务不变量和 Reconciliation；
- Destroy；
- Cloud Residual Resource Check；
- 额度使用记录。

## Stage E — Review

- 安全；
- 成本；
- 恢复；
- 运行手册；
- 是否需要缩减 Telemetry 或 SKU。

## Gate

- 同一 Artifact 可晋级；
- 无长期 Cloud Secret；
- Migration 独立执行；
- Azure Queue 行为有 E3 真实测试；
- Rollback 可用；
- Backup/Restore 有 E3 Evidence；
- 环境可 Terraform 重建和销毁；
- 无未解释残留资源；
- 额度未突破计划阈值；
- 至少保留最终 Demo 和意外重建预算。

# 12. Phase 7 — LocalStack Ultimate AWS Validation and Optional Real AWS Smoke

## 目标

使用 LocalStack Ultimate 深度验证 AWS Terraform、SQS、IAM/STS、Secrets Manager、S3、Telemetry Contract 和应用部署路径；根据账户、预算和时间选择执行短生命周期真实 AWS Smoke。

## 推荐工期

**1 个工程周**

## Stage A — Discovery

盘点当前本地版本实际行为：

- SQS；
- IAM / STS；
- Secrets Manager；
- S3；
- CloudWatch-compatible APIs；
- ECS-compatible 控制面；
- RDS-compatible 控制面或可用数据面；
- Fault Injection / Service Control；
- Cloud Pod 或等价 Snapshot（可选）。

每项服务必须通过最小实验确认，不能仅根据服务列表假设完整兼容。

## Stage B — Design

ADR：

- LocalStack Evidence Boundary；
- AWS Provider Endpoint Strategy；
- `tflocal` vs Provider Endpoint Configuration；
- SQS Semantics；
- LocalStack State and Reset；
- Real AWS Smoke Boundary；
- Resume Claim Policy。

Profile：

```text
localstack-aws
aws-real-smoke
```

保护措施：

- LocalStack Profile 禁止连接真实 AWS；
- Real AWS Profile 默认禁用；
- State 独立；
- Account / Region Guard；
- Cost Cap；
- Destroy Deadline。

## Stage C — Build

LocalStack Ultimate Terraform：

- SQS + DLQ；
- IAM / STS；
- Secrets Manager；
- S3 Evidence Storage；
- CloudWatch-compatible Resources；
- ECS/RDS 相关可验证资源；
- Application Configuration；
- Health Check；
- Reset / Cleanup。

CI：

- 启动 LocalStack；
- Wait for Health；
- Terraform Apply；
- 部署或启动相同 Artifact；
- End-to-End；
- Contract / Failure Tests；
- Terraform Destroy / Reset；
- Residual Check。

可选 Real AWS Smoke：

- 最小 SQS；
- IAM Role；
- 最小 API/Worker Runtime 或受控数据面；
- 一次业务请求；
- Evidence；
- 立即 Destroy。

## Stage D — Verify

E2 必做：

- Terraform Apply / Destroy；
- SQS Visibility Timeout；
- Message Redelivery；
- Dead-letter；
- IAM / STS Failure；
- Secrets Read；
- S3 Evidence；
- CloudWatch-compatible Contract；
- Same Artifact / Configuration Contract；
- Service Unavailable；
- API Throttling or Injected Failure；
- Reset 后无残留状态。

E4 可选：

- 真实 SQS；
- 真实 IAM Role；
- 最小端到端 Smoke；
- Destroy；
- 真实云与 LocalStack 差异记录。

## Stage E — Review

- 哪些结论只属于 E2；
- 哪些功能仍需要真实 AWS；
- LocalStack 特殊配置是否泄漏到业务代码；
- 是否存在误连真实 AWS 风险；
- 简历措辞是否与 Evidence 匹配。

## Gate

R1 Core：

- 同一 Artifact 可在 LocalStack AWS-compatible Profile 运行；
- AWS Terraform 可 Apply / Destroy；
- SQS 关键语义有 E2 Evidence；
- IAM/STS、Secrets Manager 和 S3 有 E2 Evidence；
- 至少一个 AWS-compatible 故障与恢复；
- LocalStack 和 Azure Evidence 明确区分；
- 环境可 Reset 且无残留；
- README 不把 E2 写成真实 AWS。

R1+ AWS Verified：

- 完成 E4 Real AWS Smoke；
- 收集真实 SQS、IAM 和最小业务链路 Evidence；
- 完整 Destroy；
- 才允许使用 “validated across Azure and AWS”。

# 13. Phase 8 — Failure Engineering and Recovery

## 目标

执行六个可重复的 R1 核心故障场景，并证明检测、诊断、恢复和防复发闭环。Azure Backup / Restore 作为独立恢复演练，不占用六个场景名额。

## 推荐工期

**2 个工程周**

## Stage A — Discovery

每个场景定义：

- Hypothesis；
- Blast Radius；
- Injection；
- Expected Signal；
- Safety Stop；
- Recovery Objective；
- Cleanup。

## Stage B — Design

统一模板：

```text
Scenario
Hypothesis
Preconditions
Injection
Expected Signals
User Impact
Detection
Diagnosis
Mitigation
Recovery
Preventive Control
Residual Risk
```

R1 六个必做场景：

1. Deployment Regression；
2. Database Saturation and Lock Contention；
3. Queue Backlog；
4. Worker Crash and Duplicate Redelivery；
5. Provider Timeout and Unknown Outcome；
6. Migration Failure。

Stretch / R2：

- Poison Message；
- Provider 429 / 5xx；
- Webhook Failure；
- Telemetry Backend Failure；
- Cache Failure；
- 更多 Cloud-specific Failure。

这些 Stretch 场景可以保留自动化负向测试，但不要求全部形成完整 Incident Evidence Pack。

## Stage C — Build and Execute

为六个必做场景建立：

- E1 Testcontainers Baseline；
- E2 LocalStack Ultimate AWS-compatible Failure Loop；
- E3 Azure 关键真实云验证；
- 自动注入脚本；
- k6；
- Provider Simulator；
- Worker Kill；
- Queue Delay；
- DB Saturation / Lock Fixture；
- Migration Failure Fixture；
- Incident Report；
- Runbook；
- Recovery Validation；
- Preventive Test/Gate。

同时引用 Phase 6 已完成的 Azure Backup / Restore Evidence。

## Stage D — Verify

每个必做场景必须有：

- Baseline；
- Failure Evidence；
- Detection；
- Diagnosis；
- Mitigation；
- Recovery；
- Cleanup；
- Regression Test；
- Preventive Control；
- Time Metrics。

量化：

- MTTD；
- MTTR；
- Backlog Recovery；
- Rollback Time；
- Duplicate Prevention；
- Data Correctness。

独立恢复验证：

- Azure Restore Time；
- Restore 后 Schema；
- Tenant Isolation；
- Business Invariants；
- Reconciliation。

## Stage E — Review

- Fresh-context Failure Review；
- 从 Pushpay 面试官角度审查；
- 删除无法重复的故事性结论；
- Stretch 场景不得冒充 R1 必做场景；
- E2 LocalStack Evidence 不得冒充 E4 Real AWS Evidence。

## Gate

- 六个 R1 必做场景全部可重复；
- Incident Timeline 来自真实事件；
- Database 故障能通过 Trace、Metrics 和 k6 定位；
- Worker Crash / Redelivery 不产生重复业务结果；
- Unknown Outcome 可 Reconcile；
- Migration Failure 会阻断 Release；
- Rollback 后业务与指标恢复；
- Azure Restore 后业务不变量验证通过；
- 至少三个故障进入自动 Gate；
- Evidence 可由第三方复验。

# 14. Phase 9 — Security, Hardening and Portfolio Release

## 目标

完成 R1 安全、供应链、运行和展示闭环。

## 推荐工期

**1 个工程周**

## Stage A — Discovery

盘点：

- R1 Blocker；
- Accepted Risk；
- R2；
- 简历陈述；
- Demo 路径。

## Stage B — Design

- Production Readiness Checklist；
- Security Review；
- Release Checklist；
- Demo Script；
- Fresh-machine Validation；
- Known Limitations。

## Stage C — Build

- SBOM；
- Container Scan；
- Dependency Scan；
- Rate Limit；
- Error Redaction；
- Webhook Security；
- Secret Rotation Runbook；
- Terraform Remote State；
- Dashboards；
- Runbooks；
- Evidence Index；
- README；
- Demo；
- Release Tag。

## Stage D — Verify

- Fresh Clone；
- Build/Test；
- Azure Deployment and Restore Evidence；
- AWS Repeatable Deployment and Failure Evidence；
- Failure Demo；
- Recovery Demo；
- Secret Scan；
- Azure Backup / Restore Evidence；
- README Commands；
- Cleanup；
- 10–15 分钟 Demo Rehearsal。

## Stage E — Review

- Go/No-Go；
- 简历逐句证据核对；
- Owner 签发 R1。

## Gate

- README 可复验；
- Demo 稳定；
- Azure/AWS 有真实证据；
- 核心 Failure Pack 完整；
- 高风险操作有 RBAC/Audit；
- Azure Backup / Restore 已演练；
- AWS Restore 未完成时明确标记为 Buffer 或 R2；
- 所有简历主张有 Evidence；
- 未完成能力明确标记；
- R1 Release Tag 已发布。

---

# 15. Phase 10 — Extended Multi-Cloud Resilience

## 目标

在 R1 成立后按反馈选择增强。

## 推荐工期

**4–8 个工程周**

候选能力：

- Cross-cloud Restore；
- Redis / Cache Failure；
- Multi-region Read Replica；
- Advanced Autoscaling；
- Advanced Webhook Platform；
- Richer Frontend；
- More Provider Scenarios；
- Certificate Rotation；
- Secret Expiry；
- Regional Failure；
- Advanced Chaos；
- More Performance Optimization；
- Reliant 与 NodeFoundry Integration。

---

# 16. 文档防劣化体系

## 16.1 权威顺序

1. 运行安全和业务正确性；
2. `docs/vision.md`；
3. `docs/current-state.md`；
4. 已接受 ADR；
5. 当前 Phase Plan；
6. 当前 Phase Packet；
7. SLO Policy；
8. Risk Register；
9. Runbook / Incident / Evidence；
10. 历史报告。

## 16.2 Phase Packet

每个 Phase 文档包含：

1. Objective；
2. User Value；
3. Current Facts；
4. Business Invariants；
5. Scope；
6. Non-goals；
7. Architecture Decisions；
8. Failure Modes；
9. Security Boundaries；
10. Acceptance Criteria；
11. Verification Commands；
12. Cloud Evidence；
13. Cost and Cleanup；
14. Open Risks；
15. Gate Decision。

---

# 17. AI 结对编程治理

## 17.1 Task 输入

```text
Task
Reliability value
Current facts
Business invariants
Allowed scope
Non-goals
Failure modes
Acceptance criteria
Required tests
Required evidence
Cost and cleanup
```

## 17.2 Task 输出

```text
What changed
Why
Files changed
Tests added
Commands executed
Observed results
Failure paths verified
Evidence created
Known limitations
Risks introduced
Documentation updated
```

## 17.3 AI 禁止事项

AI 不得：

- 伪造云和测试结果；
- 把 Mock 当全部集成证据；
- 声称 Exactly-once Delivery；
- 用无限 Retry 掩盖问题；
- 为展示微服务强拆系统；
- 自动批准高风险操作；
- 忽略失败测试；
- 把日志当作数据库；
- 把 LocalStack 结果表述成真实 AWS 结果；
- 绕过 Azure Budget、Expiry 或 Cleanup Gate；
- 自签 Phase ACCEPT；
- 写入敏感数据。

## 17.4 Fresh-context Review

每个 Phase 至少一次独立 Review，只提供：

- Vision；
- Phase Packet；
- Code；
- Tests；
- Evidence；
- Risks。

---

# 18. 自动化防劣化门禁

## 18.1 Repository Gate

每个 PR：

- format；
- build；
- unit；
- architecture；
- integration；
- contract；
- migration；
- Terraform；
- OpenAPI；
- secret；
- dependency；
- container；
- SBOM；
- k6 smoke；
- evidence schema。

## 18.2 Fitness Functions

- Domain 不依赖 Infrastructure；
- Application 不依赖云 SDK；
- Provider SDK 只在 Infrastructure；
- API/Worker 不做 Startup Migration；
- Notification 不直接修改 Transaction 表；
- Tenant Repository 必须有 TenantContext；
- Production Endpoint 有 Permission；
- Outbox 与业务状态同事务；
- Revoked Artifact 不能部署；
- Sample Provider 不进入真实 Release Path；
- 业务代码不散落 `if localstack` 判断；
- LocalStack Profile 不能无提示连接真实 AWS。

## 18.3 Golden Paths

1. Tenant → Campaign → Contribution；
2. Contribution → Outbox → Queue → Worker；
3. Worker → Provider → Reconciliation；
4. Success → Notification/Webhook；
5. Deployment → Failure → Detection → Recovery。

---

# 19. 项目劣化预警

出现以下情况暂停新功能：

- README 超过实际能力；
- 只有 Happy Path；
- 普通 Mock 完全替代 LocalStack 和真实 Azure；
- LocalStack Evidence 被写成真实 AWS Evidence；
- LocalStack Profile 可能误连真实 AWS；
- 消息重复会产生重复结果；
- Retry 没有上限；
- Trace 在 Queue 断开；
- Dashboard 无法指导行动；
- 告警没有 Runbook；
- Rollback 与 Migration 不兼容；
- Backup 未实际恢复；
- Azure/AWS 被伪统一；
- AI 连续大型重构；
- 微服务数量增长但可靠性不增；
- 测试数量增长但核心故障仍无法复现；
- Azure 额度没有预算台账；
- 云资源未清理；
- LocalStack Reset 后仍依赖隐藏状态。

---

# 20. R1 最小范围控制

R1 限制：

- 一个核心业务流程；
- 两个长期运行部署单元：
  - Public API Host；
  - Unified Worker Host；
- Migrator 是 Release Job；
- OpenTelemetry Collector 是基础设施组件；
- Unified Worker 内包含 Processing、Notification、Reconciliation 和 Maintenance Handler；
- 一个主要外部 Provider；
- E1 使用 Testcontainers；
- E2 使用 LocalStack Ultimate 作为 AWS-compatible 主集成环境；
- E3 使用 Azure 100 美元学生额度完成 Production-like 主验证；
- Azure 必须完成 PostgreSQL Backup / Restore；
- AWS Real Cloud E4 Smoke 是可选增强；
- 未完成 E4 时不得声称已在真实 AWS 部署验证；
- 六个 R1 必做 Failure Scenario；
- 不做商业级 UI；
- 不做 Active-Active；
- 不做完整支付；
- 不做通用 SRE 平台；
- 不做第三个云；
- 不做无证据的服务拆分；
- 不做大规模 Chaos 产品；
- 不允许 Azure 计划内消耗超过 70% 额度而不重新评审；
- 不允许 LocalStack 和真实云共享 State。

# 21. 12 周推荐时间表

| 周次 | 目标 |
| --- | --- |
| Week 1 | Phase 0 |
| Week 2 | Phase 1 |
| Week 3 | Phase 2 |
| Week 4 | Phase 3 |
| Week 5 | Phase 4 |
| Week 6 | Phase 5 |
| Week 7 | Phase 6 |
| Week 8 | Phase 7（LocalStack Ultimate；可选 Real AWS Smoke） |
| Week 9–10 | Phase 8 |
| Week 11 | Phase 9 Build/Hardening |
| Week 12 | Release Review and Demo |

额外 2 周缓冲用于：

- Azure 配额和学生订阅 SKU；
- Azure 额度预留；
- LocalStack 服务兼容差异；
- 可选 Real AWS Smoke；
- 数据库和队列差异；
- Trace 断链；
- Migration；
- Restore；
- AWS/Azure 网络；
- Demo；
- Cleanup。

---

# 22. R1 完成定义

Reliant R1 Core 只有在以下条件全部满足时完成：

- 多租户业务链路真实运行；
- 只有 Public API Host 和 Unified Worker Host 两个长期运行部署单元；
- API、DB、Queue、Worker Handler、Provider 和 Notification Trace 完整；
- 重复请求和消息不重复业务结果；
- Unified Worker Crash 后任务恢复；
- Unknown Outcome 可 Reconcile；
- 至少四个 SLI；
- 至少一个正确性 SLI；
- Burn Rate 可关联 Deployment；
- k6 可阻断性能回归；
- Migration 有独立 Gate；
- Queue Backlog 和 Dead-letter 可恢复；
- Azure PostgreSQL Backup / Restore 有 E3 Evidence；
- Azure 完成完整 Production-like 故障、恢复和环境重建；
- 同一 Artifact 可在 Azure 和 LocalStack AWS-compatible Profile 使用；
- LocalStack Ultimate 完成 Terraform、SQS、IAM/STS、Secrets Manager、S3 和至少一个故障恢复验证；
- E2 LocalStack 与 E3 Azure Evidence 明确区分；
- 六个 R1 必做 Failure Scenario 全部可重复；
- 至少三个故障转化为自动 Gate；
- 高风险操作有 RBAC 和 Audit；
- Azure 100 美元额度有预算、预警、Expiry 和 Cleanup Evidence；
- README 和 Runbook 可复验；
- 简历陈述标明正确 Evidence Level；
- 真实 AWS 未验证时明确标记为 E4 Optional 或 Known Limitation。

R1+ AWS Verified 额外要求：

- 真实 AWS 最小环境部署；
- 真实 SQS 与 IAM Role；
- 一个端到端 Smoke；
- 完整 Destroy；
- 才能声称 “validated across Azure and AWS”。

# 23. 与 NodeFoundry 的组合

```text
Reliant
→ SaaS Application Reliability Ownership

NodeFoundry
→ Kubernetes Node and Infrastructure Reliability
```

最终职业定位：

> 能从业务代码、数据库、消息队列、Worker、SLO 和事故恢复，一直深入到 Kubernetes 节点、操作系统、Container Runtime 和云基础设施供应链的 Senior SRE / Platform Engineer。
