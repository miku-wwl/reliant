# ADR-0012: Worker Execution Model

## Status

Accepted — implemented and verified by Phase 2 Experiment 5

## Context

ADR-0001 定义了 Unified Worker Host，内含 4 个 Handler。outline 第8.1节定义了 Job 模型。需要回答：Worker 怎么领任务、怎么执行、怎么防止多个 Worker 重复处理同一个任务。

## Decision

### 1. Job 模型

**说人话**：每个要处理的任务叫一个 JobRun。Worker 领取任务时"租"它一段时间（Lease），期间定时续约（Heartbeat）。如果 Worker 崩了不续约，Lease 过期后其他 Worker 可以接手。

```
JobDefinition    - 定义：什么任务、什么重试策略、什么超时
  ↓
JobRun           - 一次具体执行：状态、开始时间、结束时间
  ├── JobAttempt - 每次尝试：第几次、结果、错误信息
  ├── Lease      - 租约：谁在处理、什么时候过期
  └── Checkpoint - 断点：处理到哪了，恢复时从这里继续
```

### 2. Lease + Heartbeat 机制

- 处理类 Outbox 与 JobRun 在同一个数据库事务中创建，二者使用同一个 Id
- Worker 领取消息后创建 Lease，有效期 30 秒
- 数据库部分唯一索引保证同一 JobRun 最多一个 active Lease
- Lease、JobRun Running、JobAttempt Running 在同一事务开始
- Worker 每 10 秒续约一次（Heartbeat）
- 如果 Worker 崩了，Lease 30 秒后过期
- Scheduled Maintenance Handler 在同一事务中释放过期 Lease、把旧 JobAttempt
  标成 Abandoned，并将 JobRun 放回 Pending
- SQS Visibility Timeout 设为 35 秒（略大于 Lease），确保 Lease 过期后消息重新可见

### 3. Checkpoint 机制

- 对于长时间处理的任务，Worker 定期保存进度
- 恢复时从最后一个 Checkpoint 继续，不从头开始
- R1 的任务通常很短（秒级），Checkpoint 主要为防止超时

### 4. 4 个 Handler 的职责

| Handler | 消费什么 | 做什么 | 并发数 |
| --- | --- | --- | --- |
| Processing Handler | ContributionCreated 消息 | 将 Contribution 从 Accepted 转到 Processing，调外部 Provider（Phase 3） | 10 |
| Notification Handler | ContributionSucceeded 消息 | 发 Receipt、Email、Webhook | 5 |
| Reconciliation Handler | 定时触发 | 查 Provider 确认状态（Phase 3） | 1 |
| Scheduled Maintenance Handler | 定时触发 | 清理过期 Lease、重试失败任务、清理旧数据 | 1 |

### 5. Handler 隔离

- 每个 Handler 有独立的 SQS 队列
- 一个 Handler 的失败不影响其他 Handler
- 每个 Handler 有独立的并发限制、重试策略、监控指标
- Handler 之间通过消息通信，不直接方法调用

### 6. 失败不传播

- Processing Handler 处理失败，不会阻塞 Notification Handler 的队列
- 每个队列独立，DLQ 独立
- 一个队列积压不影响其他队列

## Consequences

- JobRun/JobAttempt/Lease/Checkpoint 共 4 张新表
- JobAttempt 明确记录 Running、Succeeded、Failed、Abandoned、Deferred
- JobDefinition、JobRun、JobAttempt、Lease 之间由数据库外键和唯一约束保护
- Lease 续约增加数据库写入（每 10 秒一次），但量不大
- SQS Visibility Timeout 必须大于 Lease 有效期
- Worker 重启后不自动恢复之前的 JobRun，靠 Lease 过期触发重新分配
- 长任务还需要同步延长 SQS Visibility Timeout
- 若旧 Worker 只是暂停后恢复，仍需 fencing token 阻止过期 Owner 提交
