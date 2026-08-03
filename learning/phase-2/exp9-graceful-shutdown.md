# Phase 2 / Experiment 9 — Graceful Shutdown

## 一页结论

**PASS（E2：真实 Docker SIGTERM + PostgreSQL Testcontainer + LocalStack SQS）**

我向一个正在处理长任务的真实 Worker 容器发送 `SIGTERM`。Worker 收到信号后：

```text
停止领取新任务
取消当前 Provider 调用
Provider ProcessingAttempt → Unknown
JobAttempt → Abandoned
JobRun → Pending
Lease → IsActive=false
SQS Message → 不 ACK
```

第二条已经在 Queue 中等待的任务完全没有进入处理路径。Worker A 以 ExitCode 0
退出，从 SIGTERM 到退出约 716ms。

启动 Worker B 后，未 ACK 的当前消息和从未领取的第二条消息都被处理完成：

```text
Contribution Succeeded = 2
Inbox = 2
ProviderReference = 2
Queue final depth = 0
Active Lease = 0
Silent loss = false
```

## 实验信息

- 日期：2026-08-03
- 测试目录：`tests/Reliant.Tests/Integration/Phase2/Exp9/`
- 测试：
  `Sigterm_ShouldStopNewReceives_ReleaseCurrentWork_AndRecover`
- 信号：`docker kill --signal=SIGTERM`
- Worker 运行方式：真实 .NET Worker Docker 容器
- 数据库：PostgreSQL 17 Testcontainer
- Queue：LocalStack 3 SQS
- Worker A Processing Concurrency：1
- Worker A Provider Submit Delay：60 秒
- SQS Visibility Timeout：5 秒
- Exp9 专项：1/1 通过
- Phase 2 Exp1–Exp9：11/11 通过
- 最终全量：157/157 通过

## 我的假设

Graceful shutdown 不一定要求当前任务必须成功完成，但必须有明确策略：

```text
SIGTERM
  → 停止启动新的 Receive/Processing
  → 等待已启动任务执行取消清理
  → 当前 Attempt 留下明确状态
  → Job 回到可恢复状态
  → Lease 释放
  → 未完成消息不 ACK
  → 新 Worker 可以恢复
```

如果进程只停止主循环，却让正在处理消息的 Task 留在后台，容器可能在 Lease、
Attempt 和 ACK 状态落盘前退出。这不属于 graceful shutdown。

## 实验设计

### 为什么使用两条消息

只投递一条消息只能证明当前任务如何结束，不能证明 Worker 是否停止领取新任务。

因此我在 Queue 中预先放入两条消息，并把 Processing Concurrency 设为 1：

```text
Message A → Worker A 正在处理
Message B → Queue 中等待
```

SIGTERM 后要求：

- Message A 有明确中断状态；
- Message A 不 ACK；
- Message B 不产生 JobAttempt、Inbox 或业务状态转换。

### 为什么 Provider 延迟 60 秒

正常 Sandbox Provider 会很快返回，SIGTERM 很难稳定落在处理中间。实验使用
`Provider:SubmitDelayMs=60000`，并等待数据库出现以下事实后才发送信号：

```text
JobAttempt = Running
Lease = Active
Contribution = Processing
ProcessingAttempt = Pending
```

该延迟默认为 0，只是 Sandbox/Test 环境的确定性故障窗口，不改变正常 Provider
行为。

## 实验前的代码 Review

### 1. Processing Task 是 detached task

原 Handler 使用：

```text
_ = Task.Run(...)
```

`ExecuteAsync` 没有保存或等待 Task。Host 收到 SIGTERM 后，主循环可以先结束，
而消息处理仍在后台运行。

### 2. Host 退出不等待 Lease 清理

Lease release 位于处理 Task 的 `finally` 中。由于 BackgroundService 不等待该
Task，进程可能在 `finally` 执行前结束。

### 3. Cancellation 被当成普通处理异常

原处理路径没有区分：

```text
业务异常
数据库异常
Worker shutdown cancellation
```

所以无法把关闭中断准确写成 `JobAttempt=Abandoned`。

### 4. Provider Attempt cancellation 不可审计

ProcessingAttempt 在 Provider 调用前已经持久化为 Pending。Provider 调用收到
cancellation 后，原代码尝试继续使用已取消 token 保存状态，最终可能仍留下
Pending Attempt。

## 第一次真实运行：FAIL

未修复版本收到 SIGTERM 后，日志出现：

```text
Application is shutting down...
Scheduled Maintenance Handler stopped
Reconciliation Handler stopped
Outbox Publisher stopped
```

但没有出现：

```text
Processing Handler draining ...
Processing Handler stopped
```

实验等待 8 秒后仍无法得到以下完整关闭状态：

```text
JobRun = Pending
JobAttempt = Abandoned
Provider Attempt = Unknown
Lease Active = false
```

测试以：

```text
SIGTERM did not durably release the current task
```

失败。这证明信号确实到达 Host，但 Processing BackgroundService 没有把进行中的
Task 纳入关闭生命周期。

## 修复设计

### 1. 跟踪所有已启动的 Processing Task

`ProcessingHandlerService` 现在维护 in-flight Task 集合：

```text
Task 启动 → 加入 in-flight
Task 结束 → 从 in-flight 移除
SIGTERM → 停止主 Receive 循环
BackgroundService → await 剩余 in-flight Task
```

`Task.Run` 不再使用已取消 token 作为“是否启动 delegate”的条件，避免已经取得
Semaphore 后 delegate 根本不运行、Semaphore 也无法释放。

### 2. SIGTERM 后不再领取第二条任务

Host cancellation 会取消主循环和正在等待的 SQS Receive。实验把 Concurrency
设为 1，所以 Worker A 正在处理中时没有第二个 Receive slot。

关闭后第二条任务保持：

```text
Contribution = Created
JobRun = Pending
JobAttempt count = 0
Inbox count = 0
```

### 3. 当前 Provider Attempt 保守记录为 Unknown

当 Provider 调用被 Worker shutdown cancellation 中断时，不能假设 Provider
一定没有执行副作用。因此使用保守语义：

```text
ProcessingAttempt.Status = Unknown
ErrorCategory = UnknownOutcome
CompletedAt = shutdown time
```

这条审计使用 `CancellationToken.None` 保存，因为原 Host token 已经取消。
保存完成后重新抛出 cancellation，让 Worker 的 Job 层执行释放。

### 4. Job 清楚表达“本次 Owner 放弃”

Processing Handler 捕获 shutdown cancellation 后：

```text
JobAttempt = Abandoned
JobRun = Pending
Error = Worker graceful shutdown interrupted processing
```

`Abandoned` 表示这不是业务失败，也没有消耗业务 Retry budget；它只是当前 Worker
不再拥有该次执行。

### 5. 释放 Lease，但不 ACK

处理中断后：

```text
Lease.IsActive = false
Inbox 不写入
SQS DeleteMessage 不调用
```

消息在 Visibility Timeout 后重新可见。Worker B 使用相同 MessageId 和 Provider
Idempotency Key 恢复，不会把中断误认为成功。

## 实际运行结果

```text
SIGNAL | Type=SIGTERM | WorkerAExitCode=0 | SignalToExitMs=716

SHUTDOWN | NewWorkAccepted=false | ActiveJob=Pending | ActiveAttempt=Abandoned | ProviderAttempt=Unknown | LeaseActive=false | Acked=false | Checkpoints=0

QUEUE | BeforeRestartVisible=1 | BeforeRestartInFlight=1 | RecoverableMessages=2 | FinalDepth=0

RECOVERY | Contributions=2:Succeeded | Inbox=2 | JobAttempts=3 | ProcessingAttempts=3 | ProviderReferences=2 | ActiveLeases=0

RESULT | PASS | SilentLoss=false
```

Queue 在重启前显示：

```text
Visible = 1
InFlight = 1
```

含义是：

- 从未领取的 Message B 仍然可见；
- 中断且未 ACK 的 Message A 仍在 Visibility 窗口；
- 两条消息总数仍为 2，没有静默丢失。

## 关闭后的数据库状态

### 当前任务 Message A

| 检查项 | SIGTERM 后 |
| --- | --- |
| Contribution | Processing |
| JobRun | Pending |
| JobAttempt | Abandoned |
| ProcessingAttempt | Unknown |
| Lease | Inactive |
| Inbox | 0 |
| ACK | 未执行 |

Contribution 保持 Processing 是有意的。Worker B 收到同一 MessageId 后走
“redelivery/recovery”入口，不重复执行 Created → Accepted → Processing。

### 未领取任务 Message B

| 检查项 | SIGTERM 后 |
| --- | --- |
| Contribution | Created |
| JobRun | Pending |
| JobAttempt | 0 |
| ProcessingAttempt | 0 |
| Inbox | 0 |

这证明 Worker 收到停止信号后没有继续启动新业务处理。

### Worker B 恢复后

| 检查项 | 最终值 |
| --- | ---: |
| Succeeded Contribution | 2 |
| Succeeded JobRun | 2 |
| Inbox | 2 |
| ProviderReference | 2 |
| JobAttempt | 3 |
| Abandoned JobAttempt | 1 |
| Succeeded JobAttempt | 2 |
| ProcessingAttempt | 3 |
| Unknown ProcessingAttempt | 1 |
| Succeeded ProcessingAttempt | 2 |
| Active Lease | 0 |
| Queue Depth | 0 |

三个 Attempt 的原因是：

```text
Message A：Worker A Abandoned + Worker B Succeeded
Message B：Worker B Succeeded
```

## Checkpoint 检查的准确结论

当前 Contribution Processing 工作流不是 checkpointed workload，没有在处理过程
中调用 `ICheckpointRepository`。因此本实验实际检查到：

```text
Checkpoint rows before/after shutdown = 0
```

它证明关闭没有留下虚构或陈旧 Checkpoint，但不能证明“从 Checkpoint 继续执行”。
真正的 checkpoint resume 必须使用会周期性保存进度的长 Job 单独实验，不能把
`0 rows` 写成该能力已经完成。

## PASS 条件逐项判定

| PASS 条件 | 证据 | 判定 |
| --- | --- | --- |
| 无新任务继续进入 | Message B 保持 Created/Pending，Attempt=0 | PASS |
| 当前任务行为明确 | Unknown + Abandoned + Job Pending + Lease released | PASS |
| 无任务静默丢失 | 重启前 Queue 总数=2，最终两条均 Succeeded | PASS |

## 生产代码修改及必要性

| 修改 | 类型 | 原因 |
| --- | --- | --- |
| Processing Concurrency 可配置 | Worker 运行配置 | 默认仍为 10；实验和容量控制需要 |
| 跟踪并 await in-flight tasks | 生命周期修复 | 否则 Host 可先于清理退出 |
| Shutdown cancellation 单独分类 | Job 审计修复 | 不能把关闭误记成业务失败 |
| Provider Attempt 持久化 Unknown | 业务边界审计 | 外部调用中断时结果不能假定 |
| Sandbox Submit Delay | 测试支撑 | 默认 0，只制造确定性长任务 |

没有修改 Contribution 正常成功/失败状态转换、Retry budget、Inbox dedup、
Provider idempotency 或 Lease acquisition 规则。

## 当前限制

### 1. 本实验验证的是 Processing Handler

Notification Handler 目前仍是 Skeleton，没有纳入本实验的长任务 shutdown
验证。它正式实现前，也必须采用同样的 in-flight 生命周期管理，不能把本实验
外推为所有未来 Handler 都已自动安全。

### 2. 依赖操作响应 CancellationToken

Graceful release 要求数据库、HTTP Provider 和 Queue SDK 能响应 cancellation。
如果外部 SDK 永久忽略 token，Host 最终仍受全局 ShutdownTimeout 和容器平台
termination grace period 约束。

生产部署时必须保证：

```text
orchestrator terminationGracePeriod
  > Host ShutdownTimeout
  > 正常 cancellation cleanup 时间
```

本地 Docker 证据是 716ms，但它不是生产环境延迟上限。

### 3. SIGKILL 仍由 Exp4/Exp5 负责

Graceful shutdown 不能替代 crash recovery：

- SIGTERM：主动取消、写审计、释放 Lease；
- SIGKILL：来不及清理，依赖 Visibility Timeout + Lease Expiry 接管。

两条恢复路径都必须保留。

## 验证命令

```powershell
dotnet build Reliant.slnx -c Release --no-restore

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build `
  --filter "FullyQualifiedName~Phase2.Exp9" `
  --logger "console;verbosity=detailed"

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build `
  --filter "FullyQualifiedName~Integration.Phase2.Exp"

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build
```

最终结果：

```text
Build: 0 errors
Exp9: 1/1 passed
Phase 2 Exp1–Exp9: 11/11 passed
Full suite: 157/157 passed
```

构建仍有仓库既存 package vulnerability / obsolete API 警告，本实验没有把它们
写成已处理。

## 我的学习结论

我原来容易把 graceful shutdown 理解成“收到 SIGTERM 后循环停了”。这次实验
证明，真正的关闭完成条件应该是：

```text
不再创建新工作
已经启动的工作被 Host 跟踪
取消结果被持久化
Lease 已释放
未完成消息未 ACK
进程最后才退出
```

Graceful shutdown 和 crash recovery 不是二选一。SIGTERM 尽量留下干净状态，
SIGKILL 则依赖 Lease/Visibility 自愈。正式 SRE 系统必须同时验证两条路径。
