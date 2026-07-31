# ADR-0016: Backpressure

## Status

Proposed

## Context

outline 第8.3节定义了 Backpressure 要求。当系统负载超过处理能力时，不能崩掉，需要有机制保护自己。

## Decision

### 1. 什么场景需要 Backpressure

**说人话**：用户提交速度超过 Worker 处理速度，消息在队列里越积越多。如果不处理，最终数据库连接耗尽、Worker 内存爆掉、Provider 被打限流。

| 场景 | 表现 | 后果 |
| --- | --- | --- |
| Queue Backlog | 队列消息数持续增长 | 处理延迟增大，Freshness SLO 违反 |
| Worker Saturation | 所有 Worker 都在忙 | 新消息没人处理 |
| Database Saturation | 连接池耗尽 | API 也受影响 |
| Provider Rate Limit | Provider 返回 429 | 所有请求被拒 |
| Notification Storm | 大量通知同时发送 | Webhook 接收方被打爆 |

### 2. R1 Backpressure 策略

| 场景 | 策略 | 实现方式 |
| --- | --- | --- |
| Queue Backlog | 限制 Worker 并发，不无限拉取 | 每个 Handler 有并发上限（Processing=10, Notification=5） |
| Worker Saturation | 有界并发，不超限 | SemaphoreSlim 限制并发 |
| Database Saturation | 连接池上限 | EF Core 默认连接池上限 100 |
| Provider Rate Limit | Circuit Breaker | Phase 3 实现，超限后暂停发送 |
| Notification Storm | 批量 + 限速 | Notification Handler 限制 5 并发，每秒最多 10 条 |

### 3. 不做什么

R1 不做：
- 自动扩容（Phase 6 部署到云后才涉及）
- 优先级队列（R1 所有消息同等优先级）
- Admission Control（R1 不在 API 层拒绝请求，靠限流保护）

### 4. 监控指标

Backpressure 需要可观测才能行动：

| 指标 | 阈值 | 动作 |
| --- | --- | --- |
| Queue Backlog > 1000 | 告警 | 人工检查 Worker 是否正常 |
| Queue Oldest Message Age > 60s | 告警 | Freshness SLO 可能违反 |
| Worker Active Concurrency = Max | 信息 | Worker 满载，正常 |
| Database Connection Pool Usage > 80% | 告警 | 可能需要调大连接池或减少并发 |

这些指标在 Phase 4（Observability）实现，Phase 2 只实现机制（有界并发），不实现监控。

## Consequences

- R1 的 Backpressure 是被动的（有界并发），不是主动的（自动扩缩）
- 监控留到 Phase 4，Phase 2 只保证机制不崩
- 如果负载持续超过处理能力，消息会积压，但系统不会崩
- Provider Rate Limit 的 Circuit Breaker 在 Phase 3 实现
