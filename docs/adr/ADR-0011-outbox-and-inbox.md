# ADR-0011: Outbox and Inbox

## Status

Proposed

## Context

ADR-0002 不变量 #9 要求"数据库事务提交后，必要异步事件最终能够发布"。outline 第7.2-7.3节定义了 Outbox/Inbox 要求。

核心问题：如果先写数据库再发消息，两步之间可能崩（数据库提交了但消息没发出去）。如果先发消息再写数据库，消息可能比数据库先被消费（Consumer 查不到数据）。

## Decision

### 1. Outbox 模式

**说人话**：把"要发的消息"和"业务数据"写在同一个数据库事务里。这样要么都成功要么都失败。然后一个后台进程轮询这个消息表，把消息发到队列。

```
用户提交 Contribution
  ↓
同一个数据库事务：
  1. 写 Contribution（状态=Created）
  2. 写 OutboxMessage（待发送）
  3. 写 AuditEvent
  ↓ 事务提交（要么全成功，要么全回滚）
Outbox Publisher 轮询 OutboxMessage 表
  ↓ 发现未发送的消息
发到 SQS 队列
  ↓ 发送成功
标记 OutboxMessage 为已发送
```

### 2. OutboxMessage 数据模型

```
OutboxMessage
├── Id (Guid) - 消息 ID，全局唯一
├── OrganizationId (Guid) - 租户
├── MessageType (string) - 消息类型（如 "ContributionCreated"）
├── Payload (JSON) - 消息内容
├── CorrelationId (string) - 关联请求
├── CausationId (string, nullable) - 因果关系（谁触发了这条消息）
├── OccurredAt (DateTime) - 事件发生时间
├── SentAt (DateTime, nullable) - 发送时间
├── SendCount (int) - 发送尝试次数
├── Status (OutboxStatus) - Pending / Sent / Failed
└── Version (int) - 乐观并发
```

### 3. Outbox Publisher 工作方式

- 后台定时轮询（每 2 秒）查询 `Status = Pending` 的消息
- 每次最多取 50 条
- 逐条发送到 SQS，发送成功后标记 `Status = Sent`
- 发送失败不标记，下次轮询重试
- `SendCount` 超过 10 次标记 `Status = Failed`，告警

### 4. Publisher Crash 恢复

- Publisher 发送消息到 SQS 成功，但还没来得及标记 Sent 就崩了
- 下次轮询时这条消息还是 Pending，会被重新发送
- SQS 端会出现重复消息，由 Consumer 的 Inbox 去重

### 5. Inbox 模式

**说人话**：Consumer 收到消息后先检查"这个消息我处理过吗"，处理过就跳过。这样即使消息重复投递，也不会重复执行业务逻辑。

```
Worker 收到 SQS 消息
  ↓
查询 InboxMessage 表：这个 MessageId 处理过吗？
  ↓ 处理过
  跳过，确认消息（删除）
  ↓ 没处理过
  在同一个数据库事务中：
  1. 执行业务逻辑
  2. 写 InboxMessage（标记已处理）
  ↓ 事务提交
确认 SQS 消息（删除）
```

### 6. InboxMessage 数据模型

```
InboxMessage
├── Id (Guid)
├── MessageId (string) - 来自 OutboxMessage.Id，全局唯一
├── OrganizationId (Guid)
├── MessageType (string)
├── HandlerName (string) - 哪个 Handler 处理的
├── HandlerVersion (string) - Handler 版本
├── ProcessedAt (DateTime)
├── AttemptCount (int)
└── Status (InboxStatus) - Processing / Processed / Failed
```

### 7. 不宣称 Exactly-once Delivery

- SQS 是 At-least-once Delivery：同一个消息可能投递多次
- Outbox Publisher Crash 后重发也会产生重复
- 通过 Inbox 去重实现 **Exactly-once Effect**：业务上只执行一次
- 不对外宣称"消息只投递一次"，只宣称"业务只执行一次"

### 8. 消息顺序

- 不保证全局顺序
- 同一个 Contribution 的消息通过 CausationId 关联
- Consumer 不依赖消息顺序处理，每个消息自包含足够信息

## Consequences

- OutboxMessage 表需要定期清理已发送的旧记录（Scheduled Maintenance Handler 负责）
- InboxMessage 表会增长，需要 Retention 策略
- Publisher 轮询增加数据库负载，但 2 秒间隔 + 50 条批量是可接受的
- 重复消息由 Inbox 去重，不会产生重复业务结果
