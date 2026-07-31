# ADR-0014: Graceful Shutdown

## Status

Proposed

## Context

outline 第8.4节定义了 Graceful Shutdown 要求。部署时 Worker 收到停止信号，不能直接杀进程，否则正在处理的消息会丢失或重复。

## Decision

### 1. 为什么要 Graceful Shutdown

**说人话**：Worker 正在处理一条消息，处理到一半（比如已经调了 Provider 但还没更新数据库）。这时候部署杀进程，消息没确认（没删除），SQS 会重新投递。但 Provider 已经被调过了，重新处理可能导致重复操作。

Graceful Shutdown 就是：收到停止信号后，停止领新任务，等当前任务处理完再退出。

### 2. Shutdown 流程

```
Worker 收到 SIGTERM（容器停止信号）
  ↓
1. 停止从 SQS 领取新消息
  ↓
2. 当前正在处理的消息：
   - 如果快完成了 -> 等它完成，确认删除，退出
   - 如果还需要很长时间 -> 保存 Checkpoint，释放 Lease，不确认消息，退出
  ↓
3. 释放所有 Lease
  ↓
4. 记录 Shutdown 日志（Shutdown Evidence）
  ↓
5. 退出进程
```

### 3. 超时保护

- 等待当前任务完成的最大时间：30 秒
- 超过 30 秒强制退出（保存 Checkpoint + 释放 Lease）
- 容器编排（Container Apps / ECS）的 termination grace period 设为 35 秒

### 4. 非优雅 Crash 的区别

| 场景 | Graceful Shutdown | 非 Graceful Crash（kill -9 / OOM） |
| --- | --- | --- |
| 当前消息 | 完成或保存 Checkpoint | 丢失进度，消息重新投递 |
| Lease | 正常释放 | 等 30 秒过期 |
| 新消息 | 不再领取 | 不适用（进程已死） |
| 日志 | 有 Shutdown Evidence | 没有 |

- 非 Graceful Crash 靠 Lease 过期 + SQS Visibility Timeout 恢复
- Inbox 去重保证即使消息重新投递也不会重复执行

### 5. .NET 实现

- 使用 `IHostApplicationLifetime.ApplicationStopping` 令牌
- Worker 监听这个令牌，收到后停止领取新任务
- `BackgroundService.ExecuteAsync` 接收 `CancellationToken`，自动传播停止信号

## Consequences

- 部署时需要给 Worker 35 秒的优雅退出时间
- 如果 Worker 处理时间 > 30 秒，会被强制中断（靠 Checkpoint 恢复）
- Graceful Shutdown 的 Evidence 是 Phase 2 Gate 条件之一
