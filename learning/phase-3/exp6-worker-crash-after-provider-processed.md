# Phase 3 / Experiment 6 — Worker Crash after Provider Processed

## 一页结论

**PASS（E2：真实 PostgreSQL + LocalStack SQS + WorkerHost）**

Provider 已按稳定 Key 创建成功 Operation，但 Worker 在处理响应前丢失当前执行。此时
第一次 ProcessingAttempt 仍为 `Pending`，本地没有 ProviderReference、Inbox 或 ACK。

Visibility Timeout 到期后同一消息重投。第二条 Attempt 使用完全相同的 Provider Key，
Sandbox Provider 返回原 Operation 的 Reference，而不是创建第二个 Effect。最终
Contribution、JobRun 和 Inbox 成功收敛。

```text
AFTER PROVIDER | Attempt1=Pending | Operation=1 | Reference=0 | ACK=0
REDELIVERY     | Attempt2 same key | idempotent provider replay
FINAL          | Succeeded | Operation=1 | Reference=1 | Queue=empty
```

## 实验信息

- 日期：2026-08-11
- 测试目录：`tests/Reliant.Tests/Integration/Phase3/Exp6/`
- 主测试：`CrashAfterProviderProcessed_ShouldRedeliverAndReplaySameProviderOperation`
- 辅助测试：2 个 Provider crash-recovery integration tests
- Provider Mode：`Success`
- Visibility Timeout：5 秒
- Fault Point：`AfterProviderProcessed`
- 数据库：PostgreSQL 17 Testcontainer
- Queue：LocalStack 3 SQS
- Worker：真实 WorkerHost
- Exp6：3/3 passed

## 假设

```text
Provider 成功与本地响应处理之间存在崩溃窗口
恢复时不能生成新的业务 Key
同一 Key 的再次 Submit 必须返回原 Provider Operation
```

## 实验设计

Exp6 Fault 在 Sandbox Provider 已返回成功结果、但 `SubmitToProviderHandler` 尚未将
结果写入 Attempt 和 ProviderReference 时暂停。暂停期间由独立 DbContext 读取证据，
然后释放 Fault 并抛出专门的 `InjectedWorkerCrashException`。

```text
CreateContribution
  -> Outbox -> SQS -> Worker
  -> Attempt 1 Pending committed
  -> Provider Submit creates Operation
  -> AfterProviderProcessed pause
  -> observe DB / Provider / Queue
  -> injected worker execution loss
  -> message remains unacked
  -> Visibility Timeout
  -> same message redelivery
  -> Attempt 2 uses same ProviderIdempotencyKey
  -> Provider returns original operation/reference
  -> Contribution + Inbox + Job commit
  -> ACK
```

Processing concurrency 固定为 1，确保第一次执行退出前不会由同一 Host 的另一个处理任务
抢先进入该消息。

## 学生视角：中间过程

### 第一次 Review：原 Fault 会被误认为 Provider Timeout

现有 `AfterProviderProcessed` 和 `BeforeProviderResponseHandled` Fault Point 位于
`SubmitToProviderHandler` 的 Provider `try/catch` 内。如果 Injector 抛普通异常，通用
catch 会把它转换成：

```text
Attempt = Unknown
ErrorCategory = Timeout
return ProviderSubmissionResult.Unknown
```

随后 Worker 会正常写 Inbox 并 ACK。这样验证到的是“响应丢失”，不是“Worker 丢失、
消息重投”。Fault Point 的名字和实际行为不一致。

### 必要的小修复

增加专门的 `InjectedWorkerCrashException`，并在 Provider catch 中明确重新抛出：

```text
Injected worker crash
  != provider timeout
  -> escape Submit handler
  -> worker records interrupted JobAttempt
  -> no Inbox
  -> no Queue Delete
```

生产默认 `IWorkerFaultInjector` 是 `NoopWorkerFaultInjector`，所以正常业务请求不会创建或
抛出这个异常。修改只校正故障注入协议，不改变 Provider 成功、失败、Timeout 或状态机
的生产判定。

### 第一个观察点：Provider 已处理，本地响应尚未处理

Fault 暂停时：

```text
Contribution.State = Processing
Attempt 1 = Pending
Attempt 1 ProviderKey = stable key
Attempt 1 ProviderReference = null
Attempt 1 CompletedAt = null
ProviderOperationCount = 1
ProviderReferenceCount = 0
InboxCount = 0
Queue Send/Receive/Delete = 1/1/0
```

这正是最危险的窗口：外部副作用已经发生，本地只保留调用前的 Pending Evidence。

### 第二个观察点：重投使用相同 Key

释放 Fault 后，第一次 Worker 处理任务退出，JobAttempt 记为 Failed，JobRun 回到
Pending，消息不 ACK。5 秒后重新 Receive：

```text
Attempt 1 = Pending
Attempt 2 = Succeeded
Distinct ProviderIdempotencyKey = 1
Attempt 2 ProviderReference != null
ProviderOperationCount = 1
```

Sandbox Provider 的原子 Key 索引命中第一次 Operation，第二次 Submit 返回原结果。

### 最终恢复

```text
Contribution = Succeeded
ProviderReference = 1
Inbox = 1 / Processed
JobRun = Succeeded / AttemptCount=2
JobAttempts = Failed, Succeeded
StateTransitions = 4
DeadLetter = 0
Queue Receive >= 2
Queue Delete = 1
Queue = empty
```

第一次 ProcessingAttempt 保持 Pending 是合理的 crash evidence：Worker 在 Provider 返回后
来不及更新它。第二条 Succeeded Attempt 和唯一 ProviderReference 记录恢复结果。

## PASS 条件逐项判定

| PASS 条件 | 实际证据 | 判定 |
| --- | --- | --- |
| ProviderOperationCount == 1 | 崩溃前为 1，重投后仍为 1 | PASS |
| 最终 Succeeded | Contribution 和 JobRun 均 Succeeded | PASS |
| 没有重复 ProviderReference | 最终 ReferenceCount=1 | PASS |
| 重投使用相同 Key | Attempts=2，Distinct Key=1 | PASS |

## 最终数据

| 数据 | 最终值 |
| --- | --- |
| Contribution | Succeeded |
| ProcessingAttempt | 2：Pending、Succeeded |
| Distinct Provider Key | 1 |
| ProviderOperation | 1 |
| ProviderReference | 1 |
| Inbox | 1 / Processed |
| JobRun | Succeeded / AttemptCount=2 |
| JobAttempt | Failed、Succeeded |
| StateTransition | 4 |
| Queue Send | 1 |
| Queue Receive | 2 |
| Queue Delete | 1 |
| DeadLetter | 0 |
| Queue | empty |

## 业务代码修改必要性 Review

```text
生产代码修改：2处，小范围
1. WorkerFaultPoint.cs：新增明确的 InjectedWorkerCrashException
2. SubmitToProviderCommand.cs：该异常直接 rethrow，不参与 Provider 错误分类
业务状态机修改：0
数据库 Migration：0
Provider Key 算法修改：0
```

这两处必须一起保留：只有异常类型而不 rethrow，仍会被误判 Timeout；只有 catch 规则而
没有明确类型，就只能用脆弱的异常消息或把所有 InvalidOperationException 穿透。

Review 后没有保留以下不必要方案：

- 没有增加 Exp6 专用 Provider Mode；
- 没有修改 SQS Adapter；
- 没有修改 Contribution 状态机；
- 没有让所有 Provider 异常穿透；
- 没有为测试加入生产配置开关。

测试整理方面，旧 `CrashRecoveryTests.cs` 已迁入 `Phase3/Exp6/`；其中与 Exp4 重复的
`DuplicateInboxDelivery` 被删除，其余两个 Provider 恢复测试保留。新增主 E2E 后测试
总数净不变。

## 当前限制

1. 主测试模拟当前 Worker 处理执行丢失，而不是 `docker kill` 整个进程；真实进程级
   Crash/Lease 接管已由 Phase 2 Exp4/Exp5 验证。
2. Sandbox Provider 在 WorkerHost 进程内，因此进程级 kill 会同时清空 Sandbox 内存
   Operation，不能用于证明外部 Provider 的持久化幂等；Exp6 选择保持 Provider 实例。
3. 真实 Provider 必须在自身存储中持久保存 Idempotency Key 与 Operation 的映射，E4
   仍需合同测试或 Provider Smoke 证据。

## 验证命令

```powershell
dotnet build Reliant.slnx -c Release --no-restore

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build `
  --filter "FullyQualifiedName~Integration.Phase3.Exp6" `
  --logger "console;verbosity=detailed"

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build
```

## 我的学习结论

可靠恢复需要区分两个错误域：

```text
Provider error -> classify provider outcome
Worker crash   -> do not fabricate provider outcome; leave message recoverable
```

Provider 调用前持久化 Pending Attempt，使崩溃后仍有稳定 Key；Provider 端按这个 Key
返回原 Operation，使第二次 Attempt 能补齐本地 Reference。两者共同消除崩溃窗口中的
重复外部副作用。
