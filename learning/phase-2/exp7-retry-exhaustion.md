# Phase 2 / Experiment 7 — Retry Exhaustion

## 一页结论

**PASS（E2：PostgreSQL Testcontainer + LocalStack SQS + 真实 WorkerHost）**

我让 Sandbox Provider 持续返回 `429 RateLimited`。这属于 transient failure，
但在整个实验中永远不恢复。

最终系统只执行 5 次 Provider Attempt，前 4 次分别使用指数 Backoff 加 0–1 秒
Jitter，第 5 次失败后不再创建新的 Retry Outbox。Contribution 进入 Failed，
最后一个 JobRun 进入 DeadLettered，数据库创建一条 `DeadLetterRecord` 和一条
`OperatorAlert` Outbox。

额外等待 3 秒后，Attempt 数和 Retry Outbox 数都没有变化：

```text
Retry 有上限：是
Attempt 可审计：是
最终状态明确：是
```

## 实验信息

- 日期：2026-08-03
- 测试目录：`tests/Reliant.Tests/Integration/Phase2/Exp7/`
- 测试：
  `PersistentTransientFailure_ShouldExhaustRetryBudget_AndStop`
- 环境：.NET 10、PostgreSQL 17 Testcontainer、LocalStack 3、真实 WorkerHost
- Provider 模式：`RateLimited`
- ErrorCategory：`RateLimited`
- 最大 Provider Attempt：5
- Backoff：1s、2s、4s、8s，单次增加 0–1s Jitter
- 专项测试：1/1 通过
- Retry / Circuit / Exp6 / Final E2E 相关回归：47/47 通过
- 最终默认并行全量：155/155 通过

## 我的假设

Transient failure 可以重试，但“可以重试”不等于“可以无限重试”：

```text
暂时失败
→ 有限 Attempt
→ 每次之间 Backoff + Jitter
→ 达到预算后停止
→ 进入明确终态
→ 保留人工处理所需审计
```

本实验故意使用 `RateLimited`，因为它是 Retryable Error，但不会触发当前只统计
ServerError / Timeout / NetworkFailure 的 Circuit Breaker。这样可以单独验证
Retry Exhaustion，不会把 Circuit Open 的 defer 混入 Attempt 数。

## 实验前的代码 Review

### 1. 第 5 次 Attempt 没有计入 RetryCount

原 Handler 使用：

```text
ShouldRetry(contribution.RetryCount + 1)
```

当第 5 次 Provider Attempt 失败时，`ShouldRetry(5)` 返回 false，于是代码直接走
“Permanent failure”分支。但 `ProcessingAttempt #5` 已经真实发生，Contribution
的 RetryCount 却仍是 4。

### 2. Transient exhaustion 被错误写成 Permanent failure

第 5 次错误仍是 `RateLimited`，不是永久业务拒绝。原代码却使用：

```text
Contribution → Failed("Permanent failure")
```

最后错误分类和错误信息也没有更新为第 5 次结果。

### 3. Scheduler 的 Exhaustion 分支在正常链路不可达

`RetrySchedulerService` 已经存在：

```text
RetryCount >= 5
→ Contribution Failed
→ DeadLetterRecord
→ OperatorAlert Outbox
```

但是 Handler 在第 5 次失败时不把 RetryCount 写成 5，也不再设置 NextRetryAt。
因此正常运行产生的数据永远不会被 Scheduler 的这个分支扫描到。原有单元/集成
测试只是直接 Seed `RetryCount=5`，没有证明真实 Worker 能走到这里。

### 4. Backoff / Jitter 没有可读运行记录

`RetryPolicy.GetDelay` 已经计算指数延迟和随机抖动，但 Worker 没有记录：

```text
当前 Attempt
最大 Attempt
实际 Delay
NextRetryAt
```

数据库最终只保留最后一个 `NextRetryAt`，调度后它又会被清空，无法直接复原每次
实际延迟。

## 第一次真实运行：FAIL

先对未修复系统运行 Exp7：

```text
Expected RetryCount: 5
Actual RetryCount:   4

0 passed, 1 failed
```

失败发生时数据库已经存在 5 条 `ProcessingAttempt`，说明 Provider 确实被调用
了 5 次；错误在于业务 Retry 计数和 exhaustion 终态没有闭环，而不是测试等待
时间不足。

## 修复设计

### 1. Error 分类和 Retry 预算分开判断

现在先判断错误是不是 Retryable：

```text
ErrorClassifier.IsRetryable(error)
```

只要真实发生了一次 Retryable Provider failure，就立即：

```text
RetryCount = 当前 Attempt
LastErrorCategory = 当前错误分类
LastErrorMessage = 当前错误
```

然后再判断预算是否还有剩余。

### 2. Attempt 1–4 调度下一次重试

前 4 次执行：

```text
delay = min(base × 2^(attempt-1), cap) + jitter
NextRetryAt = now + delay
Contribution → RetryPending
```

并记录结构化日志：

```text
ContributionId
Attempt / MaxAttempts
DelayMs
NextRetryAt
```

### 3. Attempt 5 不创建第 6 次 Provider 调用

第 5 次仍然先准确持久化：

```text
RetryCount = 5
LastErrorCategory = RateLimited
LastErrorMessage = Simulated 429
```

然后把 `NextRetryAt` 设为当前时间，表示“exhaustion finalization 已到期”，交给
现有 Scheduler 立即处理。它不会创建新的 Retry Outbox，而是在一个事务中执行：

```text
RetryPending → Failed
NextRetryAt → null
写 StateTransition
写 DeadLetterRecord
写 OperatorAlert Outbox
```

这种做法复用了已经存在的原子终态逻辑，没有在 Handler 中复制一套 DeadLetter
代码。

### 4. 当前 Job 也反映失败终态

前 4 条处理消息本身成功完成了“记录失败并调度下一次重试”，所以对应 JobRun /
JobAttempt 是 Succeeded。

第 5 条已经确认预算耗尽：

```text
JobAttempt 5 = Failed
JobRun 5 = DeadLettered
```

因此 Job 层不会把最终失败误报成 Succeeded。

## 实际运行结果

原始输出：

```text
CONFIG | ProviderMode=RateLimited | MaxAttempts=5 | BackoffBaseMs=1000 | BackoffCapMs=30000 | JitterMs=0-1000

ATTEMPTS | Count=5 | Numbers=1,2,3,4,5 | Statuses=Failed | ErrorCategory=RateLimited | ProviderEffects=0

BACKOFF | Attempt1=1699ms | Attempt2=2334ms | Attempt3=4145ms | Attempt4=8325ms

FINAL | Contribution=Failed | RetryCount=5 | NextRetryAt=null | DeadLetters=1 | JobRuns=5 | DeadLetteredJobs=1

STABILITY | WaitMs=3000 | AttemptsBefore=5 | AttemptsAfter=5 | RetryOutboxesBefore=4 | RetryOutboxesAfter=4 | ContinuedRetry=false

RESULT | PASS | StartedAt=2026-08-03T10:16:21.1028632Z | TerminalAt=2026-08-03T10:16:57.9404290Z | CompletedAt=2026-08-03T10:17:01.2992296Z | DurationMs=40196
```

## Backoff / Jitter 核对

| Attempt 后 | 指数 Base | 允许 Jitter | 实际 Delay |
| ---: | ---: | ---: | ---: |
| 1 | 1000ms | 0–1000ms | 1699ms |
| 2 | 2000ms | 0–1000ms | 2334ms |
| 3 | 4000ms | 0–1000ms | 4145ms |
| 4 | 8000ms | 0–1000ms | 8325ms |
| 5 | 不再重试 | 不适用 | 立即进入终态处理 |

每个实际 Delay 都落在对应的指数 Base 和 `Base + 1000ms` 之间。

## 数据库最终状态

| 检查项 | 实际值 |
| --- | ---: |
| Contribution | Failed |
| RetryCount | 5 |
| LastErrorCategory | RateLimited |
| LastErrorMessage | Simulated 429 |
| NextRetryAt | null |
| ProcessingAttempt | 5 |
| AttemptNumber | 1、2、3、4、5 |
| AttemptStatus | 全部 Failed |
| Provider Idempotency Key | 1 个稳定 Key |
| Provider 业务副作用 | 0 |
| Retry Outbox | 4 |
| Inbox | 5 |
| JobRun | 5 |
| Succeeded JobRun | 4 |
| DeadLettered JobRun | 1 |
| JobAttempt | 5 |
| Succeeded JobAttempt | 4 |
| Failed JobAttempt | 1 |
| DeadLetterRecord | 1 |
| OperatorAlert Outbox | 1 |

## 为什么 Retry Outbox 是 4，不是 5

`MaxAttempts=5` 表示 Provider 总共最多执行 5 次：

```text
Attempt 1（初始）
→ Retry Outbox 1
Attempt 2
→ Retry Outbox 2
Attempt 3
→ Retry Outbox 3
Attempt 4
→ Retry Outbox 4
Attempt 5
→ Exhausted，不再创建 Retry Outbox
```

因此这是“初始 Attempt + 最多 4 次重试”，不是“初始 Attempt + 5 次重试”。

## PASS 条件核对

- [x] Handler 持续得到可重试 `RateLimited`
- [x] Provider Attempt 恰好 5 次
- [x] AttemptNumber 连续为 1–5
- [x] 每次 Attempt 的状态、错误和时间可查询
- [x] 5 次使用同一个 Provider Idempotency Key
- [x] 前 4 次记录 Backoff 和 Jitter
- [x] 实际 Delay 落在预期范围
- [x] 第 5 次后没有第 6 个 Retry Outbox
- [x] Contribution 最终 Failed
- [x] RetryCount 最终为 5
- [x] NextRetryAt 最终为 null
- [x] DeadLetterRecord 恰好 1 条
- [x] 最后 JobRun 为 DeadLettered
- [x] 最后 JobAttempt 为 Failed
- [x] 生成 OperatorAlert Outbox
- [x] 额外等待 3 秒后 Attempt 数没有增加
- [x] 额外等待 3 秒后 Retry Outbox 数没有增加
- [x] 专项测试 1/1 通过
- [x] 相关回归 47/47 通过
- [x] 全量测试 155/155 通过

## 我的最终理解

三个计数不能混在一起：

```text
ProcessingAttempt.AttemptNumber：
真实 Provider 调用次数。

Contribution.RetryCount：
已发生的 Retryable Provider failure 次数。

SQS ApproximateReceiveCount：
同一条物理 Queue Message 被 Broker 投递的次数。
```

Exp7 验证的是前两个。每次业务 Retry 都通过新的 Outbox Message 调度，因此不能
用 SQS ReceiveCount 判断业务 Retry 是否耗尽。

完整闭环是：

```text
Provider transient failure
→ ProcessingAttempt Failed
→ RetryCount + 1
→ Backoff + Jitter
→ RetryPending / NextRetryAt
→ Scheduler 原子写 Retry Outbox + JobRun
→ Worker 下一次 Attempt
→ Attempt 5 failure
→ Scheduler 原子 Failed + DeadLetter + Alert
→ 不再 Retry
```

## 第三方复验

专项实验：

```powershell
dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release `
  --filter "FullyQualifiedName~Phase2.Exp7" `
  --logger "console;verbosity=detailed"
```

预期：`1 passed, 0 failed, 0 skipped`。

全量回归：

```powershell
dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release `
  --logger "console;verbosity=minimal"
```

预期：`155 passed, 0 failed, 0 skipped`。

## 已知限制和正式化事项

1. 本实验是 LocalStack E2，不是 AWS E4。
2. `MaxAttempts`、Base Delay、Cap 和 Jitter 当前由代码中的 `RetryPolicy` 默认值
   提供，还没有按 JobDefinition / 租户 / Provider 动态配置。
3. 每次实际 Delay 已写结构化日志，但没有独立持久化的 RetryScheduleHistory；
   ProcessingAttempt 和 StateTransition 是持久化审计，精确 Jitter 目前依赖日志。
4. ADR 中的全局 Retry Budget（例如一分钟同类错误超过 100）尚未实现；Exp7
   只证明单个 Contribution 的 Attempt 上限。
5. `OperatorAlert` Outbox 已生成，但当前统一 Outbox 路由和 Notification Handler
   仍是 Skeleton，Operator 告警的正式投递、升级和确认属于后续可观测性工作。
6. `reliantctl deadletter list/replay` 仍未完成，本实验没有证明受控 Replay。
7. DeadLetterRecord 的并发唯一约束和 DLQ audit compensation 仍是 Exp6 已记录的
   正式化事项。
8. 构建仍报告既有 NuGet 高危依赖警告和测试辅助 SQS API 过时警告，本实验没有
   将它们标记为已修复。
