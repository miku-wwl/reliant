# ADR-0021: Reconciliation Policy

## Status

Accepted

## Context

ADR-0019 定义了 Unknown Outcome 需要 Reconciliation 来收敛状态。outline 第7.6节定义了 Reconciliation 要求。需要回答：多久查一次？查到不一致怎么处理？

## Decision

### 1. 什么是 Reconciliation

**说人话**：Reliant 不确定 Contribution 的最终状态时，主动去问 Provider"这笔到底成功了没有"，直到拿到明确答案。

### 2. 触发条件

| 场景 | 触发方式 |
| --- | --- |
| Unknown Outcome | Contribution 进入 ReconciliationPending，立即触发一次 |
| 定期对账 | 每 15 分钟扫描所有 ReconciliationPending 状态的 Contribution |
| 手动触发 | Operator 通过 CLI `reliantctl reconciliation run` |

### 3. Reconciliation 流程

```
扫描 ReconciliationPending 的 Contribution
  ↓
取最新的 ProcessingAttempt 的 ProviderIdempotencyKey
  ↓
调 Provider QueryStatus(Key)
  ↓
Provider 返回 Succeeded
  -> Contribution 变 Succeeded
  -> 写 ReconciliationRecord（差异=无）
  -> 写 Outbox 消息触发通知
  ↓
Provider 返回 Failed
  -> Contribution 变 Failed
  -> 写 ReconciliationRecord（差异=状态不同）
  -> 高风险，通知 Operator
  ↓
Provider 返回 NotFound
  -> Provider 没处理过
  -> 可以安全重试 Submit
  -> Contribution 回到 Processing
  ↓
Provider 返回 Pending
  -> 还在处理
  -> 等下次 Reconciliation
```

### 4. ReconciliationRecord

```
ReconciliationRecord
├── Id (Guid)
├── ContributionId (Guid)
├── OrganizationId (Guid)
├── LocalState (ContributionState)
├── ProviderState (string)
├── Difference (string) - "None" / "StateMismatch" / "ProviderNotFound"
├── Resolution (string) - "AutoFixed" / "ManualRequired"
├── ResolvedAt (DateTime, nullable)
├── ResolvedBy (string, nullable)
└── CreatedAt (DateTime)
```

### 5. 自动修复 vs 人工介入

| 差异类型 | 处理方式 | 原因 |
| --- | --- | --- |
| Provider 返回 Succeeded，本地是 ReconciliationPending | 自动修复 | Provider 是真值来源 |
| Provider 返回 Failed，本地是 ReconciliationPending | 自动修复 | Provider 是真值来源 |
| Provider 返回 NotFound | 自动重试 Submit | Provider 没处理过，安全重试 |
| Provider 返回 Succeeded，本地已经是 Succeeded | 忽略 | 状态一致 |
| Provider 返回 Failed，本地是 Succeeded | 人工介入 | 严重不一致，不能自动改 |
| Provider 不可用 | 等下次 | 可能是临时故障 |

### 6. Reconciliation 不静默修复高风险差异

- 低风险（状态收敛）：自动修复 + 记录
- 高风险（状态矛盾）：只记录，不修改，通知 Operator
- 所有 Reconciliation 结果都写 ReconciliationRecord

### 7. 最大 Reconciliation 次数

- 同一个 Contribution 最多 Reconciliation 20 次
- 超过 20 次还是 Pending，标记为 Failed，通知 Operator
- 防止永远无法收敛

## Consequences

- Reconciliation Handler 是 ScheduledMaintenance 的一部分
- ReconciliationRecord 表会增长，需要 Retention（Phase 8）
- Provider QueryStatus 接口必须可靠（不能也超时）
- Circuit Breaker 不阻断 Reconciliation（即使 Provider 故障，也要继续查）
