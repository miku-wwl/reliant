# Phase 3 / Experiment 14 — Provider Backlog and Recovery

## 一页结论

**PASS（E2：真实 WorkerHost + LocalStack SQS + PostgreSQL）**

我一次准备了 50 个 Contribution 和 50 条 SQS 消息，把 Sandbox Provider 设置为
`Error5xxBeforeProcessing`，同时保留 100ms 调用延迟。单并发 Worker 发出第 5 次请求并收到
5xx 后，Circuit 从 Closed 进入 Open。此时只发生了 5 次受控的 Provider 调用尝试，没有产生
Provider 业务效果；Queue 仍有 45 条积压，最老消息年龄约 4.1 秒，5 个 Contribution 处于
RetryPending。

Provider 切回 Success 后，Circuit 到期进入 Half-Open，只放行一个 Probe。Probe 成功后
Circuit Close，Worker 继续按并发 1 排空消息。恢复期间实测最大增长为 1 个 Provider Effect
/ 100ms，没有瞬时洪峰；约 13.0 秒后队列归零。

```text
Outage calls before Open = 5
Outage provider effects = 0
Queue depth at Open = 45
Oldest message age at Open ≈ 4.1s
RetryPending at Open = 5
Half-Open probe = observed
Recovery concurrency = 1
Max recovery growth = 1 effect / 100ms
Drain time ≈ 13.0s
Final succeeded = 50
Final provider effects = 50
Duplicate provider effects = 0
Dead letters = 0
```

## 实验信息

- 日期：2026-08-11
- 测试入口：
  `tests/Reliant.Tests/Integration/Phase3/Exp14/ProviderBacklogAndRecoveryE2ETests.cs`
- 数据库：PostgreSQL 17 Testcontainer
- 队列：LocalStack SQS
- Worker：真实 Processing Handler、Outbox Publisher、Maintenance Scheduler
- Provider 故障模式：`Error5xxBeforeProcessing`
- Provider 延迟：100ms
- Circuit Failure Threshold：5
- 实验 Circuit Open Duration：3 秒
- Worker Processing Concurrency：1
- 消息数：50
- 定向测试：1/1 passed
- 全量回归：161/161 passed，0 failed，0 skipped

## 假设

```text
Provider 故障时，系统必须先限制调用压力，再保留尚未完成的工作。
恢复必须经过 Half-Open 探针，不能把整个 backlog 同时打回 Provider。
积压可以增长，但必须可观察、可恢复，并且最终业务效果仍然幂等。
```

## 学生视角：中间过程

### 1. 我先区分了“积压”与“丢失”

一开始我容易把 Queue Depth 上升理解成系统失败。这个实验让我看到，在 Provider 已经故障时，
适度积压其实是保护机制工作的结果：Worker 不能继续无上限调用 Provider，也不能把未处理的消息
ACK 掉。真正危险的是 Queue Depth 看不到、消息被静默删除，或者恢复后瞬间洪峰。

所以这次没有只检查最终 50 条都成功，而是同时记录：

- Circuit 状态；
- 真实业务 ProcessingAttempt 数量；
- Queue visible + in-flight depth；
- 最老消息年龄；
- RetryPending 数量；
- Provider side effect 数量；
- 恢复阶段每 100ms 的 effect 增量。

### 2. 为什么故障模式用 Error5xxBeforeProcessing

`ServerError` 属于 Circuit 需要统计的错误类别，而 `Error5xxBeforeProcessing` 明确表示 Provider
没有创建外部操作。因此前 5 次失败可以安全进入 RetryPending，同时 Provider Effect 必须保持 0。

这与 `ProcessedButResponseLost` 不同。后者已经可能产生外部效果，正确路径应该是 Unknown Outcome
和 Reconciliation，不适合用来证明故障期间的调用限流。

### 3. 为什么使用 50 条消息和单并发

Checklist 要求 50–100 个 Contribution，我选择下限 50，已经足以形成明显 backlog，同时不会让
本地和 CI 的容器测试无意义地延长。Worker 并发设为 1，使以下断言可解释且稳定：

```text
第 1–5 次调用：Provider 返回 5xx
第 5 次失败：Circuit Open
剩余消息：进入 Worker 后被 Deferred，不再调用 Provider，也不 ACK
```

如果一开始使用高并发，多个已经越过 Circuit 检查的 in-flight 请求可能同时到达 Provider，
失败调用数可以合理地略高于 threshold。这是并发 Circuit 的正常边界，但不利于本实验精确证明
“threshold=5 时调用受控”，所以这里把并发限制固定为 1。

### 4. 我先让 50 条消息全部可见，再启动 Worker

测试先创建 Contribution、OutboxMessage 和 JobRun，再通过真实 `SqsQueueAdapter` 发布 50 条消息。
Worker 启动前断言 Queue Depth=50，并读取每条消息的 SQS `SentTimestamp`；采样期间临时设置的
Visibility 最后全部恢复为 0。

这样 Oldest Message Age 来自真实 SQS 属性，而不是用测试开始时间猜测。Worker 启动后，最早的
5 条失败消息被正常 ACK 并等待 Retry Scheduler，其余消息形成可见或 in-flight backlog。

### 5. Circuit Open 时观察到什么

实测结果：

```text
Threshold=5
Provider5xxCalls=5
ProviderEffects=0
RetryPending=5
QueueDepth=45
OldestAgeMs=4141
```

这组数据说明调用压力已经停在 5 次，而工作没有消失。前 5 条明确失败并进入 RetryPending；其余
45 条仍留在 SQS。Circuit Open 后的物理 Redelivery 不创建新的 ProcessingAttempt，也不消耗
Contribution RetryCount，这部分语义已经由 Phase 3 Exp11 单独证明。

### 6. Provider 恢复与 Half-Open Probe

测试先把 Provider 模式切回 Success，但不会人工调用 `RecordSuccess()` 关闭 Circuit。Circuit 必须
等待 Open Duration 到期并进入 Half-Open，再由真实 Worker 请求拿到唯一 Probe。

生产默认 Open Duration 是 30 秒。为了避免每次全量回归都空等 30 秒，实验通过测试夹具注入一个
相同 threshold、但 Open Duration=3 秒的真实 CircuitBreaker。状态机和 Worker 路径不变，只有
测试时间被压缩。

Probe 成功后 Circuit 自动 Close。恢复过程每 100ms 采样一次 Provider Effect：

```text
HalfOpenProbeObserved = true
ProcessingConcurrency = 1
MaxProviderEffectsPer100Ms = 1
```

因此 backlog 恢复不是瞬时把 45 条请求一起冲向 Provider，而是受 Worker concurrency 和 Provider
latency 共同约束。

### 7. 最终排空与幂等结果

最终数据库和队列快照：

```text
Contribution Succeeded = 50
ProcessingAttempt = 55
  Failed = 5
  Succeeded = 50
ProviderReference = 50
Sandbox Provider Effect = 50
Duplicate ProviderReference groups = 0
DeadLetter = 0
Active Lease = 0
Queue Depth = 0
```

55 个 ProcessingAttempt 不是重复业务效果。最早 5 个 Contribution 各有一次 5xx 失败 Attempt 和
一次恢复成功 Attempt；其余 45 个各有一次成功 Attempt。外部 Provider 使用稳定 Idempotency Key，
最终每个 Contribution 恰好一个 Provider operation 和一个 ProviderReference。

## PASS 条件逐项核对

| PASS 条件 | 证据 | 结果 |
| --- | --- | --- |
| Provider 故障时调用量受控 | 第 5 次 5xx 后 Open，Attempt 停在 5 | PASS |
| Queue 积压可观察 | Depth=45，OldestAge≈4.1s | PASS |
| 恢复时无瞬时洪峰 | Half-Open 单 Probe；1 effect/100ms；并发 1 | PASS |
| 队列最终清空 | 约 13.0s 后 Depth=0 | PASS |
| 无重复业务效果 | 50 Contribution 对应 50 Provider Effect，重复组 0 | PASS |

## 业务代码必要性 Review

### 生产代码

```text
src/ 修改文件：0
数据库 migration：0
生产配置默认值修改：0
```

已有生产实现已经提供本实验需要的能力：

- `CircuitBreaker` 对 ServerError 计数并执行 Open / Half-Open / Close；
- Open 时 `SubmitToProviderHandler` 返回 Deferred，不创建业务 Attempt；
- `ProcessingHandlerService` 对 Deferred 消息 no-ACK、no-retry-budget；
- Worker concurrency 提供恢复 backpressure；
- Sandbox Provider 用稳定 Idempotency Key 防止重复 effect。

因此没有理由为了实验再增加业务分支或专用配置。

### 测试基础设施

`WorkerHostFixture.StartWorkersAsync` 增加了一个可选 `CircuitBreaker?` 参数。只有 Exp14 传入
3 秒实例；其他测试传 null 时仍由原来的生产 DI 注册创建默认 5/30 秒 Circuit。该修改只减少实验
等待时间，不改变默认语义。

### 删除审查

我检查了新增测试的每一类代码：批量造数、SQS 时间戳采样、Queue Depth、数据库快照、Half-Open
采样和最终断言都直接对应 Checklist。没有发现可删除的生产修改，也没有保留调试分支、临时开关
或模拟 PASS 的断言。

## 最终报告

Experiment 14 证明了 Provider 故障下的完整压力控制链：Circuit 在有限失败后阻止后续调用，
未完成消息留在 SQS，Queue Depth、Oldest Age 和 RetryPending 可作为积压信号；Provider 恢复后，
Half-Open 单 Probe 先验证健康，再按 Worker 并发逐步排空。最终 50 个 Contribution 全部成功，
Provider Effect 恰好 50 个，没有重复、DeadLetter 或静默丢失。

实验结论：**PASS，可以进入 Experiment 15。**
