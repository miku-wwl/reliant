# Phase 3 / Experiment 4 — Same SQS Message Redelivery

## 一页结论

**PASS（E2：真实 PostgreSQL + LocalStack SQS + WorkerHost）**

Worker 在 Provider Success、业务结果与 Inbox 已 Commit 后，于 SQS Delete 前触发一次
`BeforeMessageAck` 故障。第一次处理没有 ACK；Visibility Timeout 到期后，LocalStack
重新投递同一个逻辑 MessageId，SQS 原生 `ApproximateReceiveCount` 从 1 增至 2。

第二次处理被 Inbox 唯一记录识别为重复，Worker 没有再次进入业务处理或调用
Provider，只执行最终 Delete。队列清空，业务副作用保持一次。

```text
BEFORE ACK | DB committed | Inbox=1 | ProviderOperation=1 | Delete=0
REDELIVERY | same MessageId | ReceiveCount=2 | ApproximateReceiveCount=2
FINAL      | Inbox=1 | Attempt=1 | ProviderOperation=1 | Queue=empty
```

## 实验信息

- 日期：2026-08-11
- 测试目录：`tests/Reliant.Tests/Integration/Phase3/Exp4/`
- 测试：`CrashBeforeMessageAck_ShouldRedeliverAndDeduplicate_WithoutSecondProviderEffect`
- Provider Mode：`Success`
- Visibility Timeout：3 秒
- Fault Point：`BeforeMessageAck`，仅第一次抛异常
- 数据库：PostgreSQL 17 Testcontainer
- Queue：LocalStack 3 SQS
- Worker：真实 WorkerHost
- Exp4：1/1 passed

## 假设

```text
数据库 Commit 与 SQS ACK 不是同一个事务
Commit 后、ACK 前崩溃会导致 at-least-once Redelivery
Inbox 必须把同一 MessageId 的第二次投递变成无副作用 ACK
```

## 实验设计

测试只向 SQS 发布一条 Outbox 消息，并用 Queue Evidence Adapter 记录：

- Outbox 逻辑 MessageId；
- 每次 Receive 的逻辑 MessageId；
- SQS 原生 `ApproximateReceiveCount`；
- Send、Receive、Delete 次数。

一次性 `ThrowingFaultInjector` 在 `BeforeMessageAck` 抛异常：

```text
Outbox -> SQS -> Receive #1
  -> Contribution Succeeded
  -> ProcessingAttempt Succeeded
  -> ProviderReference written
  -> Inbox Processed
  -> JobRun Succeeded
  -> transaction Commit
  -> BeforeMessageAck throws
  -> Delete not called

Visibility expires
  -> Receive #2, same logical MessageId
  -> Inbox dedup hit
  -> no Provider call
  -> Delete called
```

## 学生视角：中间过程

### 第一次 Review：不复制旧测试

仓库根目录原有 `CrashBeforeAckE2ETests.cs`，与 Exp4 是同一个实验。为了避免文件越来越
多，我没有新增一个内容重复的测试，而是：

1. 把旧测试迁入 `Integration/Phase3/Exp4/`；
2. 保留原测试语义；
3. 补充 JobRun、逻辑 MessageId 和 SQS 原生 Receive Count 证据；
4. 增加可直接写入报告的三段输出。

因此本实验测试总数不增加，旧根目录文件也不再保留。

### 第一个观察点：Commit 已完成，ACK 尚未发生

测试等待故障日志出现后，用独立 DbContext 查询：

```text
Contribution = Succeeded
ProcessingAttempt = 1 / Succeeded
ProviderReference = 1
Inbox = 1 / Processed
JobRun = Succeeded
ProviderOperation = 1
Queue Send = 1
Queue Receive = 1
Queue Delete = 0
```

`Delete=0` 与已 Commit 的业务数据同时成立，证明 Fault 确实位于数据库事务之后、
Queue ACK 之前。

### 第二个观察点：同一个 MessageId 重投

第一次 Receive 后不 Delete，3 秒 Visibility Timeout 到期。第二次 Receive 的证据：

```text
ReceivedMessageIds = [OutboxId, OutboxId]
ReceiveCount = 2
MaxApproximateReceiveCount = 2
```

SQS 自己生成的物理 ID 与 Reliant 的业务去重 ID 不是同一概念。实验使用 Outbox Id
作为消息属性中的稳定逻辑 MessageId，Worker 和 Inbox 都以它去重。

### 第三个观察点：Inbox dedup 只 ACK，不再执行业务

第二次收到相同 MessageId 时，Worker 在处理入口发现 Inbox 已存在，并记录
`already processed (inbox dedup)`。最终：

```text
InboxCount = 1
ProcessingAttemptCount = 1
ProviderReferenceCount = 1
ProviderOperationCount = 1
DeleteCount = 1
DeadLetterCount = 0
Queue = empty
```

## PASS 条件逐项判定

| PASS 条件 | 实际证据 | 判定 |
| --- | --- | --- |
| ReceiveCount >= 2 | Receive=2，ApproximateReceiveCount=2 | PASS |
| ProviderOperationCount == 1 | 首次成功后和重投后均为 1 | PASS |
| InboxCount == 1 | 同一逻辑 MessageId 只有一条 Inbox | PASS |
| AttemptCount == 1 | 重投没有创建第二条 Attempt | PASS |
| Queue 最终为空 | dedup 分支 Delete 后探测为空 | PASS |

## 最终数据

| 数据 | 最终值 |
| --- | --- |
| Contribution | Succeeded |
| ProcessingAttempt | 1 / Succeeded |
| ProviderOperation | 1 |
| ProviderReference | 1 |
| Inbox | 1 / Processed |
| JobRun | Succeeded |
| Queue Send | 1 |
| Queue Receive | 2 |
| Max ApproximateReceiveCount | 2 |
| Queue Delete | 1 |
| DeadLetter | 0 |
| Queue | empty |

## 业务代码必要性 Review

```text
生产代码修改：0
数据库 Migration：0
测试总数变化：0
测试整理：旧 CrashBeforeAckE2ETests 迁入 Phase3/Exp4 并增强证据
新增：1份聚合报告
```

现有生产路径已经正确：

1. Worker 在最终业务事务中提交 Contribution、Inbox、JobRun 与 Attempt；
2. `BeforeMessageAck` 位于 Commit 后、`DeleteAsync` 前；
3. 异常时消息保持未 ACK；
4. SQS Visibility 到期后允许再次 Receive；
5. Worker 在业务处理前查询 Inbox；
6. Inbox 命中时直接 Delete，不调用 Provider；
7. Inbox MessageId 存在唯一约束。

实验只增强观察能力，没有理由再次修改这些生产语义。

## 当前限制

1. 测试中的故障是进程内一次性异常，能够验证 Worker 行为；真正 `docker kill` 的宿主
   进程级恢复已在 Phase 2 Worker Crash 实验验证。
2. Exp4 验证同一物理消息的 Redelivery；两个不同物理消息但相同 Contribution 属于
   Exp5。
3. LocalStack 提供 E2 证据；真实 AWS SQS 的 E4 Smoke 仍需单独执行。

## 验证命令

```powershell
dotnet build Reliant.slnx -c Release --no-restore

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build `
  --filter "FullyQualifiedName~Integration.Phase3.Exp4" `
  --logger "console;verbosity=detailed"

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build
```

## 我的学习结论

At-least-once Queue 中，正确目标不是阻止 Redelivery，而是让 Redelivery 可恢复、可解释
且无重复业务副作用：

```text
Commit -> crash -> same message redelivery -> Inbox hit -> ACK
```

Inbox 是数据库事务与 Queue ACK 之间的恢复桥梁。它不能让消息只投递一次，但能保证
同一业务消息的第二次处理不再跨过 Provider 副作用边界。
