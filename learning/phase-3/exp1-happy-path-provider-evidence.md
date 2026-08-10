# Phase 3 / Experiment 1 — Happy Path with Provider Evidence

## 一页结论

**PASS（E2：真实 PostgreSQL + LocalStack SQS + WorkerHost）**

我使用两个确定性暂停点观察正常 Provider Happy Path：

```text
边界一：ProcessingAttempt 已 Commit，但 Provider 尚未调用
边界二：业务结果、Inbox 和 Job 已 Commit，但 SQS 尚未 ACK
```

实验确认 Provider 调用前已经存在 Pending Attempt；Provider 只创建一个 Operation；
ProviderReference、Contribution、Inbox 和状态审计完整；Worker 只在最终数据库提交后
执行一次 SQS Delete。

```text
Provider 前：Attempt=Pending，ProviderOperation=0，Inbox=0，ACK=0
ACK 前：Contribution=Succeeded，Reference=1，Inbox=Processed，ACK=0
最终：ProviderOperation=1，Send/Receive/Delete=1/1/1，Queue=empty
```

## 实验信息

- 日期：2026-08-10
- 测试目录：`tests/Reliant.Tests/Integration/Phase3/Exp1/`
- 测试：`HappyPath_ShouldPersistAttemptBeforeProvider_CommitInboxThenAck`
- Provider Mode：`Success`
- 数据库：PostgreSQL 17 Testcontainer
- Queue：LocalStack 3 SQS
- Worker：真实 WorkerHost
- 故障注入：不制造失败，只在两个真实生产边界暂停观察
- Exp1：1/1 passed

## 假设

正常处理必须满足：

```text
Contribution + Outbox + JobRun 原子提交
ProcessingAttempt 在 Provider 调用前持久化
Provider Operation 只有一个
ProviderReference 只有一个
Contribution 状态完整推进
Inbox 与最终业务状态同事务提交
数据库 Commit 后才 ACK
```

## 实验设计

### 边界一：Attempt Commit 后、Provider 调用前

生产代码已经提供 `AfterAttemptPersisted` Fault Point。Exp1 的测试 Injector 不抛
异常，而是在这里暂停 Worker。

暂停期间从一个独立 DbContext 查询 PostgreSQL，并查询 Sandbox Provider：

```text
ProcessingAttempt.Status = Pending
AttemptNumber = 1
ProviderIdempotencyKey 已保存
Contribution.State = Processing
ProviderOperationCount = 0
ProviderReferenceCount = 0
InboxCount = 0
SQS DeleteCount = 0
```

这证明 Attempt 不是 Provider 调用后的补记，而是调用前已经 Commit 的恢复证据。

### 边界二：最终 Commit 后、SQS ACK 前

释放 Provider 边界后，Worker 正常调用 Sandbox Provider。测试在
`BeforeMessageAck` 暂停。

暂停期间数据库已经可以观察到：

```text
Contribution = Succeeded
ProcessingAttempt = Succeeded
ProviderReference = 1
Inbox = Processed
JobRun = Succeeded
JobAttempt = Succeeded
StateTransition = 4
```

但 CountingQueueAdapter 显示：

```text
DeleteCount = 0
```

因此数据库 Commit 明确发生在 Queue ACK 之前。

### 最终释放 ACK

释放第二个边界后：

```text
Queue Send = 1
Queue Receive = 1
Queue Delete = 1
Queue = empty
Lease = inactive
```

## 学生视角：中间过程

### 第一次 Review：已有测试不等于完整实验

仓库原来已经有 `AttemptPersisted_BeforeProviderCall` 和多个 Final E2E，但它们分别
证明局部性质：

- Provider 测试证明 Attempt 最终存在；
- Final E2E 证明最终可以成功；
- 旧测试没有在同一真实 Outbox → SQS → Worker 流程中冻结两个事务边界。

所以我没有把旧测试改名，而是新增 Exp1 专用 E2E，让一个测试同时给出 Provider
前和 ACK 前证据。

### 业务代码 Review：不需要修改

反查现有实现后确认：

1. `CreateContributionCommand` 一次 SaveChanges 提交 Contribution、Outbox 和 JobRun；
2. `SubmitToProviderCommand` 保存 Pending Attempt 后才调用 Provider；
3. Worker 在最终 fenced transaction 中保存 Contribution、Inbox 和 Job 结果；
4. `DeleteAsync` 位于最终 Commit 和 `BeforeMessageAck` 之后；
5. Sandbox Provider 用稳定 Key 原子创建 Operation。

这些能力已经存在。为通过 Happy Path 再修改业务代码只会扩大范围，因此 Exp1
只增加实验测试和文档。

### 第一次运行：PASS

```text
BEFORE PROVIDER |
Attempt=Pending | AttemptNumber=1 |
ProviderOperationCount=0 | Inbox=0 | ACK=0

BEFORE ACK |
Contribution=Succeeded | Attempt=Succeeded |
ProviderOperationCount=1 | ProviderReference=1 |
Inbox=Processed | StateTransitions=4 |
JobRun=Succeeded | ACK=0

FINAL |
QueueSend=1 | QueueReceive=1 | QueueDelete=1 |
Queue=empty | Lease=inactive |
ProviderOperationCount=1 | ProviderReferenceCount=1 | InboxCount=1
```

## 状态转换证据

本实验通过真实 `CreateContributionCommand` 创建业务，因此完整审计包含：

```text
Created → Created       Contribution created
Created → Accepted      Worker accepted
Accepted → Processing   Worker processing
Processing → Succeeded  Provider succeeded
```

第一条是创建审计，不是重复业务处理。后三条是 Worker Happy Path 的实际状态推进。

## PASS 条件逐项判定

| PASS 条件 | 实际证据 | 判定 |
| --- | --- | --- |
| ProviderOperationCount == 1 | 最终 OperationCount=1 | PASS |
| ProviderReferenceCount == 1 | 数据库 Reference=1 | PASS |
| Contribution Succeeded | ACK 前已是 Succeeded | PASS |
| Attempt 调用前存在 | ProviderOperation=0 时 Attempt=Pending | PASS |
| 状态转换完整 | Created/Accepted/Processing/Succeeded 全部审计 | PASS |
| Inbox 与业务状态已提交 | ACK 前二者均可由独立 DbContext 查询 | PASS |
| Commit 后 ACK | ACK 前 DeleteCount=0，释放后=1 | PASS |
| Queue 最终为空 | 最终 Receive visibility=0 返回 null | PASS |

## 最终数据

| 数据 | 最终值 |
| --- | --- |
| Contribution | Succeeded |
| Outbox | Sent |
| JobRun | Succeeded |
| JobAttempt | 1 / Succeeded |
| Lease | 1 / inactive |
| ProcessingAttempt | 1 / Succeeded |
| ProviderOperation | 1 |
| ProviderReference | 1 |
| Inbox | 1 / Processed |
| StateTransition | 4 |
| DeadLetter | 0 |
| Queue Send/Receive/Delete | 1 / 1 / 1 |

## 代码影响与必要性

```text
生产代码修改：0
数据库 Migration：0
既有测试修改：0
新增：1个 Exp1 聚合 E2E + 1份聚合报告
```

测试使用已有 `IWorkerFaultInjector` 和 `CountingQueueAdapter`，没有向生产代码加入
Exp1 模式、延迟开关或特殊分支。

## 当前限制

1. Sandbox Provider 是进程内实现；真实 Provider 的 E4 合同和 Smoke 仍需单独验证。
2. 本实验验证成功路径，不替代 Response Lost、Crash 和 Callback 并发实验。
3. SQS 与 PostgreSQL 不共享事务；可靠性来自 Outbox、Inbox、稳定 Provider Key 和
   正确 ACK 顺序，不是分布式 Exactly-once Transaction。

## 验证命令

```powershell
dotnet build Reliant.slnx -c Release --no-restore

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build `
  --filter "FullyQualifiedName~Integration.Phase3.Exp1" `
  --logger "console;verbosity=detailed"

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build
```

## 我的学习结论

“最终成功”不足以证明 Provider Happy Path 可靠。真正需要观察的是两个顺序：

```text
Attempt Commit < Provider Call
Business + Inbox Commit < SQS ACK
```

第一个顺序保证 Provider 调用发生后即使 Worker 消失，仍有可恢复证据；第二个顺序
保证 ACK 不会让尚未提交的业务任务永久消失。ProviderOperationCount=1 则证明稳定
业务 Key 和 Provider 幂等共同限制了外部副作用。
