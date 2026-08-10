# Phase 3 / Experiment 13 — Retry Exhaustion

## 一页结论

**PASS（E2：真实 WorkerHost + LocalStack SQS + PostgreSQL）**

Sandbox Provider 持续返回明确可重试的 `429 RateLimited`。系统只执行 5 次业务
ProcessingAttempt，前 4 次按 1/2/4/8 秒指数 Backoff 加 0–1 秒 Jitter 调度；第 5 次失败后
停止自动执行，Contribution 进入 Failed，`NextRetryAt=null`，并持久化一条 DeadLetterRecord
和一条 OperatorAlert。

```text
Attempts = 5 (1..5)
RetryCount = 5
Retry Outbox = 4
Contribution = Failed
DeadLetter = 1
OperatorAlert = 1
```

终态后额外等待 3 秒，Attempt 保持 5、Retry Outbox 保持 4，证明没有隐藏的无限重试。

## 实验信息

- 日期：2026-08-11
- Phase3 测试入口：
  `tests/Reliant.Tests/Integration/Phase3/Exp13/RetryExhaustionEvidenceE2ETests.cs`
- 共享 scenario：
  `tests/Reliant.Tests/Integration/Phase2/Exp7/RetryExhaustionE2ETests.cs`
- 数据库：PostgreSQL 17 Testcontainer
- 队列：LocalStack SQS
- Worker：真实 Outbox Publisher + Processing Handler + Maintenance Scheduler
- Provider mode：RateLimited
- ErrorCategory：RateLimited
- 最大业务 Attempt：5
- Exp13：1/1 passed

## 与 Phase 2 Experiment 7 的重叠 Review

Phase 3 Exp13 的假设、步骤和 PASS 条件与已经完成的 Phase 2 Exp7 完全相同：

| 项目 | Phase 2 Exp7 | Phase 3 Exp13 |
| --- | --- | --- |
| 持续 transient failure | RateLimited | RateLimited |
| Attempt 上限 | 5 | 5 |
| Backoff / Jitter | 1/2/4/8s + jitter | 相同 |
| 最终状态 | Failed | Failed |
| DeadLetter / Alert | 必须 | 必须 |
| 停止稳定性 | 必须 | 必须 |

复制 400 多行 E2E 会让两个实验以后发生漂移。因此本次把 Phase2 测试主体提取为同文件的
`RunScenarioAsync`，两个 Phase 的专属 `[Fact]` 入口调用同一个 scenario：

```text
Phase2 Exp7 Fact  --\
                    -> one shared executable scenario
Phase3 Exp13 Fact --/
```

这样 Phase2 历史入口继续存在，Phase3 也能独立过滤和复现，同时没有复制测试实现。

## 假设

```text
retryable != retry forever
每次 Provider 调用必须有 Attempt 证据
达到预算后必须进入明确终态
终态必须稳定，并保留 Operator 可处理证据
```

这里的“Safe Retry”表示系统已把错误分类为可重试，并按受控预算调度；它不是 Exp2 中
“Provider NotFound 后证明无外部 effect”的狭义 SafeRetry reconciliation resolution。

## 学生视角：中间过程

### 为什么使用 RateLimited

`RateLimited` 是明确 retryable 错误，但不会使当前 Circuit Breaker Open。Circuit 只统计
ServerError、Timeout 和 NetworkFailure。

这让实验只验证 Retry Budget：

```text
Provider request -> 429
record failed ProcessingAttempt
RetryPending + NextRetryAt
Maintenance creates retry Outbox
next Worker attempt
```

如果使用 5xx，Circuit 可能在中途 Open，把后续工作变成 Deferred；那会混合 Exp11 的
No-ACK 语义，无法准确证明 5 次业务 Retry Exhaustion。

### Attempt 和 RetryCount

最终数据库：

```text
ProcessingAttempt #1 Failed / RateLimited
ProcessingAttempt #2 Failed / RateLimited
ProcessingAttempt #3 Failed / RateLimited
ProcessingAttempt #4 Failed / RateLimited
ProcessingAttempt #5 Failed / RateLimited

Contribution.RetryCount = 5
LastErrorCategory = RateLimited
LastErrorMessage contains 429
```

五条 Attempt 使用同一个 ProviderIdempotencyKey。即使 Provider 对失败响应的实际处理存在
不确定性，稳定 key 也不会把业务重试变成五个不同 logical operation。

### Backoff 和 Jitter

本次实测日志：

| Attempt 后 | 基础 Backoff | 实测 Delay |
| --- | ---: | ---: |
| 1 | 1000 ms | 1847 ms |
| 2 | 2000 ms | 2165 ms |
| 3 | 4000 ms | 4971 ms |
| 4 | 8000 ms | 8649 ms |

每个值都落在 `base <= delay <= base + 1000ms`，证明指数增长和 Jitter 同时生效。第 5 次不再
计算 NextRetryAt，因为 Retry Budget 已耗尽。

### 最后一次失败的原子终结

第 5 次失败后，系统最终留下：

```text
Contribution = Failed
RetryCount = 5
NextRetryAt = null
Retry Outbox = 4（只对应前四次）
DeadLetterRecord = 1
  MessageType = ContributionRetryExhausted
  AttemptCount = 5
  ErrorCategory = RateLimited
OperatorAlert Outbox = 1
final JobRun = DeadLettered
final JobAttempt = Failed
```

前四个 JobRun/JobAttempt 成功完成“把业务推进到 RetryPending 并 ACK 当前消息”的职责；最后
一个 transport job 被标为 DeadLettered/Failed，明确表示自动恢复链结束。

### 无无限 Retry 的稳定性检查

达到 Failed 后记录基线，再等待 3 秒：

```text
ProcessingAttempt: 5 -> 5
ContributionRetryRequested Outbox: 4 -> 4
Contribution: Failed -> Failed
NextRetryAt: null -> null
DeadLetter: 1 -> 1
```

所以 PASS 不只是“某个瞬间看到 Failed”，而是证明后台 Scheduler 没有继续产生工作。

## PASS 条件逐项判定

| PASS 条件 | 实际证据 | 判定 |
| --- | --- | --- |
| Retry 有明确上限 | Attempt=5，RetryCount=5 | PASS |
| 无无限 Retry | 稳定等待后 5→5、4→4 | PASS |
| 最终状态明确 | Failed / NextRetryAt=null | PASS |
| DeadLetter 可审计 | 1 条，包含 attempt/error/time | PASS |

## 最终数据

| 数据 | 最终值 |
| --- | --- |
| Contribution | Failed |
| RetryCount | 5 |
| NextRetryAt | null |
| ProcessingAttempt | 5 / all Failed / RateLimited |
| Retry Outbox | 4 |
| Inbox | 5 |
| DeadLetterRecord | 1 / ContributionRetryExhausted |
| OperatorAlert | 1 |
| JobRun | 5；4 Succeeded + 1 DeadLettered |
| JobAttempt | 5；4 Succeeded + 1 Failed |
| Provider-side successful effects | 0（429 均未处理） |

## 业务代码与测试聚合 Review

```text
生产代码修改：0
数据库 Migration：0
Phase2 测试修改：5行，将主体暴露为 internal shared scenario
Phase3 新测试入口：15行，调用 shared scenario
大段测试复制：0
新增测试：1
```

Phase2 Exp7 已经完成过必要的 RetryCount、终结事务、DeadLetter 与 OperatorAlert 业务修复；本次
复现没有发现新业务缺口，因此不应再次修改 Worker、RetryPolicy 或 Scheduler。

## 当前限制

1. 全量测试会分别执行 Phase2 和 Phase3 两个 owner 入口，因此同一 scenario 会跑两次；这是
   为两个 Gate 保留独立可过滤证据的成本，但实现只有一份。
2. 当前 Backoff/Jitter 从日志验证；正式 SRE 观测应提供结构化 retry delay histogram/counter，
   不依赖文本解析。
3. RateLimited 不触发 Circuit，符合本实验隔离目标；5xx outage 下 Retry、Circuit 与 backlog 的
   联合行为由 Exp14 验证。
4. DeadLetter 状态为 Pending，后续人工 claim/replay/resolve 工作流仍需正式生产运维能力。

## 验证命令

```powershell
dotnet build tests/Reliant.Tests/Reliant.Tests.csproj -c Release --no-restore

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build `
  --filter "FullyQualifiedName~Integration.Phase3.Exp13" `
  --logger "console;verbosity=detailed"

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build
```

## 我的学习结论

Retry 的安全性由三个边界共同决定：

```text
classification: only retry retryable errors
budget: stop after a finite number
terminalization: clear schedule + durable dead letter + operator alert
```

实验也让我看到“聚合”不只是减少 Markdown 文件：当两个 Gate 的验证语义相同，应该共享一个
可执行 scenario，并保留轻量的 owner 入口，而不是复制几百行测试后让它们逐渐不一致。
