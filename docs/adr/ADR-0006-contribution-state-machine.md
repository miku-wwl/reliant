# ADR-0006: Contribution State Machine

## Status

Proposed

## Context

Contribution 是 Reliant 的核心业务实体。outline 第7.4节定义了状态机。ADR-0002 不变量 #3 要求"一个 Contribution 只能进入合法状态"。

需要回答：状态转换的完整规则是什么？谁来触发转换？怎么防止非法转换？

## Decision

### 1. 完整状态机

```
正常路径：
Created -> Accepted -> Processing -> Succeeded -> ReceiptPending -> Completed

失败路径：
Processing -> RetryPending -> Processing（重试）
Processing -> Failed（重试耗尽）

不确定路径：
Processing -> ProviderUnknown -> ReconciliationPending -> Succeeded / Failed
```

### 2. 合法转换表

| 当前状态 | 允许转到 | 触发者 |
| --- | --- | --- |
| Created | Accepted | API（验证通过） |
| Accepted | Processing | Worker（开始处理） |
| Processing | Succeeded | Worker（Provider 成功） |
| Processing | RetryPending | Worker（可重试失败） |
| Processing | ProviderUnknown | Worker（超时/不确定） |
| Processing | Failed | Worker（重试耗尽/永久失败） |
| RetryPending | Processing | Worker（重试触发） |
| ProviderUnknown | ReconciliationPending | Reconciliation Handler |
| ReconciliationPending | Succeeded | Reconciliation（确认成功） |
| ReconciliationPending | Failed | Reconciliation（确认失败） |
| Succeeded | ReceiptPending | Worker（开始发 Receipt） |
| ReceiptPending | Completed | Worker（Receipt 发送成功） |

**所有其他转换都是非法的，必须被拒绝。**

### 3. 实现方式

- Domain 层定义 `ContributionState` 枚举和 `ContributionStateTransition` 方法
- 方法接收目标状态，检查当前状态是否允许转换
- 不允许则抛出 `InvalidStateTransitionException`
- Infrastructure 层在数据库事务中执行状态更新 + 写 StateTransition 记录
- 数据库额外加 CHECK 约束作为最后防线（防止代码 bug 绕过）

### 4. StateTransition 记录

```
StateTransition
├── Id (Guid)
├── ContributionId (Guid)
├── FromState (ContributionState)
├── ToState (ContributionState)
├── Reason (string)
├── ChangedBy (string - 用户 ID 或 Worker 标识)
└── ChangedAt (DateTime)
```

### 5. 并发控制

- Contribution 实体包含 `Version`（int，每次更新 +1）
- EF Core 用乐观并发：`UPDATE ... WHERE Id = @id AND Version = @expectedVersion`
- 两个 Worker 同时转换同一个 Contribution，只有一个成功，另一个收到 `DbUpdateConcurrencyException`
- 失败的 Worker 不重试状态转换，重新查询当前状态决定下一步

## Consequences

- 状态机逻辑在 Domain 层，不依赖 EF Core
- StateTransition 表会增长；由 Phase 3 Experiment 15 定义 Retention /
  Archival Policy，未满足审计要求前不直接删除
- CHECK 约束是最后防线，不是第一道（第一道是 Domain 逻辑）
- 并发冲突时 Worker 需要处理 `DbUpdateConcurrencyException`，不能盲目重试
