# Phase 3 / Experiment 15 — Operational History Retention and Capacity Guardrails

## 一页结论

**PASS（E2：真实 PostgreSQL + WorkerHost + LocalStack）**

这个实验补齐了此前不存在的 operational history 生命周期。`ScheduledMaintenance` 现在可以按
明确 retention policy 执行有上限、可重入的批量清理；多个 Worker 使用 PostgreSQL transaction
advisory lock 选出唯一 Scanner；ProcessingAttempt 和 Reconciliation 在离开 hot table 前进入
带唯一来源约束的 online archive；AuditEvent、StateTransition 和业务数据不参与自动删除。

```text
Initial managed rows = 73
Eligible rows = 40
Protected rows = 33
Oldest eligible age ≈ 120 days
Database bytes ≈ 630,784
Batch size = 2 per parent category
Estimated batches = 3

Interrupted before commit = full rollback / 0 changed
Concurrent scanner B = skipped in ≈15ms
Successful cleanup runs = 3
Scanned = 40
Deleted = 40
Archived = 10

Business rows retained = 7
Audit rows retained = 14
Failures observed = 1
Alerts emitted = 2
Final eligible rows = 0
```

定向测试 1/1 通过，最终全量 162/162 通过，0 failed，0 skipped。

## 实验信息

- 日期：2026-08-11
- 测试入口：
  `tests/Reliant.Tests/Integration/Phase3/Exp15/OperationalHistoryRetentionE2ETests.cs`
- Cleanup 实现：
  `src/Reliant.Worker/Scheduling/OperationalHistoryCleanup.cs`
- 归档表 migration：`AddOperationalHistoryArchive`
- 数据库：PostgreSQL 17 Testcontainer
- Worker：真实 `ScheduledMaintenanceHandlerService`
- 竞争控制：PostgreSQL `pg_try_advisory_xact_lock`
- 实验 Batch Size：每个 parent category 2 条
- 生产默认 Batch Size：500 条
- 定向测试：1/1 passed
- 全量回归：162/162 passed，0 failed，0 skipped

## 最终 Policy Matrix

| 数据类型 | Owner | 默认周期 | 动作 | 必须保护 |
| --- | --- | ---: | --- | --- |
| Outbox | Messaging Platform | 30 天 | Sent/Failed 且无 Active Job 后删除 | Pending、Active Job |
| Inbox | Messaging Platform | 30 天 | Processed/Failed 且无 Active Job 后删除 | Processing、Active Job |
| JobRun / JobAttempt / Lease / Checkpoint | Worker Platform | 30 天 | Job 终态且无 Active Lease；Checkpoint→Attempt→Lease→JobRun | Pending、Running、Active Lease |
| ProcessingAttempt | Provider Integration | 90 天 | Succeeded/Failed 且 Contribution 终态；先归档后删除 | Pending、Unknown、非终态业务 |
| Reconciliation | Provider Reliability / SRE | 90 天 | Resolved 且 Contribution 终态；先归档后删除 | unresolved、WaitNextCycle、ManualRequired |
| AuditEvent / StateTransition | Security / Compliance | 合规决定 | 不直接删除；只允许审批后的外部归档 | 全部 legal hold / 事故证据 |
| ProviderReference / Contribution | Business Owner | 业务策略 | 不属于本 Cleanup | 全部业务事实、幂等映射 |
| DeadLetter / OperatorAlert | SRE / Operations | 单独审批 | Pending 不清理 | 未调查、未处置 |
| OperationalHistoryArchive | Data Governance | 单独审批 | online archive，等待外部归档策略 | 未外部归档、legal hold |

生产配置位于 Worker `appsettings.json`：

```text
Enabled = true
IntervalMinutes = 60
BatchSize = 500
TransportRetentionDays = 30
JobRetentionDays = 30
ProviderHistoryRetentionDays = 90
CapacityWarningRows = 1,000,000
CapacityWarningBytes = 10 GiB
AlertCooldownMinutes = 60
```

## 学生视角：中间过程

### 1. 原来的 Maintenance 不等于 Retention

开始前我先看了 `ScheduledMaintenanceHandlerService`。它已经会释放过期 Lease，也会调度 Retry，
但完全没有历史数据生命周期：终态 Job、Attempt、Inbox、Outbox 和 Reconciliation 会一直增长。

所以这次不能像 Exp14 那样只增加实验。Checklist 要求的 Cleanup、Capacity 和 Alert 都是缺失的
生产能力，必须实现；只写 SQL 脚本或测试内 DELETE 会让报告虚假 PASS。

### 2. 我先定义“可清理”，再写 DELETE

Retention 最危险的错误不是清理太慢，而是把仍可能恢复的证据删掉。我把 eligibility 写成正向白名单：

```text
Outbox    = Sent/Failed + older than cutoff + no Pending/Running Job
Inbox     = Processed/Failed + older than cutoff + no Pending/Running Job
Job group = terminal Job + CompletedAt older than cutoff + no active Lease
Attempt   = Succeeded/Failed + CompletedAt old + terminal Contribution
Recon     = ResolvedAt old + not ManualRequired + terminal Contribution
```

任何没有明确落入白名单的状态都保留。特别是：

- Pending Outbox；
- Processing Inbox；
- Pending/Running Job；
- Active Lease；
- Pending/Unknown ProcessingAttempt；
- unresolved / ManualRequired Reconciliation；
- 非终态 Contribution 对应的所有 Provider 历史。

### 3. 为什么 ProcessingAttempt 和 Reconciliation 要先归档

Outbox、Inbox 和 Job execution history 是可再生或短期运行证据，达到期限后可以按批准策略删除。
Provider Attempt 与 Reconciliation 则直接解释“外部到底发生了什么”，事故调查价值更高。

因此新增 `operational_history_archives`：

```text
SourceType + SourceId = UNIQUE
OrganizationId
SourceOccurredAt
ArchivedAt
Payload = source row JSON
```

Archive INSERT 与 hot-row DELETE 在同一 PostgreSQL transaction 内。崩溃时要么两者都提交，要么
两者都回滚；唯一索引保证同一个来源不会重复归档。

这是一层 online archive，不等同于跨账号、不可变 WORM 存储。外部归档、加密密钥、访问控制和
legal hold 属于生产环境 Data Governance 配置，不能在 E2 本地实验里伪装完成。

### 4. Job 为什么必须按 Child → Parent 删除

JobAttempt 和 Lease 对 JobRun 使用 Restrict FK。直接删除 JobRun 会失败，也会失去执行链上下文。
Cleanup 对每批 terminal Job 执行：

```text
Checkpoint
→ JobAttempt
→ Lease
→ JobRun
```

整个批次仍在一个 transaction 中。Active Lease 会在 candidate 查询阶段排除，因此 Cleanup 不会
和正在工作的 owner 抢 Job。

### 5. 并发 Scanner 使用 try-lock，而不是等待锁

两个 Worker 的 Maintenance 可能同时触发。如果两个 Scanner 都扫描并 DELETE，相同数据会产生
重复工作和锁竞争。本实现先调用：

```text
pg_try_advisory_xact_lock(7341885150001)
```

拿不到锁的 Scanner 不等待，记录 `Skipped=1` 后立即返回。实验把 Scanner A 暂停在拿锁之后，
再启动 Scanner B：

```text
Scanner A = lock owner
Scanner B = skipped
Scanner B return ≈ 15ms
```

这证明并发 Cleanup 不会重复处理，也不会因为阻塞锁形成长事务队列。锁是 PostgreSQL 全局协调，
不局限于单 Worker 进程。

### 6. 中途终止为什么能恢复

实验在 `BeforeCommit` 注入 `OperationCanceledException`。此时 DELETE 和 Archive INSERT 都已在
事务内执行，但尚未 Commit。结果：

```text
RowsChanged = 0
ArchiveRows = 0
所有初始计数完全一致
```

下一次 Scanner 重新看到相同候选并继续。没有独立游标需要修复，进度由数据库当前事实决定，
因此 batch 天然可重入。

### 7. 容量指标口径的 Review 与修正

第一次实现的定向测试虽然通过，但 Review 发现指标口径不准确：

1. `EligibleRows` 只数了 JobRun，没有数随组删除的 JobAttempt、Lease 和 Checkpoint；
2. `EstimatedBatches` 用总 eligible / batch，忽略每个类别每轮各取一个 batch；
3. `Skipped` 重复累加所有 Protected rows，会把容量 gauge 误当成事件 counter。

修正后：

```text
ManagedRows = 73
EligibleRows = 40       # 包括全部 Job child rows
ProtectedRows = 33
EstimatedBatches = 3    # max(category eligible / category batch)
Skipped = 1             # 仅 Scanner lock contention
```

这个过程提醒我：测试绿色不代表观测口径正确。SRE 指标如果定义错了，会让容量预测和告警比没有
指标更危险。

### 8. Retention 查询为什么增加 4 个索引

Cleanup 在小数据上全表扫描也能过，但正式库会持续增长。为实际 eligibility 谓词补了：

```text
Inbox(Status, ProcessedAt)
JobRun(Status, CompletedAt)
ProcessingAttempt(Status, CompletedAt)
Reconciliation(Resolution, ResolvedAt)
```

Outbox 已有 `(Status, OccurredAt)`；Job child 表也已有 JobRunId 索引或唯一索引。这 4 个索引不是
为了单元测试，而是防止 retention 本身在大表上变成数据库事故。

### 9. 指标和告警

`Reliant.OperationalHistory` Meter 提供：

```text
reliant.cleanup.runs
reliant.cleanup.rows.scanned{category}
reliant.cleanup.rows.deleted{category}
reliant.cleanup.rows.archived{category}
reliant.cleanup.rows.skipped
reliant.cleanup.failures
reliant.cleanup.alerts{type}
reliant.cleanup.duration
reliant.operational.rows
reliant.operational.eligible_rows
reliant.operational.oldest_eligible_age
reliant.operational.database_size
reliant.operational.estimated_drain_time
```

容量超过 row/byte threshold 时产生 EventId 15001；Cleanup 非取消失败产生 EventId 15002。告警在
单进程内按类型使用 60 分钟 cooldown，避免 1 分钟扫描产生告警风暴。实验结果：

```text
Runs=6
Scanned=40
Deleted=40
Archived=10
Skipped=1
Failures=1
Alerts=2 (Capacity + CleanupFailure)
```

Meter 已可被后续 OpenTelemetry 接入，但当前仓库尚未完成 Collector、Dashboard 和 Alertmanager
部署，所以 `docs/current-state.md` 中全局 OpenTelemetry / Dashboard 仍保持 Not Started。

### 10. Hosted Maintenance 路径

直接调用 Service 只能证明算法。实验最后重新启动 Worker，设置 Cleanup Enabled，并准备一条
120 天前的 Sent Outbox。真实 `ScheduledMaintenanceHandlerService` 自动删除该记录，日志出现
`Operational cleanup completed`。这证明正式注册、配置和调度路径也已接通。

## 两次全量非功能失败与修复

### 第一次：161/162，dotnet publish 文件锁

Phase2 Exp5 与其他 Docker 实验并行执行 `dotnet publish`，共同写入
`src/Reliant.Domain/obj/Release`，Windows 返回 CS2012 file-in-use。Exp5 单独复跑 3/3 通过。

修复：把 Exp4、Exp5、Exp9、Exp11、Exp12 五个会 publish Worker 的测试放入同一个
`Docker Worker Publish` collection，并禁用该 collection 的并行执行。普通测试仍保持并行。

### 第二次：161/162，Exp12 Lease release 读取竞态

Exp12 原完成条件只等 Job/Inbox Commit，但 Worker 在随后 `finally` 才调用 `Lease.ReleaseAsync`。
测试偶尔在这两个动作之间读取 final snapshot，看到 token=2 Lease 仍 Active。

修复：最终条件额外等待该 Job `Active Lease=0`。没有修改生产顺序。Exp12 定向 2/2 通过，最终
全量 162/162 通过。

## PASS 条件逐项核对

| PASS 条件 | 证据 | 结果 |
| --- | --- | --- |
| 只清理符合策略的终结历史 | 40 eligible 全部清理/归档；33 protected 保留 | PASS |
| Active/Pending/Unknown/ManualRequired 不误删 | 控制组逐项 DB 断言 | PASS |
| Batch 有上限、可重入、可中断恢复 | batch=2；3轮；BeforeCommit 回滚0变更 | PASS |
| 并发无重复或长等待 | advisory try-lock；Scanner B≈15ms跳过 | PASS |
| 容量、进度、失败有指标 | Meter + telemetry snapshot 全部断言 | PASS |
| 容量/失败触发告警 | EventId 15001/15002；Alerts=2 | PASS |
| 业务与审计正确 | Contribution=7、ProviderReference=5、Audit=14 全保留 | PASS |

## 业务代码必要性 Review

### 必须保留

- Archive entity、migration 和唯一索引：保证先归档后删除及重入；
- 4 个 retention query indexes：避免大表扫描；
- Retention options：周期、batch、容量阈值和 cooldown 必须可配置；
- Advisory lock + transaction：并发、Child→Parent 和中断恢复的核心；
- 5 类 eligibility：对应不同数据状态，不能用一个宽泛 DELETE 合并；
- Meter / capacity snapshot / EventId：满足指标和可告警要求；
- Maintenance wiring：让能力在真实 Worker 中运行。

### Review 后已修正或删除

- 修正 EligibleRows 漏算 Job child；
- 修正 EstimatedBatches 公式；
- 修正 Skipped counter 语义；
- 删除测试 helper 中无意义的 `fresh` 参数；
- 丢弃 EF 误生成的“重建所有表” migration，手工收敛为 1 张表 + 4 个索引；
- 没有修改 Provider、Contribution 状态机、Processing Handler 业务分支或 Queue Adapter。

### 为什么生产代码仍然较多

Exp15 与 Exp13/14 不同：它新增的是完整数据生命周期能力，而不是验证已有行为。实现同时包含策略、
归档、并发、事务、容量、指标、告警和调度六个边界。把这些压成一段通用 SQL 会减少行数，却会
丢失状态保护、归档原子性和可观测性。

因此 Review 的目标是删除错误和重复，而不是以行数为目标删除必要保护。当前变更没有测试专用
业务分支；唯一 fault injector 遵循项目已有 crash-injection 模式，默认实现是 Noop。

## 已知生产边界

本实验达到 E2 Local Verified，但以下环境集成仍需在后续生产准备阶段完成：

1. 把 `Reliant.OperationalHistory` Meter 接入 OpenTelemetry Collector、Dashboard 和 Alertmanager；
2. 多 Worker 的告警去重由外部 Alertmanager 完成；当前 cooldown 是进程内保护；
3. online archive 导出到加密、不可变、跨账号存储，并实现 legal hold；
4. 根据真实流量和合规要求审批 30/90 天默认周期与容量阈值；
5. 在 E3 预生产数据量上执行 EXPLAIN、锁等待和批次耗时压测。

这些边界没有被写成已完成，也不影响本实验对软件层 retention safety 的 E2 PASS。

## 最终报告

Experiment 15 建立了从“无限增长”到“有政策、有边界、有证据”的 operational history 生命周期。
终态 transport/job history 可以小批次清理，Provider/reconciliation history 先归档，活跃与未解决
状态默认保护；并发 Scanner 不重复工作，中断不产生半提交，容量和失败都有 Meter 与结构化告警。

最终 162/162 全量通过。至此 Phase 3 的 15 个 Owner Experiments 已全部完成本地 E2 验证。
