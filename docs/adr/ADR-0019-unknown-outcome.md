# ADR-0019: Unknown Outcome

## Status

Proposed

## Context

outline 第9.3节定义了 Unknown Outcome 场景。这是整个项目最危险的故障：Provider 已经处理了请求，但响应在返回途中丢失，本地只看到 Timeout。如果盲目重试，会导致重复扣款。

## Decision

### 1. 什么是 Unknown Outcome

```
Worker 调 Provider
  -> 请求到达 Provider
  -> Provider 处理成功（扣款了）
  -> Provider 返回响应
  -> 响应在网络中丢失
  -> Worker 只看到 Timeout
  -> Worker 不知道 Provider 到底处理了没有
```

### 2. 处理流程

```
Provider 调用超时
  ↓
不直接重试，也不标记 Failed
  ↓
Contribution 状态进入 ProviderUnknown
  ↓
写 ProcessingAttempt 记录（含 IdempotencyKey、ProviderReference=null、ErrorCategory=Timeout）
  ↓
Contribution 进入 ReconciliationPending
  ↓
Reconciliation Handler 查 Provider 状态
  ↓
查到成功 -> Contribution 变 Succeeded
查到失败 -> Contribution 变 Failed
查不到 -> 继续等，下次再查
  ↓
状态收敛
```

### 3. Provider Idempotency Key

每次调 Provider 时带一个 Idempotency Key：
- Key = `ContributionId + AttemptNumber` 的哈希
- 即使重试，Provider 看到同一个 Key 不会重复处理
- Reconciliation 查询时也用这个 Key

### 4. ProcessingAttempt 记录

```
ProcessingAttempt
├── Id (Guid)
├── ContributionId (Guid)
├── AttemptNumber (int)
├── ProviderIdempotencyKey (string) - 发给 Provider 的 Key
├── ProviderReference (string, nullable) - Provider 返回的参考号
├── Status (AttemptStatus) - Pending / Succeeded / Failed / Unknown
├── ErrorCategory (ErrorCategory, nullable)
├── ErrorMessage (string, nullable)
├── RequestPayload (string) - 发给 Provider 的请求内容
├── ResponsePayload (string, nullable) - Provider 返回的内容
├── StartedAt (DateTime)
└── CompletedAt (DateTime, nullable)
```

### 5. 不允许的行为

- **不允许**：Timeout 后直接重试 Submit（可能重复）
- **不允许**：Timeout 后直接标记 Failed（可能已成功）
- **不允许**：Timeout 后忽略不处理（任务卡住）
- **必须**：进入 Unknown 状态，等 Reconciliation 确认

### 6. 和典型支付超时事故的关系

| 典型事故 | Phase 3 的解法 |
| --- | --- |
| Provider 超时了不知道处理了没有 | 进入 UnknownOutcome 状态 |
| 盲目重试导致重复 | 用 IdempotencyKey，不盲目重试 |
| 没有查 Provider 最终状态 | Reconciliation Handler 定期查询 |

## Consequences

- ContributionState 新增 ProviderUnknown 和 ReconciliationPending 路径（已在状态机中定义）
- ProcessingAttempt 是审计关键表，记录每次尝试的完整信息
- Reconciliation 是唯一能让 Unknown 状态收敛的机制
- Unknown Outcome 不允许自动恢复，必须通过 Reconciliation
