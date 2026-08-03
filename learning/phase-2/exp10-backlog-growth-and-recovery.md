# Phase 2 / Experiment 10 — Backlog Growth and Recovery

## 一页结论

**PASS（E2：PostgreSQL Testcontainer + LocalStack SQS + 两个真实 WorkerHost）**

我用自动化测试并行发布 40 条消息，在没有 Worker 时先形成 Queue Depth=40；
随后只启动一个 `ProcessingConcurrency=1`、Provider 延迟 500ms 的 Worker，
让生产速度明显超过处理速度。

低容量 Worker 完成 4 条时，Queue 中仍有 36 条 backlog：

```text
Running Job 最大值 = 1
Running JobAttempt 最大值 = 1
Active Lease 最大值 = 1
PostgreSQL connection 最大值 = 2
```

之后不停止低容量 Worker，而是增加一个并发为 8 的 Worker。剩余 backlog 在
3686ms 内清空，最终 40 条消息对应 40 份业务结果，没有失败或重复副作用。

## 实验信息

- 日期：2026-08-03
- 测试目录：`tests/Reliant.Tests/Integration/Phase2/Exp10/`
- 测试：
  `ProducerBurst_ShouldGrowObservableBacklog_ThenDrainAfterScaleOut`
- 数据库：PostgreSQL 17 Testcontainer
- Queue：LocalStack 3 SQS
- 消息数：40
- 低容量：Concurrency=1，Provider delay=500ms
- 恢复容量：额外 Concurrency=8，Provider delay=25ms
- Exp10：1/1 passed
- Phase 2 Exp1–Exp10：12/12 passed
- 全量：158/158 passed

## 我的假设

生产者短时间突发并不等于系统失效。可靠系统需要同时满足：

```text
生产速度 > 消费速度
  → Queue Depth 上升
  → Oldest Message Age 上升
  → Worker 并发和数据库负载仍受控
  → 增加容量后 backlog 可清空
  → 每个逻辑消息最终仍只有一份业务结果
```

Queue Depth 只告诉我“积压多少”，Oldest Message Age 才能说明用户等待了多久。
两者必须结合，不能只看 Worker 进程是否存活。

## 实验设计

### 1. 快速生产

测试先在同一个数据库事务中准备 40 份 Contribution、Sent Outbox 和 Pending
JobRun，然后使用真实 `SqsQueueAdapter` 并行发送 40 条 SQS 消息。

实测：

```text
Publish time = 212ms
Publish rate = 188.3 msg/s
低容量理论上限约 = 2 msg/s
Peak Queue Depth = 40
```

这不是正式性能基准，只用于稳定制造“生产快于消费”的实验条件。

### 2. 限制 Worker 并发

第一个 Worker 使用：

```text
Worker:ProcessingConcurrency = 1
Provider:SubmitDelayMs = 500
```

我持续从 PostgreSQL 采样 Succeeded Contribution、Running Job、Running
JobAttempt、Active Lease 和数据库连接数，同时从 SQS 读取 visible +
not-visible message count。

### 3. 恢复容量

保留低容量 Worker，再启动第二个真实 WorkerHost：

```text
新增 ProcessingConcurrency = 8
Provider delay = 25ms
```

恢复计时从第二个 Worker 启动开始，到 Queue Depth=0、40 个 Contribution 与
JobRun 全部 Succeeded、Active Lease=0 为止。

## 学生视角：中间过程

### 第一步：先确认现有能力

我先检查到 Exp9 已经把 Processing Concurrency 做成配置项，Sandbox Provider
也已有测试用延迟配置。因此 Exp10 不需要为了制造 backlog 再修改业务代码。

### 第二步：Oldest Message Age 的 LocalStack 差异

真实 AWS 生产环境通常从 CloudWatch 读取
`ApproximateAgeOfOldestMessage`。本地 LocalStack 实验没有把 CloudWatch
指标链路当成可靠依赖，所以测试使用 SQS 消息的 `SentTimestamp` 做一次采样：

```text
Receive 最多 40 条
读取最早 SentTimestamp
立即把 Visibility 恢复为 0
Worker 尚未启动，因此不会和业务处理竞争
```

采样前后都断言 Queue 中仍有 40 条消息。Queue 的
`MaxReceiveCount` 在本实验设为 100，避免这一次观测性 Receive 误触发 DLQ。

这是一种有侵入性的实验测量方法，不应该复制到生产监控程序中。

### 第三步：第一次编译

测试第一次编译提示缺少 `ISandboxProviderControl` 的 namespace。补充
`Reliant.Infrastructure.Provider` using 后通过。这只是测试代码引用错误，
没有暴露生产业务缺陷。

### 第四步：真实运行

```text
PRODUCER | Messages=40 | PublishMs=212 | Rate=188.3msg/s | LowCapacity≈2.0msg/s

PEAK | Depth=40 | OldestAgeMs=1499

THROTTLED | Concurrency=1 | ProviderDelayMs=500 | Succeeded=4 |
Depth=36 | RunningJobsMax=1 | RunningAttemptsMax=1 |
ActiveLeasesMax=1 | DbConnectionsMax=2

SCALE | AddedConcurrency=8 | ProviderDelayMs=25

RECOVERY | DrainMs=3686 | FinalDepth=0

FINAL | Succeeded=40 | Inbox=40 | JobAttempts=40 |
ProcessingAttempts=40 | References=40 | ProviderEffects=40 |
DeadLetters=0 | DuplicateGroups=0

RESULT | PASS
```

## 最终数据库与 Queue 核对

| 检查项 | 最终值 |
| --- | ---: |
| Contribution | 40 |
| Succeeded Contribution | 40 |
| Sent Outbox | 40 |
| Inbox | 40 |
| Succeeded JobRun | 40 |
| Succeeded JobAttempt | 40 |
| Succeeded ProcessingAttempt | 40 |
| ProviderReference | 40 |
| Sandbox Provider effect | 40 |
| DeadLetterRecord | 0 |
| Active Lease | 0 |
| Queue Depth | 0 |
| 重复业务结果 group | 0 |

数量一一对应也说明没有“为了清空 backlog 而吞掉消息”：

```text
40 Queue messages
→ 40 Inbox records
→ 40 JobAttempts
→ 40 ProcessingAttempts
→ 40 Provider effects
→ 40 Succeeded Contributions
```

## PASS 条件逐项判定

| PASS 条件 | 证据 | 判定 |
| --- | --- | --- |
| Backlog 可观察 | Depth=40/36，OldestAge=1499ms | PASS |
| 系统未失控 | 低容量时 Running/Attempt/Lease 均≤1，DB connections=2 | PASS |
| 恢复后最终清空 | 增加并发后 3686ms，Depth=0 | PASS |
| 无大量失败或重复 | 0 DLQ，40 份一一对应，0 duplicate group | PASS |

## 代码影响

本实验没有修改 `src/` 下的生产代码，也没有修改 Exp1–Exp9 的测试。

新增内容只包括：

```text
tests/Reliant.Tests/Integration/Phase2/Exp10/
  BacklogGrowthAndRecoveryE2ETests.cs

learning/phase-2/
  exp10-backlog-growth-and-recovery.md
```

测试文件较长是因为它把环境、负载生成、SQS 指标采样、两个 WorkerHost、数据库
断言和资源清理全部放在一个实验文件中，避免拆成大量不知道从哪里进入的辅助文件。

## 当前限制与生产准备事项

### 1. 不是容量认证

40 条本地消息只证明恢复机制和不变量，不代表生产 TPS、最大 backlog 或数据库
容量已经认证。正式容量结论需要 k6/生产等价环境、多轮分位数和资源饱和点。

### 2. 生产指标尚未完成

当前 `Metrics / Logs / Dashboard` 仍是 Not Started。本实验读取 SQS 属性和
PostgreSQL 状态形成 E2 证据，但没有实现以下生产能力：

```text
CloudWatch ApproximateNumberOfMessagesVisible
CloudWatch ApproximateAgeOfOldestMessage
Worker throughput / failure / retry metrics
PostgreSQL saturation metrics
backlog 与 oldest-age 告警
容量自动扩缩策略
```

这些仍应在 Phase 3 生产准备实验中完成，不能因 Exp10 PASS 就标记为已实现。

### 3. LocalStack 时间不等于真实 AWS 时间

LocalStack、Docker Desktop 和本机 PostgreSQL 的 3686ms 仅是本次实验记录。
真实 AWS 中还会受到网络延迟、SQS 指标聚合周期、Provider latency、数据库连接池
与 throttling 的影响。

## 验证命令

```powershell
dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release `
  --filter "FullyQualifiedName~Integration.Phase2.Exp10" `
  --logger "console;verbosity=detailed"

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build `
  --filter "FullyQualifiedName~Integration.Phase2.Exp"

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build
```

结果：

```text
Exp10: 1/1 passed
Phase 2 Exp1–Exp10: 12/12 passed
Full suite: 158/158 passed
```

仓库既存的 package vulnerability 和 obsolete API warning 仍然存在，本实验没有
把它们伪装成已处理。

## 我的学习结论

我原来更容易把“Queue 最终变成 0”当成成功。这次实验让我看到，可靠恢复必须同时
检查过程和结果：

```text
过程：Depth、Oldest Age、并发、Lease、数据库连接
结果：Drain time、最终状态、失败数、重复业务副作用
```

Queue 可以暂时积压，但系统必须让积压有数字、有年龄、有恢复时间，并在扩容后仍
保持幂等。Exp10 证明了本地 E2 恢复路径；生产 dashboard、告警和容量门槛仍需要
在后续 SRE 阶段单独完成。
