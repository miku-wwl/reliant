# ADR-0007: Idempotency

## Status

Proposed

## Context

ADR-0002 不变量 #1 要求"同一个 Idempotency Key 不产生两个 Contribution"。outline 第2.2节说"重复提交不会产生重复业务结果"。

需要回答：幂等 Key 怎么生成？怎么存储？重复请求怎么处理？

## Decision

### 1. Idempotency Key 来源

- 客户端在创建 Contribution 时必须提供 `Idempotency-Key` HTTP Header
- 格式：UUID v4
- 没有这个 Header 的创建请求被拒绝（400 Bad Request）

### 2. 存储模型

```
IdempotencyRecord
├── Id (Guid)
├── OrganizationId (Guid) - Tenant Boundary
├── IdempotencyKey (string) - 客户端提供的 UUID
├── ContributionId (Guid, nullable) - 关联的 Contribution
├── RequestHash (string) - 请求体哈希
├── ResponseStatus (int, nullable) - 之前的响应状态码
├── ResponseBody (string, nullable) - 之前的响应体
├── CreatedAt (DateTime)
└── ExpiresAt (DateTime) - 24 小时过期
```

### 3. 处理流程

```
收到创建 Contribution 请求
  ↓
检查 Idempotency-Key Header
  ↓ 不存在
  400 Bad Request
  ↓ 存在
查询 IdempotencyRecord (OrganizationId + IdempotencyKey)
  ↓ 找到
  返回之前的响应（相同状态码和响应体）
  ↓ 未找到
  在同一数据库事务中：
  1. 创建 IdempotencyRecord（状态=Processing）
  2. 创建 Contribution（状态=Created）
  3. 写 Outbox 消息（Phase 2）
  4. 写 AuditEvent
  ↓ 事务提交
  返回 201 Created + Contribution 详情
```

### 4. 请求体哈希

- 如果同一个 Idempotency Key 但请求体不同，返回 409 Conflict
- 防止客户端用同一个 Key 但改了参数

### 5. 数据库约束

- 唯一索引：`(OrganizationId, IdempotencyKey)` - 核心防线
- 如果两个请求同时到达（并发），数据库唯一索引拒绝第二个，返回 409

### 6. 过期清理

- IdempotencyRecord 24 小时后过期
- Scheduled Maintenance Handler 定期清理过期记录（Phase 2）
- 过期后相同 Key 可以创建新的 Contribution（这是预期行为）

## Consequences

- 创建 Contribution 需要 2 次数据库写（IdempotencyRecord + Contribution），在同一事务中
- 重复请求不触发业务逻辑，直接返回缓存响应
- 请求体哈希确保客户端不能用同一个 Key 改参数
- 唯一索引是最后防线，代码逻辑是第一道
- Phase 2 的 Outbox 消息也在同一事务中，确保 DB 提交后消息最终发出
