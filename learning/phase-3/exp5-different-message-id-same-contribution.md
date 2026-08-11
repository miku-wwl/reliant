# Phase 3 / Experiment 5 — Different MessageId, Same Contribution

## 一页结论

**PASS（E2：真实 PostgreSQL + LocalStack SQS + WorkerHost）**

第一条消息把 Contribution 正常处理到 `Succeeded`。随后创建新的 OutboxMessage 与
JobRun，使用新的 MessageId，但 Payload 指向相同 ContributionId。

第二条消息拥有自己的 Inbox 和 ACK，因此不是同一 MessageId 的 Inbox dedup；Worker
依靠 Contribution 终态保护直接完成 Job，不再进入 Provider 提交路径。最终状态转换、
Attempt、ProviderReference 和 Provider Operation 均没有增加。

```text
Message A != Message B
Inbox A + Inbox B = 2
Contribution = Succeeded
StateTransitions = 4 -> 4
Attempts/References/ProviderOperations = 1/1/1
```

## 实验信息

- 日期：2026-08-11
- 测试目录：`tests/Reliant.Tests/Integration/Phase3/Exp5/`
- 测试：`NewMessageIdForSucceededContribution_ShouldAckWithoutNewBusinessEffect`
- Provider Mode：`Success`
- 数据库：PostgreSQL 17 Testcontainer
- Queue：LocalStack 3 SQS
- Worker：真实 WorkerHost
- Exp5：1/1 passed

## 假设

```text
不同 MessageId 不能由 Inbox MessageId 唯一约束互相去重
因此必须由业务终态和稳定 Provider 语义阻止第二次业务处理
```

## 实验设计

第一条消息由真实 `CreateContributionCommand` 创建：

```text
Contribution + Outbox A + JobRun A
  -> SQS Message A
  -> Worker -> Provider Success
  -> Contribution Succeeded
  -> Inbox A + ACK A
```

待第一条完整结束后，在同一数据库事务中创建：

```text
Outbox B.Id != Outbox A.Id
JobRun B.Id = Outbox B.Id
Payload.ContributionId = 原 ContributionId
```

第二条消息经过真实 Outbox Publisher 和 LocalStack SQS。Worker 读取数据库业务事实，
发现 Contribution 已是终态，执行：

```text
Inbox B Processed
JobRun B Succeeded
ACK B
no SubmitToProviderCommand
no StateTransition
```

## 学生视角：中间过程

### 第一次 Review：Exp4 和 Exp5 的去重层不同

开始时我容易把 Exp5 当成 Exp4 的重复，但两者的保护层不同：

| 场景 | MessageId | 第一保护层 |
| --- | --- | --- |
| Exp4 同一消息重投 | 相同 | Inbox unique MessageId |
| Exp5 新消息指向同一业务 | 不同 | Contribution terminal state |

Exp5 的第二条消息应该产生新的 Inbox，因为它确实是新的逻辑消息。正确结果不是
`InboxCount=1`，而是 `InboxCount=2` 且业务副作用仍为 1。

### 第一条消息完成后的基线

```text
MessageId A = original Outbox Id
Contribution.State = Succeeded
StateTransitionCount = 4
ProcessingAttemptCount = 1
ProviderReferenceCount = 1
ProviderOperationCount = 1
InboxCount = 1
```

四条状态审计来自真实 Command 和 Worker：

```text
Created -> Created
Created -> Accepted
Accepted -> Processing
Processing -> Succeeded
```

### 第二条消息使用不同 MessageId

```text
MessageId B != MessageId A
Payload.ContributionId = same ContributionId
Outbox B = Sent
Inbox B = Processed
JobRun B = Succeeded
```

日志出现 `idempotent ACK without submit`，说明不是 Provider 自己在第二次调用时去重，
而是 Reliant 在调用 Provider 之前就由业务终态短路。

### 最终无新增业务推进

```text
Contribution.State = Succeeded
StateTransitionCount = 4
ProcessingAttemptCount = 1
ProviderReferenceCount = 1
ProviderOperationCount = 1
OutboxCount = 2
InboxCount = 2
JobRunCount = 2
Queue Send/Receive/Delete = 2/2/2
```

## PASS 条件逐项判定

| PASS 条件 | 实际证据 | 判定 |
| --- | --- | --- |
| ProviderOperationCount 仍为 1 | 第二条消息后 Operation=1 | PASS |
| ProviderReferenceCount 仍为 1 | 第二条消息后 Reference=1 | PASS |
| Contribution 仍为 Succeeded | 最终 State=Succeeded | PASS |
| 无新的业务状态推进 | StateTransition 4 -> 4 | PASS |

## 最终数据

| 数据 | 最终值 |
| --- | --- |
| Logical MessageId | 2 个不同值 |
| Contribution | 1 / Succeeded |
| Outbox | 2 / Sent |
| Inbox | 2 / Processed |
| JobRun | 2 / Succeeded |
| StateTransition | 4，第二条消息增加 0 |
| ProcessingAttempt | 1 / Succeeded |
| ProviderReference | 1 |
| ProviderOperation | 1 |
| DeadLetter | 0 |
| Queue Send/Receive/Delete | 2 / 2 / 2 |
| Queue | empty |

## 测试聚合与业务代码必要性 Review

```text
生产代码修改：0
数据库 Migration：0
删除：根目录 DuplicateMessageE2ETests.cs（5个重叠测试）
替代：Phase3/Exp4 1个综合测试 + Phase3/Exp5 1个综合测试
本次新增：1个 Exp5 聚合 E2E + 1份聚合报告
```

旧文件中的五个测试分成两组：同一 MessageId Redelivery，以及不同 MessageId 指向
同一 Contribution。Exp4 已完整覆盖前三个测试的 Receive/Inbox/Provider 断言；Exp5
完整覆盖后两个测试，并额外验证两套 Outbox、Inbox、JobRun、Queue 操作和状态转换
不增加。因此删除旧文件不会丢失行为覆盖，反而减少三个重复测试和一个根目录文件。

`docs/evidence/phase-3.1.md` 和早期 Phase 2 报告仍保留旧测试名，因为它们记录的是对应
历史 commit 的 CI/学习证据；当前可执行入口以 `Phase3/Exp4` 和 `Phase3/Exp5` 为准。

现有生产代码已满足 Exp5：

1. Worker 从数据库读取业务状态，不信任消息中的业务快照；
2. Succeeded/Failed/Completed 都进入 `skipProcessing`；
3. skip 分支仍写入当前 MessageId 的 Inbox；
4. skip 分支完成当前 JobRun 并 ACK；
5. skip 分支不发送 `SubmitToProviderCommand`；
6. Provider Key 和 ProviderReference 仍是第二层保护。

所以不需要修改业务代码。

## 当前限制

1. Exp5 验证顺序到达的新 MessageId；并发不同 MessageId 属于更强竞态，需要依靠
   Job Lease、乐观并发和 Provider Key，相关并发实验单独验证。
2. 本实验只验证 Succeeded 终态；Failed/Completed 的终态短路由现有状态机测试覆盖。
3. LocalStack 是 E2 证据，真实 AWS SQS E4 Smoke 仍未执行。

## 验证命令

```powershell
dotnet build Reliant.slnx -c Release --no-restore

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build `
  --filter "FullyQualifiedName~Integration.Phase3.Exp5" `
  --logger "console;verbosity=detailed"

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build
```

## 我的学习结论

幂等不是只有 Inbox 一层。新 MessageId 会合法地产生新 Inbox，因此系统还必须在业务
入口判断当前事实：

```text
new message + terminal contribution
-> record that this message was handled
-> do not repeat the business effect
```

这也是为什么最终允许两条 Inbox，却不允许两条 ProcessingAttempt 或两个 Provider
Operation。
