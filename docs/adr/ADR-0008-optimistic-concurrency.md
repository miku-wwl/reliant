# ADR-0008: Optimistic Concurrency

## Status

Proposed

## Context

ADR-0002 不变量 #8 要求 Worker Crash 后任务可恢复。outline 第7.5节定义了乐观并发要求。多个 Worker 可能同时处理同一个 Contribution，需要防止数据冲突。

## Decision

### 1. 乐观并发方案

不使用悲观锁（SELECT FOR UPDATE），使用乐观并发：

- 每个关键实体包含 `Version` 列（int，初始值 0）
- 每次 UPDATE 自动 +1
- EF Core 配置 `RowVersion` 或手动 `WHERE Version = @expected`
- 如果行已被其他人修改，`UPDATE` 影响 0 行，EF Core 抛出 `DbUpdateConcurrencyException`

### 2. 适用实体

| 实体 | 是否需要 Version | 原因 |
| --- | --- | --- |
| Contribution | 是 | Worker 和 Operator 可能同时修改状态 |
| Campaign | 是 | 多个管理员可能同时编辑 |
| Membership | 否 | 修改频率低，不需要 |
| IdempotencyRecord | 否 | 创建后不修改 |
| OutboxMessage | 是 | Publisher 可能并发发送 |

### 3. API 层 ETag

- GET 响应包含 `ETag` Header，值为 Contribution 的 `Version`
- PUT/PATCH 请求需要带 `If-Match` Header
- 如果 ETag 不匹配，返回 412 Precondition Failed
- 这让 API 消费者也能做乐观并发控制

### 4. 并发冲突处理

当 `DbUpdateConcurrencyException` 发生时：

| 场景 | 处理方式 |
| --- | --- |
| 两个 Worker 同时转状态 | 失败的 Worker 重新查当前状态，判断是否需要做其他操作 |
| Operator 和 Worker 同时改 | Operator 的请求失败，返回 409 Conflict |
| 重复 Callback | 第二个 Callback 检查当前状态，如果已是目标状态则忽略 |

### 5. 不使用悲观锁的原因

- 悲观锁会阻塞其他请求，降低吞吐
- 悲观锁在 Worker Crash 时不会自动释放，需要超时清理
- 乐观并发更适合 SaaS 场景（冲突概率低）

## Consequences

- 所有关键实体需要 `Version` 列
- API 消费者需要处理 412/409 错误
- Worker 需要处理 `DbUpdateConcurrencyException`，不能盲目重试
- ETag 是 API 契约的一部分，后续不能去掉
