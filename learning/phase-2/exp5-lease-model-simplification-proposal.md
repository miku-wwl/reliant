# Lease 模型简化设计草案

> 状态：草案，后续继续讨论和修改。
>
> 目的：记录当前 `JobRun + Lease + JobAttempt` 模型的理解，以及将它们简化为一张 `ProcessingJob` 表的可能方案。

## 1. 当前模型

当前处理链路中，任务执行相关的数据被拆成了三张表：

| 表 | 主要职责 |
|---|---|
| `job_runs` | 记录一个 Job 的整体状态，例如 `Pending`、`Running`、`Succeeded` |
| `leases` | 记录当前哪个 Worker 拥有 Job，以及 `ExpiresAt`、`FencingToken` |
| `job_attempts` | 记录每一次 Worker 执行，例如 Attempt 1、Attempt 2，以及 `Abandoned` / `Succeeded` |

它们分别回答三个问题：

```text
JobRun：这个 Job 整体处于什么状态？
Lease：现在谁拥有这个 Job？
JobAttempt：这个 Job 被执行过几次，每次是谁执行的？
```

当前项目还会额外使用：

| 表 | 作用 |
|---|---|
| `contributions` | 业务数据和业务状态 |
| `outbox_messages` | 可靠发布到 SQS 的事件 |
| `inbox_messages` | 防止逻辑消息重复消费 |
| `processing_attempts` | Provider 调用尝试和幂等键 |
| `provider_references` | 本地业务与外部 Provider Reference 的映射 |

## 2. 为什么当前设计拆成三张表

Exp5 中可以看到三者的不同生命周期：

```text
Worker A：
JobRun = Running
Lease = Active
JobAttempt 1 = Running

Worker A 崩溃后：
JobRun = Pending
Lease = Inactive
JobAttempt 1 = Abandoned

Worker B 接管后：
JobRun = Running
Lease = Active
JobAttempt 2 = Running
```

拆表的好处是可以分别保存：

- 当前 Job 状态；
- 当前 Owner 和 Lease 到期时间；
- 每次执行的历史；
- Worker 崩溃、Lease 过期和接管过程。

问题是：对于当前这个学习项目来说，模型比较重，`JobRun`、当前 Lease 和当前 Attempt 的概念需要同时理解。

## 3. 简化方向：合并成 `ProcessingJob`

可以把：

```text
JobRun
+ 当前 Lease
+ 当前 JobAttempt
```

合并成一张 `processing_jobs` 表。

建议字段示例：

```text
Id                         // 稳定 JobId，通常可以使用 OutboxMessage.Id
MessageId
OrganizationId
JobType
QueueName
Payload 或 PayloadHash
Status                     // Pending / Running / Succeeded / Failed
OwnerId                    // 当前 Worker
LeaseExpiresAt             // Lease 到期时间
LastHeartbeatAt
FencingToken
AttemptCount
CurrentAttemptStartedAt
CompletedAt
LastErrorCategory
LastErrorMessage
CreatedAt
UpdatedAt
```

这张表同时表达：

```text
Job 状态
当前 Owner
Lease 是否过期
当前 FencingToken
总共尝试了几次
最近一次错误
```

## 4. 简化后的表结构

一个更容易学习和维护的版本可以是：

```text
Contribution
OutboxMessage
InboxMessage
ProcessingJob
ProviderReference
```

如果仍然需要完整的执行审计，再额外保留：

```text
ProcessingJobHistory 或 JobAttempt
```

也就是说，`JobAttempt` 可以从“必须存在的当前控制表”变成“可选的历史审计表”。

## 5. 简化后的处理流程

```text
SQS Receive
    ↓
Inbox SELECT
    ↓
⭕ T1：原子 Claim ProcessingJob
    ↓
Contribution → Processing
    ↓
Provider 调用（事务外）
    ↓
⭕ T2：Contribution 最终状态 + Inbox + ProcessingJob 完成状态
    ↓
ACK SQS
```

### T1：原子 Claim

可以使用单行条件更新：

```sql
UPDATE processing_jobs
SET
    "Status" = 'Running',
    "OwnerId" = @ownerId,
    "LeaseExpiresAt" = @expiresAt,
    "FencingToken" = "FencingToken" + 1,
    "AttemptCount" = "AttemptCount" + 1
WHERE "Id" = @jobId
  AND (
      "Status" = 'Pending'
      OR "LeaseExpiresAt" < @now
      OR "OwnerId" IS NULL
  )
RETURNING "FencingToken";
```

如果返回一行，当前 Worker 获得处理权；如果返回 0 行，说明另一个 Worker 仍然拥有有效 Lease。

### Heartbeat

Heartbeat 继续使用条件更新：

```sql
UPDATE processing_jobs
SET
    "LeaseExpiresAt" = @newExpiresAt,
    "LastHeartbeatAt" = @heartbeatAt
WHERE "Id" = @jobId
  AND "OwnerId" = @ownerId
  AND "FencingToken" = @fencingToken
  AND "LeaseExpiresAt" > @now;
```

只有更新 1 行时，才说明当前 Worker 仍然拥有处理权。

### T2：最终提交

Provider 返回后，在同一个数据库事务中保存：

```text
Contribution：Processing → Succeeded / Failed / Unknown
InboxMessage：插入 Processed
ProcessingJob：Running → Succeeded / Failed / Pending
```

然后才 ACK SQS。

## 6. Redis 分布式锁是否可以替代 Lease

Redis 可以实现类似：

```redis
SET lock:job:{jobId} {ownerToken} NX PX 10000
```

这可以替代 Lease 的“临时所有权协调”部分，但它不是分布式事务，也不能自动替代：

- `ProcessingJob` 的持久化状态；
- Worker 尝试历史；
- Fencing Token；
- Provider Unknown 后的恢复；
- PostgreSQL 与 Redis 之间的一致性。

如果 PostgreSQL 仍然保存 Contribution 和 Inbox，就会出现：

```text
Redis 获取锁
→ PostgreSQL 更新失败
```

或：

```text
PostgreSQL 更新成功
→ Redis 状态丢失
```

因此 Redis 只适合在以下条件下考虑：

- 系统已经以 Redis / etcd 作为成熟的分布式协调基础设施；
- 任务吞吐和锁竞争很高；
- 团队愿意处理 TTL、续租、Owner Token、Fencing 和故障恢复；
- 数据库仍然保存最终业务状态。

对于当前项目，PostgreSQL 已经是业务主数据库，把 `ProcessingJob` 和业务状态放在同一个数据库里更容易保证一致性。

## 7. 当前建议

### 学习阶段

暂时保留当前实现：

```text
JobRun + Lease + JobAttempt
```

因为 Exp3、Exp4、Exp5、后续 Fencing 实验都在验证这些概念。

但学习时可以使用简化心智模型：

```text
ProcessingJob = Job 状态 + 当前 Owner + Lease + AttemptCount
```

### 后续重构阶段

可以考虑：

1. 新增 `processing_jobs` 表；
2. 将 `JobRun`、当前 `Lease`、当前 `JobAttempt` 的字段合并进去；
3. 如果需要审计，再保留独立的历史表；
4. 保留 Inbox、Outbox、ProviderReference 的职责边界；
5. 完成迁移后重新运行 Phase 2 全部实验。

## 8. 待后续决定的问题

- 一个 `Contribution` 是否可能同时有多个不同类型的 Job？
- 是否必须保存每一次 Worker Attempt 的完整历史？
- 是否永远只有一个 Provider？
- `ProviderReference` 是否可以直接放进 `Contribution`？
- 系统是否真的需要 Redis / etcd 级别的高吞吐协调？
- `ProcessingJob` 是否需要独立于 `Contribution` 的状态机？

