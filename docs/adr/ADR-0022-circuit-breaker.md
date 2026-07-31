# ADR-0022: Circuit Breaker

## Status

Proposed

## Context

ADR-0016 Backpressure 提到 Provider Rate Limit 时需要 Circuit Breaker。outline 第8.2节要求 Circuit Breaker。需要回答：什么时候熔断、熔断多久、怎么恢复。

## Decision

### 1. 什么是 Circuit Breaker

**说人话**：Provider 连续返回 5xx 错误时，暂停调用 Provider 一段时间。就像家里的电路保险丝，电流过大时自动断电保护电器。

不熔断的后果：
```
Provider 故障
  -> Worker 调 Provider 失败
  -> 重试
  -> 又失败
  -> 再重试
  -> 100 个 Worker 同时重试
  -> Provider 被打得更崩
  -> 雪崩
```

### 2. 三种状态

```
Closed（正常）
  -> 请求正常通过
  -> 记录失败次数
  -> 连续失败 >= 5 次 -> 打开熔断
  
Open（熔断中）
  -> 所有请求直接失败（不调 Provider）
  -> 等待 30 秒
  -> 30 秒后 -> 进入 Half-Open
  
Half-Open（试探）
  -> 放一个请求通过
  -> 成功 -> 关闭熔断（恢复正常）
  -> 失败 -> 重新打开熔断（再等 30 秒）
```

### 3. 熔断条件

| 错误类型 | 触发熔断吗 | 原因 |
| --- | --- | --- |
| 5xx 连续 5 次 | 是 | Provider 可能在故障 |
| 429 连续 5 次 | 否 | 只是限流，不是故障，降低频率即可 |
| Timeout 连续 3 次 | 是 | Provider 可能卡死了 |
| NetworkFailure 连续 3 次 | 是 | 网络可能断了 |
| ValidationFailure | 否 | 是请求问题，不是 Provider 问题 |

### 4. 熔断时 Worker 怎么做

```
Circuit Breaker Open
  ↓
Worker 收到消息
  -> 不调 Provider
  -> 消息不 ACK（不删除）
  -> 等 Visibility Timeout 后消息重新可见
  -> 等熔断恢复后重试
```

### 5. 熔断可观测

- Circuit Breaker 状态变化写日志
- 状态暴露为 Metrics（circuit_breaker_state: 0=closed, 1=open, 2=half-open）
- Phase 4 在 Dashboard 上展示

### 6. 不熔断 Reconciliation

- Reconciliation 查询不受 Circuit Breaker 限制
- 即使 Provider 故障，Reconciliation 仍然尝试查询
- 因为 Reconciliation 是解决 Unknown Outcome 的唯一手段

### 7. Retry Budget 和 Circuit Breaker 的关系

| 机制 | 保护什么 | 层级 |
| --- | --- | --- |
| Retry Budget | 防止单个消息无限重试 | 消息级别 |
| Circuit Breaker | 防止大量请求涌入故障 Provider | 系统级别 |

两者配合：Circuit Breaker Open 时，Retry Budget 不消耗（因为根本没调 Provider）。

## Consequences

- Circuit Breaker 是有状态的，需要存储当前状态
- R1 用内存存储 Circuit Breaker 状态（单 Worker 进程）
- 如果将来拆成多 Worker 进程，需要共享状态（Redis 或数据库）
- 熔断期间消息积压在队列，但不丢不重复
