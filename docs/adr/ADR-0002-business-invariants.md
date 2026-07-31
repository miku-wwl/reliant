# ADR-0002: Business Invariants

## Status

Proposed

## Context

Reliant 的可靠性不仅是 HTTP 200，还包括业务正确性。outline 第3节定义了 12 条业务不变量。本 ADR 说明每条不变量怎么落地保护。

## 12 条不变量及保护方式

### 1. 同一个 Idempotency Key 不产生两个 Contribution

**保护方式**：
- 数据库唯一索引：`(TenantId, IdempotencyKey)`
- API 层在创建 Contribution 时，先查 IdempotencyRecord 表
- 如果已存在，返回之前的结果，不创建新的

### 2. 外部 Provider 超时后不能盲目重复创建业务结果

**保护方式**：
- 每次 Provider 调用前创建 ProcessingAttempt 记录，带 Provider Idempotency Key
- 超时后进入 `ProviderUnknown` 状态，不重试
- 通过 Reconciliation 查询 Provider 最终状态
- 只有确认 Provider 未处理才允许重试

### 3. 一个 Contribution 只能进入合法状态

**保护方式**：
- 状态机定义合法转换路径（见下方）
- 状态转换在数据库事务中执行
- 数据库 CHECK 约束或触发器拒绝非法转换
- 单元测试覆盖所有合法和非法转换

### 4. Receipt 不能在业务处理成功前发送

**保护方式**：
- Outbox 消息在数据库事务中与状态变更原子提交
- Notification Handler 只消费 `Succeeded` 状态产生的消息
- 消息体包含状态，Handler 验证后才执行

### 5. Webhook 重复投递不能造成重复副作用

**保护方式**：
- Webhook Delivery 记录表，基于 `DeliveryId` 去重
- 接收方签名验证 + Timestamp 防重放
- Delivery Attempt 表记录每次尝试结果
- 重试只针对未确认成功的 Delivery

### 6. Tenant A 永远不能读取 Tenant B 的数据

**保护方式**：
- 所有租户表包含 `TenantId` 列
- EF Core Global Query Filter 自动过滤
- Architecture Test 强制所有 Repository 注入 TenantContext
- 集成测试验证跨租户访问 fail closed

### 7. Queue 重复投递不能造成重复处理

**保护方式**：
- 每个 Consumer 维护 Inbox 表
- 消费前检查 `MessageId` 是否已处理
- 已处理则跳过（返回成功），不重复执行业务逻辑
- 这不是 Exactly-once Delivery，而是 At-least-once + Idempotency = Exactly-once Effect

### 8. Worker Crash 不能让任务永久丢失

**保护方式**：
- Lease 机制：Worker 领取任务时设置过期时间
- Heartbeat：Worker 定期续约
- Scheduled Maintenance Handler 检查过期 Lease，重新分配
- Checkpoint：Worker 记录处理进度，恢复后从断点继续
- 消息不确认（不 ACK）直到业务逻辑完成

### 9. 数据库事务提交后，必要异步事件最终能够发布

**保护方式**：
- Outbox 模式：业务状态和 Outbox 消息在同一个数据库事务中提交
- Outbox Publisher 轮询 Outbox 表，发送消息后标记已发送
- Publisher Crash 后重启，重新扫描未发送消息
- 消息已发送但未确认，重新发送时 Consumer 通过 Inbox 去重

### 10. Reconciliation 能发现本地状态与 Provider 状态不一致

**保护方式**：
- Reconciliation Handler 定期查询 Provider 状态
- 比较本地 Contribution State 和 Provider 返回的状态
- 不一致记录到 ReconciliationRecord 表
- 低风险差异自动修复，高风险差异交给 Operator 确认
- 不静默修复

### 11. Rollback 不能破坏已经提交的数据

**保护方式**：
- Deployment Rollback 使用前一个 Artifact 版本
- 数据库 Migration 遵循 Expand/Migrate/Contract：先加新列（兼容旧版本），再迁移数据，最后删旧列
- Rollback 时旧版本代码仍能工作（因为旧列还在）
- 不允许破坏性 Migration 在同一发布窗口内执行

### 12. Migration 不得造成静默数据截断

**保护方式**：
- Migration 独立执行（Migrator Host），不在 API/Worker 启动时跑
- Migration 前自动 Backup
- 列类型变更先加新列，迁移数据，验证行数，再删旧列
- 不允许 `ALTER COLUMN` 直接截断
- Migration 测试验证数据完整性

## Contribution 状态机

```
正常路径：
Created -> Accepted -> Processing -> Succeeded -> ReceiptPending -> Completed

失败路径：
Processing -> RetryPending -> Processing（重试）
Processing -> Failed（重试耗尽）

不确定路径：
Processing -> ProviderUnknown -> ReconciliationPending -> Succeeded / Failed
```

状态转换约束：
- 只能按上述路径转换，禁止跳过（如 Created 直接到 Succeeded）
- 每次转换记录 StateTransition（时间、前状态、后状态、原因）
- 并发控制：使用 Optimistic Concurrency（Row Version），两个 Worker 同时转换同一个 Contribution 时只有一个成功

## Consequences

- 12 条不变量分散在多个 Phase 实现，不是一次全做完
- Phase 1 完成不变量 1/3/6（幂等、状态机、租户隔离）
- Phase 2 完成不变量 7/8/9（Inbox、Worker 恢复、Outbox）
- Phase 3 完成不变量 2/4/5/10（Provider、Receipt、Webhook、Reconciliation）
- 不变量 11/12 在 Phase 5/8 完成（Rollback、Migration）
- 每条不变量必须有对应的自动化测试
