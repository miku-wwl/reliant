# Phase 3 / Experiment 9 — Callback Before Submit Response

## 一页结论

**PASS（E2：真实 WorkerHost + LocalStack SQS + PostgreSQL）**

实验把 Worker 精确暂停在 Provider 已完成 Operation、但 Submit Response 尚未被本地处理的
边界。此时 Callback 先把 Contribution 从 Processing 改为 Succeeded；恢复迟到响应后，
Worker 清空 EF Tracking、重新读取 Contribution，并识别 Callback 已提交的终态，没有再次
推进状态。

```text
ProviderOperation = 1
Callback Inbox = 1
Succeeded Transition = 1（CallbackHandler）
Worker Succeeded Transition = 0
Contribution final = Succeeded
```

重复发送相同 Callback 返回 200，但 Inbox 仍只有一条；随后执行 Reconciliation 得到安全
跳过，不生成 ReconciliationRecord。

## 实验信息

- 日期：2026-08-11
- 测试目录：`tests/Reliant.Tests/Integration/Phase3/Exp9/`
- 测试类：`CallbackBeforeSubmitResponseE2ETests`
- 数据库：PostgreSQL 17 Testcontainer
- 队列：LocalStack SQS
- Worker：真实 `ProcessingHandlerService`
- Provider：Sandbox Success mode
- Exp9：1/1 passed

## 假设

```text
Callback 先提交 Succeeded
Submit Response 后到
Worker 必须重新读取数据库状态
迟到 Worker 不得覆盖 Callback 的正确终态
```

## 实验设计

使用测试专用 `IWorkerFaultInjector` 在
`WorkerFaultPoint.AfterProviderProcessed` 建立确定性屏障：

```text
Outbox -> SQS -> Worker -> Processing
Worker persists ProcessingAttempt(Pending)
Provider creates Operation
                    |
                    +-- pause response handling
Callback by ProviderIdempotencyKey
Processing -> Succeeded + Callback Inbox commit
                    |
                    +-- release response
Worker persists Attempt(Succeeded) + ProviderReference
Worker ChangeTracker.Clear() + reload
Worker sees Succeeded -> skips second transition
Worker Inbox + Job success + SQS ACK
```

这个屏障不是 `Task.Delay` 猜时序，而是在 Provider effect 已经存在的准确代码边界暂停，
因此实验可重复并且能证明真正的并发窗口。

## 学生视角：中间过程

### 第一次 Review：旧测试并没有制造“Callback Before Response”

仓库原有测试名为
`CallbackBeforeSubmitResponse_ShouldNotBeOverwrittenByWorker`，但实际流程是：

```text
Submit 返回 Unknown
Callback 到达
手工再发一次 Submit command
```

它验证了 Provider idempotency，却没有让 Callback 与正在执行的 Worker Submit Response
发生竞态，也没有覆盖 Worker 的 EF reload、Job/Lease、Inbox 和 SQS ACK。

所以本次删除这条旧测试，换成一条真实 WorkerHost E2E。测试总数不增加，证据层级从
Handler + PostgreSQL 提升为 WorkerHost + LocalStack + PostgreSQL。

### 边界一：Provider 已完成，Worker 尚未处理响应

在屏障处观察到：

```text
Contribution = Processing
ProcessingAttempt = 1 / Pending
ProviderOperation = 1
ProviderReference = 0
Processing Inbox = 0
Queue Delete = 0
```

这说明外部副作用已经发生，而本地 Submit 成功证据尚未提交，正是本实验需要的竞态窗口。

### 边界二：Callback 抢先提交

Callback 使用已持久化的 ProviderIdempotencyKey 定位 Contribution，并原子提交：

```text
Contribution: Processing -> Succeeded
StateTransition.ChangedBy = CallbackHandler
Callback Inbox = callback-{EventId}
```

此时 Worker 仍停在响应屏障，ProcessingAttempt 仍为 Pending，ProviderReference 仍为 0。

### 边界三：迟到 Submit Response 恢复

Worker 恢复后先将 ProcessingAttempt 和 ProviderReference 持久化，然后执行已有保护逻辑：

```text
dbContext.ChangeTracker.Clear()
fresh read Contribution
state changed Processing -> Succeeded
skip Worker state transition
```

最终状态变化总数为 4：

```text
Created -> Created
Created -> Accepted
Accepted -> Processing
Processing -> Succeeded（CallbackHandler）
```

不存在第二条 Worker 写入的 Succeeded transition，因此没有 Lost Update。

### 重复 Callback 与后续 Reconciliation

相同 EventId 再发送一次仍返回 200。Inbox MessageId 唯一约束保留唯一记录，Contribution
和 StateTransition 都不再变化。

Contribution 已是 Succeeded，所以手工触发 Reconciliation 返回：

```text
Resolved = true
Resolution = Not in reconciliation state, skipping
ReconciliationRecord = 0
```

## PASS 条件逐项判定

| PASS 条件 | 实际证据 | 判定 |
| --- | --- | --- |
| Contribution 最终 Succeeded | 最终读取 Succeeded | PASS |
| Callback Inbox 只有一条 | 重复发送后 count=1 | PASS |
| 无 Lost Update | Worker reload 后保留 Callback 终态 | PASS |
| 无第二次状态变化 | Succeeded transition=1，ChangedBy=CallbackHandler | PASS |
| 后续 Reconciliation 安全跳过 | skip，ReconciliationRecord=0 | PASS |

## 最终数据

| 数据 | 最终值 |
| --- | --- |
| Contribution | Succeeded |
| Provider Operation | 1 |
| ProcessingAttempt | 1 / Succeeded |
| ProviderReference | 1 |
| Callback Inbox | 1 / Processed |
| Processing Inbox | 1 / Processed |
| Succeeded Transition | 1 / CallbackHandler |
| JobRun / JobAttempt | Succeeded / Succeeded |
| Active Lease | 0 |
| ReconciliationRecord | 0 |
| DeadLetterRecord | 0 |
| Queue Send/Receive/Delete | 1/1/1，最终 empty |

## 业务代码与测试聚合 Review

```text
生产代码修改：0
数据库 Migration：0
删除：CallbackTests 中1条非并发旧用例
新增：Exp9 中1条真实 WorkerHost 竞态 E2E
测试总数净变化：0
```

业务代码不需要修改。`ProcessingHandlerService` 已经在 Provider call 后执行真实 reload，
并对 Callback 抢先写入的 Succeeded/Failed 终态显式跳过后续状态推进。本实验只是让这段
已有保护第一次在真实竞态、真实队列和真实 Job 生命周期中得到证据。

若为了本实验再加入第二套锁、Callback 特判或额外状态字段，会形成重复机制，增加状态机
复杂度，因此没有保留任何这类修改。

## 当前限制

1. 本实验直接调用 Callback command，Exp7/Exp8 已分别覆盖真实 HTTP HMAC 和重复 HTTP；
   这里聚焦 Worker 与 Callback 的数据库竞态，避免重复搭建同一 HTTP 证据。
2. Sandbox Provider 在进程内运行；外部真实 Provider 的网络延迟需在 E3/E4 环境补充，但
   不改变本地事务和 reload 语义。
3. 屏障期间使用默认 Visibility/Lease 配置；长任务的双 Heartbeat 已由 Phase 2 Exp12
   独立验证。

## 验证命令

```powershell
dotnet build tests/Reliant.Tests/Reliant.Tests.csproj -c Release --no-restore

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build `
  --filter "FullyQualifiedName~Integration.Phase3.Exp9" `
  --logger "console;verbosity=detailed"

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build
```

## 我的学习结论

乐观并发版本只能发现冲突，真正避免迟到响应覆盖 Callback 的关键是：在外部调用返回后
丢弃旧 EF tracking snapshot，重新读取数据库中的最新状态，再决定是否推进状态机。

```text
external call boundary -> reload -> terminal-state guard -> commit
```

这条顺序让 Callback 和 Worker 无论谁先完成，系统都能收敛到一个可审计的业务结果。
