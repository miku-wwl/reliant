# ADR-0009: Audit Model

## Status

Proposed

## Context

outline 第6.3节定义了高风险操作需要审计。ADR-0002 多条不变量要求"有审计"。事故调查需要知道"谁在什么时候做了什么"。

## Decision

### 1. AuditEvent 模型

```
AuditEvent
├── Id (Guid)
├── OrganizationId (Guid) - Tenant Boundary
├── EntityType (string) - "Contribution" / "Campaign" / "Membership"
├── EntityId (Guid)
├── Action (string) - "Create" / "Update" / "Delete" / "StateTransition" / "Replay" / "Retry"
├── ChangedBy (string) - 用户 ID 或 Worker 标识
├── ChangedAt (DateTime)
├── OldValues (JSON, nullable)
├── NewValues (JSON, nullable)
├── CorrelationId (string) - 关联请求/操作的 Trace ID
└── Metadata (JSON, nullable)
```

### 2. 审计范围

| 操作 | 是否审计 | 原因 |
| --- | --- | --- |
| 创建 Contribution | 是 | 业务核心 |
| Contribution 状态转换 | 是 | 事故调查关键 |
| 创建/修改 Campaign | 是 | 配置变更 |
| Membership 变更 | 是 | 安全敏感 |
| Replay Dead-letter | 是 | 高风险操作 |
| Retry Provider Processing | 是 | 高风险操作 |
| Rollback | 是 | 高风险操作 |
| 查询（GET） | 否 | 只读，不影响数据 |

### 3. 审计写入方式

- AuditEvent 和业务操作在**同一个数据库事务**中写入
- 不使用异步审计（先提交业务再写审计会丢失记录）
- 不使用日志文件作为审计（日志可能丢失，且不可查询）

### 4. 审计查询

- 审计按 OrganizationId 隔离（租户不能看别人的审计）
- Auditor 角色只能查审计，不能修改业务数据
- 审计记录不可修改不可删除（R1 通过应用层保证，Phase 9 加数据库级保护）

### 5. 授权审计 vs 业务审计

- 授权审计：谁批准了什么操作（"Operator A approved replay of dead-letter X"）
- 业务审计：操作执行结果（"Contribution X state changed from Processing to Succeeded"）
- 两者分开存储，不混在一条记录里

## Consequences

- AuditEvent 表会增长，但 R1 不需要清理（Phase 8 加 Retention）
- 审计写入增加事务负担，但这是必要的代价
- 审计记录不可修改是安全要求，R1 通过应用层保证
- 审计和业务操作同事务，如果事务回滚审计也回滚（这是正确的：没发生的操作不需要审计）
