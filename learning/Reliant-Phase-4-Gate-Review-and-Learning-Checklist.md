# Reliant Phase 4 Observability Gate Review & Learning Checklist

> 状态：PLANNED
> 进入基线：`079afcb`（Phase 2/3/3.1 Engineering Gate 已通过）
> 信号契约：[`phase-3/observability-contract.md`](phase-3/observability-contract.md)
> 核心目标：让故障可发现、可关联、可告警、可处置、可量化。

Phase 4 不重新设计 Phase 2/3 的可靠性状态机。它把已经验证过的 Outbox、
Queue、Worker、Provider、Reconciliation、Callback、Retry、DLQ、Lease 和
Retention 行为变成可查询的 Metric、Trace、Log、Dashboard、Alert 和 SLO。

---

## 1. 当前目标

完成 Phase 4 后，Owner 应能从观测系统回答：

```text
系统现在是否健康？
哪条链路正在积压或失败？
故障从什么时候开始？
受影响的是 API、Queue、Worker、Provider 还是 Reconciliation？
任务是否仍在收敛？
当前错误是否正在消耗 Error Budget？
应该执行哪份 Runbook？
```

### Definition of Done

- [ ] OpenTelemetry SDK 与 Collector 已接通
- [ ] API → Outbox → SQS → Worker → Provider → Reconciliation → Callback Trace 可关联
- [ ] Phase 3 Observability Contract 中的信号已实现或明确豁免
- [ ] Metric label cardinality 有自动验证
- [ ] Telemetry 不泄露 Secret、签名或完整敏感 Payload
- [ ] Dashboard 能覆盖正常、积压、Provider 故障和恢复
- [ ] 每条 Paging Alert 都关联 Runbook 和故障实验
- [ ] SLI、SLO、Error Budget 和 Burn-rate Alert 可计算
- [ ] k6 稳态、突发、积压和恢复场景通过 Release Gate
- [ ] Collector/Backend 故障不会破坏业务事务、ACK 或 Worker
- [ ] 旧的 163 个测试持续通过
- [ ] 15 个 Phase 4 实验都有可执行验证和一份聚合报告
- [ ] CI 可以阻止零测试、缺报告、Dashboard/Alert 配置错误和 SLO 失败

---

## 2. 范围边界

### Phase 4 范围内

- OpenTelemetry Metrics、Tracing 和结构化日志关联
- OTLP Export 与本地可复现的 Collector/Backend
- Dashboard-as-Code
- Alert Rules 与 Runbook
- SLI、SLO、Error Budget 和 Burn-rate
- k6 性能、积压和恢复 Gate
- Telemetry cardinality、敏感信息与 fail-open 验证
- Phase 4 CI 和 Evidence Pack

### Phase 4 不负责

- Notification Handler 的业务实现
- 修改 Contribution 状态机或 Provider 幂等语义
- 修改 Outbox/Inbox 事务边界
- Real AWS/Azure 部署和云厂商生产认证
- 自动扩缩容；当前只观测容量与饱和点
- Secret Vault/Rotation 平台接入
- 跨 Worker 全局 Retry Budget 的重新设计
- Phase 2/3 Owner 人工签字

### 修改保护线

任何 instrumentation 都必须满足：

```text
Telemetry 成功 ≠ 业务成功前置条件
Telemetry 失败 ≠ 业务事务失败原因
Metric 不参与状态机决策
Trace 不改变 ACK / Retry / Lease / Fencing
Log 不作为唯一可靠性事实源
```

如果单个实验修改现有业务代码超过约 300 行，先暂停并做必要性 Review。

---

## 3. 必须掌握的知识

### 3.1 Metric、Trace、Log 与 Audit 的边界

- Metric 用于聚合、趋势、SLO 和告警；不保存单次业务完整事实。
- Trace 用于还原一次请求或消息的跨进程因果链。
- Log 用于解释离散事件和错误上下文。
- AuditEvent、ProcessingAttempt、ProviderOperation 等数据库记录才是业务审计事实。

必须能解释：为什么 `provider_request_total == 1` 不能单独证明 Provider Effect
最多一次，以及为什么仍需查询 ProviderOperation 和 ProviderReference。

### 3.2 OpenTelemetry 数据路径

```text
Application Instrumentation
→ OpenTelemetry SDK
→ Batch Processor / Exporter
→ OTLP Collector
→ Metric / Trace / Log Backend
→ Dashboard / Alert
```

必须理解 SDK、Provider、Exporter、Collector、Backend 的责任边界，以及为什么
Exporter 和 Collector 故障不能进入业务事务边界。

### 3.3 Counter、Gauge 与 Histogram

- Counter：只递增，例如 retry exhausted 次数。
- Gauge：当前状态，例如 queue depth、pending count。
- Histogram：分布，例如 provider latency、message age。
- Derived Signal：由原始信号计算，例如 unknown rate、queue drain rate。

不能用 Counter 表达当前积压，也不能把单次延迟仅保存为 Gauge。

### 3.4 Cardinality

允许的 Metric label 应是有限集合：

```text
provider, operation, result, error_category, queue,
handler, circuit_state, resolution, message_type
```

默认禁止进入 Metric label：

```text
TenantId, ContributionId, MessageId, JobRunId,
AttemptId, ProviderReference, IdempotencyKey, ErrorMessage
```

这些高基数字段进入 Trace、结构化 Log 或 Audit。

### 3.5 Context Propagation

必须区分：

- CorrelationId：一次业务链路。
- CausationId：当前消息由哪个上游消息产生。
- W3C Trace Context：跨进程 Span 父子关系。
- MessageId：逻辑消息身份，用于幂等。
- SQS Physical MessageId：Broker 投递身份。

Trace Context 可以变化，业务 MessageId 和 Provider Idempotency Key 的语义不能变化。

### 3.6 Async Messaging Trace

Outbox 发布和 Worker 消费不是同步 HTTP 调用。必须理解 Producer Span、Consumer
Span、Span Link，以及重投时为什么不能伪造一个永不结束的单一 Span。

### 3.7 Structured Logging

- 使用稳定 Event Name / Event Id。
- 错误分类使用枚举字段，不从 message 文本反向解析。
- Correlation 字段保持一致。
- 不输出 Secret、HMAC、Authorization、完整 Callback Payload。
- Exception Stack 可进入受控日志，但不能成为 Metric label。

### 3.8 Dashboard

Dashboard 不是图表集合。每个面板必须回答一个操作问题，并说明数据源、查询、
单位、时间窗口和空数据含义。

### 3.9 Alert 与 Runbook

告警必须具备：阈值、持续时间、严重级别、影响、对应实验、Runbook、恢复条件。
不能仅因为某个 Counter 增加一次就触发 Paging。

### 3.10 SLI、SLO 与 Error Budget

- SLI：实际测量方式。
- SLO：目标和时间窗口。
- Error Budget：允许失败量。
- Burn Rate：预算消耗速度。

每个 SLO 必须能由实际查询计算，不能只写一个百分比。

### 3.11 Load、Saturation 与 Recovery

需要同时观察吞吐、延迟、错误、队列年龄、数据库资源、Worker 并发、Provider
错误和恢复时间。高吞吐但 backlog 永不清空不是 PASS。

### 3.12 Telemetry Fail-open

Collector 断开、Backend 限流、Export Queue 满、序列化失败时：

- 业务事务仍可提交；
- Queue ACK 只由业务处理结果决定；
- Worker 不因 Export 卡死；
- Telemetry 丢弃或失败自身可观察；
- 内存和线程不会无限增长。

---

## 4. Signal Implementation Checklist

### Unknown Outcome / Reconciliation

- [ ] `provider_unknown_total`
- [ ] `provider_unknown_rate`
- [ ] `reconciliation_pending_count`
- [ ] `reconciliation_oldest_age`
- [ ] `reconciliation_resolution_total`
- [ ] `reconciliation_manual_required_total`

### Provider

- [ ] `provider_request_total`
- [ ] `provider_request_duration`
- [ ] `provider_error_total`
- [ ] `provider_timeout_total`
- [ ] `provider_idempotency_conflict_total`
- [ ] `provider_duplicate_effect_detected_total`

### Callback

- [ ] `callback_received_total`
- [ ] `callback_invalid_signature_total`
- [ ] `callback_invalid_timestamp_total`
- [ ] `callback_duplicate_total`
- [ ] `callback_orphan_total`
- [ ] `callback_terminal_conflict_total`
- [ ] `callback_processing_duration`

### Retry / Dead-letter

- [ ] `retry_pending_count`
- [ ] `retry_scheduled_total`
- [ ] `retry_exhausted_total`
- [ ] `retry_oldest_age`
- [ ] `deadletter_pending_count`
- [ ] `deadletter_replay_total`

### Circuit / Queue / Worker

- [ ] `circuit_state`
- [ ] `circuit_transition_total`
- [ ] `circuit_half_open_probe_total`
- [ ] `queue_depth`
- [ ] `queue_oldest_message_age`
- [ ] `queue_receive_total`
- [ ] `queue_delete_total`
- [ ] `queue_redelivery_total`
- [ ] `queue_drain_rate`
- [ ] `worker_inflight`
- [ ] `lease_heartbeat_failure_total`
- [ ] `visibility_renewal_failure_total`

### Operational History

- [ ] cleanup scanned / deleted / archived
- [ ] cleanup duration / failure / lock skipped
- [ ] capacity warning
- [ ] oldest eligible record age
- [ ] protected rows 不被误计为 eligible

---

# 5. Phase 4 必做实验

## Experiment 1 — OpenTelemetry Pipeline and Fail-open

### 假设

应用能通过 OTLP 导出 Telemetry；Collector 或 Backend 不可用时，业务事务、消息
处理和 ACK 不受影响，Exporter 资源使用有界。

### 步骤

- [ ] 建立本地 Collector 和 Backend
- [ ] 配置 API、Worker 的 ServiceName、Environment、Version
- [ ] 发送一笔正常业务请求并确认 Metric/Trace 到达
- [ ] 停止 Collector
- [ ] 在 Collector 停止期间继续创建和处理业务
- [ ] 检查业务数据库、Outbox、Queue 和最终状态
- [ ] 检查 Export 失败日志、队列上限和内存变化
- [ ] 恢复 Collector 并确认新 Telemetry 继续导出

### PASS 条件

```text
Telemetry 正常可导出
Collector 故障不破坏业务
ACK 和事务语义不变
Exporter 不无限阻塞或增长
恢复后继续导出
```

### 计划报告

`learning/phase-4/exp1-otel-pipeline-fail-open.md`

---

## Experiment 2 — End-to-End Trace Propagation

### 假设

一次 Contribution 可以跨 API、Outbox、SQS、Worker、Provider、Reconciliation 和
Callback 被关联，同时保留正确的业务 MessageId 与因果字段。

### 步骤

- [ ] 从 HTTP API 创建 Contribution
- [ ] 检查 API Server Span
- [ ] 检查 Outbox Producer Span 和消息 Trace Context
- [ ] 检查 SQS Consumer Span 或 Span Link
- [ ] 检查 Provider Client Span
- [ ] 触发 Reconciliation 和 Callback
- [ ] 按 CorrelationId 查询完整链路
- [ ] 验证重投产生新 Consumer Span 但业务 MessageId 不变

### PASS 条件

```text
完整异步链路可还原
Correlation/Causation 正确
重投 Trace 可解释
Trace 不改变幂等身份
```

### 计划报告

`learning/phase-4/exp2-end-to-end-trace.md`

---

## Experiment 3 — Queue, Outbox and Worker Metrics

### 假设

生产速度超过消费速度时，Queue、Outbox 和 Worker 指标能准确表示 backlog、年龄、
并发和 drain rate，并在恢复后回到正常范围。

### 步骤

- [ ] 限制 Worker 并发
- [ ] 快速创建大量 Outbox 消息
- [ ] 记录 Queue Depth 和 Oldest Message Age
- [ ] 记录 Outbox Pending/Sent/Failed
- [ ] 记录 Worker Inflight 和 Receive/Delete/Redelivery
- [ ] 恢复容量
- [ ] 测量 drain rate 和清空时间
- [ ] 与 SQS/数据库真实查询交叉核对

### PASS 条件

```text
Metric 与真实状态一致
Backlog 增长和恢复都可观察
指标没有高基数标签
最终队列清空
```

### 计划报告

`learning/phase-4/exp3-queue-outbox-worker-metrics.md`

---

## Experiment 4 — Lease, Visibility and Worker Health

### 假设

健康 Heartbeat、Visibility 续约失败、Worker Crash、Lease Takeover 和 Stale Owner
Fencing 都能被区分和关联。

### 步骤

- [ ] 启动超过初始 Visibility Timeout 的长任务
- [ ] 观察 Lease 与 Visibility 续约
- [ ] 注入一次 Visibility 续约失败
- [ ] 强制终止 Worker A
- [ ] 等待 Worker B 接管
- [ ] 触发 Stale Owner 写入
- [ ] 检查 Heartbeat、Renewal、Takeover、Fencing 信号
- [ ] 检查最终业务状态和重复副作用

### PASS 条件

```text
健康与故障信号可区分
Crash 到 Takeover 可关联
Stale Owner 被明确记录
最终结果不重复
```

### 计划报告

`learning/phase-4/exp4-worker-health-metrics.md`

---

## Experiment 5 — Provider Observability

### 假设

Provider Success、Validation、Timeout、Network Failure、429、5xx 和幂等冲突能以
低基数维度被统计，延迟分布与 ProviderOperation 审计一致。

### 步骤

- [ ] 分别触发成功和各类错误
- [ ] 记录 request count、duration、error category 和 timeout
- [ ] 并发提交相同 Contribution
- [ ] 检查 idempotency conflict
- [ ] 检查 duplicate effect 指标保持 0
- [ ] 与 ProcessingAttempt、ProviderOperation、ProviderReference 对账

### PASS 条件

```text
错误分类准确
延迟 Histogram 可查询
Metric 与数据库审计一致
Duplicate Provider Effect 为 0
```

### 计划报告

`learning/phase-4/exp5-provider-observability.md`

---

## Experiment 6 — Unknown Outcome and Reconciliation Observability

### 假设

Unknown Outcome 的产生、积压、年龄、解决方式和 ManualRequired 都可观察，并能
区分“暂未解决”和“已经终态解决”。

### 步骤

- [ ] 触发 Timeout Before Processing
- [ ] 触发 Processed-but-Response-Lost
- [ ] 让记录保持 Pending
- [ ] 触发 NotFound、Succeeded、Failed、Unavailable、ManualRequired
- [ ] 检查 Unknown rate 和 Pending count
- [ ] 检查 Oldest Age 随时间增长并在解决后下降
- [ ] 与 ReconciliationRecord 和 StateTransition 对账

### PASS 条件

```text
Unknown 产生可观察
未解决年龄准确
Resolution 分类准确
ManualRequired 不被算作自动成功
```

### 计划报告

`learning/phase-4/exp6-unknown-reconciliation-observability.md`

---

## Experiment 7 — Callback Security and Ordering Signals

### 假设

合法、非法签名、过期、重复、Orphan、乱序和终态冲突 Callback 都有可解释信号，
且 Telemetry 不泄露安全材料。

### 步骤

- [ ] 投递合法 Callback
- [ ] 投递非法签名和过期 Timestamp
- [ ] 重复投递相同 EventId
- [ ] 投递 Orphan Callback
- [ ] 触发 Callback-before-response
- [ ] 触发 Terminal Conflict
- [ ] 检查计数、延迟、Trace 和日志字段
- [ ] 扫描 Telemetry 中的 Secret、HMAC 和敏感 Payload

### PASS 条件

```text
安全与顺序场景分类准确
重复只产生一次业务结果
冲突可告警
无 Secret 或签名泄漏
```

### 计划报告

`learning/phase-4/exp7-callback-signals.md`

---

## Experiment 8 — Retry, DLQ and Replay Signals

### 假设

Retry Pending、调度、耗尽、Dead-letter 和人工 Replay 可以形成连续操作链，且不会
把重复投递误算成新的业务 Retry。

### 步骤

- [ ] 触发可重试错误
- [ ] 观察 Pending count、scheduled total 和 oldest age
- [ ] 达到 Retry Exhaustion
- [ ] 检查 Dead-letter Pending
- [ ] 使用受控 Replay
- [ ] 检查 replay result、AuditEvent 和新 MessageId
- [ ] 对比重复 Delivery 与业务 Retry 指标

### PASS 条件

```text
Retry 生命周期可观察
耗尽和 DLQ 可告警
Replay 可关联到原消息
重复 Delivery 不污染 Retry 语义
```

### 计划报告

`learning/phase-4/exp8-retry-dlq-replay-signals.md`

---

## Experiment 9 — Circuit Breaker State Visualization

### 假设

Circuit 的 Closed、Open、Half-Open 和 Probe 结果可准确展示，Open 期间不会被误报
为 Provider 调用失败或 Retry Budget 消耗。

### 步骤

- [ ] 连续注入 Provider 故障直到 Open
- [ ] 检查状态和 transition total
- [ ] 在 Open 期间投递消息
- [ ] 确认 Provider request 和 Attempt 不增加
- [ ] 推进时间进入 Half-Open
- [ ] 验证单 Probe 成功与失败路径
- [ ] 检查 Dashboard 时间线

### PASS 条件

```text
状态转换与真实状态一致
Half-Open 只有一个 Probe
Open 不调用 Provider
Open 不消耗业务 Retry Budget
```

### 计划报告

`learning/phase-4/exp9-circuit-breaker-visualization.md`

---

## Experiment 10 — Operational History Metrics

### 假设

Retention Cleanup、归档、锁竞争、失败、容量和保护数据都可观察，且指标不会把
Protected Rows 误算为可清理数据。

### 步骤

- [ ] 准备 Eligible 和 Protected 数据
- [ ] 执行正常批量清理
- [ ] 注入 BeforeCommit 失败
- [ ] 并发启动两个 Scanner
- [ ] 触发 Capacity Warning
- [ ] 检查 scanned/deleted/archived/duration/failure/skip
- [ ] 与数据库最终状态对账

### PASS 条件

```text
清理与容量信号准确
锁跳过和失败可区分
Protected 数据不被删除或误计
告警有冷却和 Runbook
```

### 计划报告

`learning/phase-4/exp10-operational-history-metrics.md`

---

## Experiment 11 — Cardinality and Sensitive Data Guard

### 假设

高租户、高消息量和错误输入不会造成 Metric Time Series 无界增长，也不会把敏感
信息导出到 Metric、Trace 或 Log。

### 步骤

- [ ] 创建大量 Tenant、Contribution、Message 和 Attempt
- [ ] 触发多类错误信息
- [ ] 导出 Metric label 集合和 Time Series 数量
- [ ] 检查禁止字段是否成为 label
- [ ] 扫描 Trace/Log 中的 Secret、Authorization、HMAC 和完整 Payload
- [ ] 验证错误分类使用有限枚举
- [ ] 将 cardinality/security 检查接入测试

### PASS 条件

```text
Time Series 数量有界
高基数字段不进入 Metric label
敏感数据不进入 Telemetry
违规测试会失败
```

### 计划报告

`learning/phase-4/exp11-cardinality-sensitive-data.md`

---

## Experiment 12 — Dashboard Failure Story

### 假设

Dashboard 能从单一入口解释正常、Broker Outage、Provider Outage、Unknown Backlog、
Circuit Open 和 Recovery，而不是只展示互不关联的图表。

### 步骤

- [ ] 建立 System Overview
- [ ] 建立 Queue/Outbox/Worker Dashboard
- [ ] 建立 Provider/Circuit Dashboard
- [ ] 建立 Reconciliation/Callback/Retry Dashboard
- [ ] 建立 Retention/Capacity Dashboard
- [ ] 重放至少四个 Phase 2/3 故障实验
- [ ] 从 Dashboard 定位故障并记录时间线
- [ ] 验证空数据、单位、查询窗口和变量

### PASS 条件

```text
故障位置和影响可在 Dashboard 判断
恢复过程可见
面板查询可版本控制
没有依赖高基数查询
```

### 计划报告

`learning/phase-4/exp12-dashboard-failure-story.md`

---

## Experiment 13 — Alert and Runbook Drill

### 假设

关键故障会在合理时间内触发告警；短暂抖动不会制造告警风暴；Operator 可以按
Runbook 定位、缓解并验证恢复。

### 步骤

- [ ] 为 Queue Age、Unknown Age、DLQ、Circuit、Heartbeat、Cleanup 建立规则
- [ ] 定义 Warning/Paging 严重级别
- [ ] 注入对应故障
- [ ] 记录触发时间和告警内容
- [ ] 按 Runbook 执行诊断和恢复
- [ ] 验证恢复通知
- [ ] 注入短暂抖动检查抑制、持续时间和冷却
- [ ] 验证每条 Paging Alert 都有 Owner 和 Runbook

### PASS 条件

```text
关键故障及时告警
短暂抖动不形成告警风暴
告警包含可操作上下文
Runbook 可以恢复并验证系统
```

### 计划报告

`learning/phase-4/exp13-alert-runbook-drill.md`

---

## Experiment 14 — SLI, SLO and Error Budget

### 假设

Availability、Latency、Queue Freshness、Processing Completion、Unknown Convergence
和 Callback 处理质量可以由真实 Telemetry 计算，并能产生 Fast/Slow Burn 判断。

### 步骤

- [ ] 定义每个 SLI 的 numerator、denominator、窗口和排除项
- [ ] 设置初始 SLO 和 Error Budget
- [ ] 用正常流量建立基线
- [ ] 注入短时高错误率
- [ ] 注入低比例长时间错误
- [ ] 计算 Fast Burn 和 Slow Burn
- [ ] 检查无流量和缺数据语义
- [ ] 形成 Release Decision 规则

### PASS 条件

```text
每个 SLO 可由查询重复计算
Fast/Slow Burn 结果符合预期
无流量不被误判为 100% 成功
Release Gate 使用 Error Budget
```

### 计划报告

`learning/phase-4/exp14-sli-slo-error-budget.md`

---

## Experiment 15 — k6 Load, Backlog and Recovery Gate

### 假设

在稳态、突发和生产速度超过消费速度时，系统保持有界、不丢业务事实、不产生重复
Provider Effect，并在负载下降后于定义时间内恢复。

### 步骤

- [ ] 建立可重复的 k6 数据与场景
- [ ] 测量稳态吞吐和延迟分位数
- [ ] 施加突发负载
- [ ] 限制 Worker 并发制造 Backlog
- [ ] 注入 Provider latency、429、5xx 和 timeout
- [ ] 观察 Queue、Worker、DB、Provider 和 Error Budget
- [ ] 恢复正常容量并测量 drain time
- [ ] 检查业务结果、ProviderOperation 和重复副作用
- [ ] 将阈值接入 CI/Release Gate

### PASS 条件

```text
定义的 SLO 和 k6 Threshold 通过
系统未失控或形成重试风暴
Queue 最终清空
无静默丢失
无重复 Provider Effect
Telemetry 开销在预算内
```

### 计划报告

`learning/phase-4/exp15-k6-load-recovery-gate.md`

---

# 6. 实验进度矩阵

| Exp | 场景 | 实现 | Test | Report | Gate |
| ---: | --- | --- | --- | --- | --- |
| 1 | OTel Pipeline / Fail-open | [ ] | [ ] | [ ] | [ ] |
| 2 | End-to-End Trace | [ ] | [ ] | [ ] | [ ] |
| 3 | Queue / Outbox / Worker Metrics | [ ] | [ ] | [ ] | [ ] |
| 4 | Lease / Visibility / Worker Health | [ ] | [ ] | [ ] | [ ] |
| 5 | Provider Observability | [ ] | [ ] | [ ] | [ ] |
| 6 | Unknown / Reconciliation | [ ] | [ ] | [ ] | [ ] |
| 7 | Callback Signals | [ ] | [ ] | [ ] | [ ] |
| 8 | Retry / DLQ / Replay | [ ] | [ ] | [ ] | [ ] |
| 9 | Circuit Visualization | [ ] | [ ] | [ ] | [ ] |
| 10 | Operational History | [ ] | [ ] | [ ] | [ ] |
| 11 | Cardinality / Sensitive Data | [ ] | [ ] | [ ] | [ ] |
| 12 | Dashboard Failure Story | [ ] | [ ] | [ ] | [ ] |
| 13 | Alert / Runbook Drill | [ ] | [ ] | [ ] | [ ] |
| 14 | SLI / SLO / Error Budget | [ ] | [ ] | [ ] | [ ] |
| 15 | k6 Load / Recovery Gate | [ ] | [ ] | [ ] | [ ] |

---

# 7. Evidence 规则

文件数量控制规则：

1. Checklist 中保存设计，不创建 15 个空报告。
2. 实验真正执行后，才创建 `learning/phase-4/expN-*.md`。
3. 每个实验只保留一份聚合报告，不拆 commands/logs/result/database 文件。
4. 原始 TRX、Metric 导出和截图由 CI Artifact 保存，不长期散落在 Git。
5. Dashboard、Alert、Collector、k6 属于可执行配置，应进入各自代码目录，不复制到报告。

每份实验报告必须包含：

- 学生视角的假设和预期；
- 实际执行命令；
- 关键中间观察；
- Metric/Trace/Log 查询；
- 数据库、Queue、Provider 对账；
- PASS/FAIL；
- 业务代码修改必要性 Review；
- 限制与 E1/E2/E3/E4 级别；
- Commit SHA 和 CI Run。

计划测试目录：

```text
tests/Reliant.Tests/Integration/Phase4/Exp1/
...
tests/Reliant.Tests/Integration/Phase4/Exp15/
```

Phase 4 开始执行后，`scripts/verify-experiments.ps1` 必须扩展为 Exp1–Exp15 的
零测试/缺报告 Gate。

---

# 8. Owner 代码审查清单

## Instrumentation

- [ ] Metric、ActivitySource 和 Tag 命名集中管理
- [ ] Handler 中没有大段重复埋点
- [ ] 优先使用 Middleware、Decorator 或集中 Observer
- [ ] Telemetry 调用不参与业务分支结果
- [ ] Exporter 不进入数据库事务
- [ ] 所有业务核心异常语义保持不变

## Context Propagation

- [ ] API 和消息入口都能创建或恢复 Trace Context
- [ ] Outbox/SQS 只传播允许字段
- [ ] CorrelationId 和 CausationId 不混用
- [ ] 重投不会改变业务 MessageId
- [ ] 无效 Trace Header 安全降级

## Metrics

- [ ] Counter/Gauge/Histogram 类型正确
- [ ] 单位和边界明确
- [ ] Gauge 查询有超时和取消
- [ ] 标签是有限枚举
- [ ] 没有 TenantId/MessageId/ContributionId label
- [ ] Metric 与数据库/SQS 真值做过对账

## Traces and Logs

- [ ] Span status 与业务状态不混淆
- [ ] Exception 记录不泄露敏感数据
- [ ] Async producer/consumer 关系可解释
- [ ] Log Event Name 稳定
- [ ] 不靠日志文本解析生成核心指标

## Dashboard / Alert / SLO

- [ ] 配置可版本控制和自动校验
- [ ] 每个面板有操作问题
- [ ] 每条 Paging Alert 有 Runbook
- [ ] 告警具备持续时间和恢复条件
- [ ] SLI 查询处理无流量和缺数据
- [ ] k6 Threshold 与 SLO 对齐

## Regression

- [ ] 旧 163 tests 全部通过
- [ ] Collector 关闭时业务测试通过
- [ ] ProviderOperationCount 不增加
- [ ] ACK/Retry/Lease/Fencing 行为不变
- [ ] Telemetry 高负载资源使用有界

---

# 9. Owner 口头验收题

1. Metric、Trace、Log、AuditEvent 分别证明什么，不能证明什么？
2. 为什么 ContributionId 不能成为 Prometheus label？
3. Counter、Gauge、Histogram 应如何选择？
4. Outbox 到 SQS 的异步 Trace 应使用父子 Span 还是 Span Link？
5. Message Redelivery 时哪些身份不变，哪些 Span 会变化？
6. Collector 停止时为什么不能回滚业务事务？
7. 如何证明 queue_depth Metric 与真实 SQS 状态一致？
8. Unknown rate 上升时，如何区分 Provider 故障和 Reconciliation Worker 故障？
9. Circuit Open 时哪些指标应该增加，哪些不应该增加？
10. 为什么 Metric 不能替代 ProviderOperation 审计？
11. 什么是 Error Budget Burn Rate？
12. 无请求时 Availability SLI 应该如何处理？
13. 为什么只看平均延迟会掩盖故障？
14. Paging Alert 和 Warning Alert 的区别是什么？
15. Dashboard 显示 Queue 恢复后，如何验证业务结果真的没有重复？

---

# 10. Phase 4 Gate

## Engineering Gate

- [ ] 15/15 Experiments PASS
- [ ] Phase 4 Experiment Discovery Gate PASS
- [ ] 旧测试与新增测试全部通过
- [ ] Build 0 compiler warnings
- [ ] Vulnerable dependency gate PASS
- [ ] Collector/Backend fail-open PASS
- [ ] Cardinality 和 Sensitive Data Gate PASS
- [ ] Dashboard 与 Alert 配置校验 PASS
- [ ] k6 Release Gate PASS
- [ ] CI Artifact 可下载

## Operational Gate

- [ ] Owner 能从 Dashboard 诊断至少四种故障
- [ ] Owner 完成一次 Alert/Runbook Drill
- [ ] Owner 能解释所有 SLI/SLO 查询
- [ ] Owner 能解释 Error Budget Release Decision
- [ ] Runbook 包含恢复后验证
- [ ] 已记录 E1/E2 与真实 E3/E4 的边界

## Decision Template

```markdown
# Phase 4 Gate Decision

## Decision

ACCEPT / VALIDATION / BLOCKED

## Date

YYYY-MM-DD

## Owner

Name

## Implementation Baseline

- Commit SHA:
- CI Run:
- Total Tests:
- Phase 4 Experiments:
- Evidence Pack:

## SLO and Error Budget

- SLI/SLO:
- Measurement Window:
- Burn-rate Alerts:
- Release Decision:

## Known Limitations

- Real AWS/Azure status:
- Capacity environment:
- Telemetry backend limitations:

## Phase 5 Entry Decision

Proceed / Hold

## Owner Notes
```

---

# 11. 推荐推进顺序

```text
Exp1  Telemetry Pipeline / Fail-open
→ Exp2 Trace Propagation
→ Exp3–10 Domain Metrics
→ Exp11 Cardinality / Sensitive Data
→ Exp12 Dashboard
→ Exp13 Alert / Runbook
→ Exp14 SLI / SLO / Error Budget
→ Exp15 k6 / Release Gate
→ Full Regression
→ Evidence Pack
→ Owner Gate
```

每个实验完成后执行：实现 → Targeted Test → 业务修改必要性 Review → 全量 Gate →
聚合报告 → 独立 Commit。是否直接 push `main` 继续由 Owner 决定。
