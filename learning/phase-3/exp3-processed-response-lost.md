# Phase 3 / Experiment 3 — Processed but Response Lost

## 一页结论

**PASS（E2：真实 PostgreSQL + LocalStack SQS + WorkerHost）**

Sandbox Provider 已创建并完成 Operation，但提交响应被模拟丢失。Worker 没有把 Timeout
当作失败，而是保存一条 `Unknown` Attempt 并进入 `ReconciliationPending`。

Reconciliation 使用该 Attempt 的稳定 Provider Key 查询到原 Operation 为
`Succeeded`，随后补写唯一 ProviderReference，并将 Contribution 收敛到
`Succeeded`。再次投递一个新的业务消息时，Worker 识别终态并直接幂等 ACK，没有
第二次 Provider 调用或第二条 Attempt。

```text
RESPONSE LOST | Operation=1 | Attempt=Unknown | Reference=0
RECONCILED    | QueryByKey=Succeeded | State=Succeeded | Reference=1
DUPLICATE     | terminal ACK | Attempts=1 | Operation=1
```

## 实验信息

- 日期：2026-08-11
- 测试目录：`tests/Reliant.Tests/Integration/Phase3/Exp3/`
- 测试：`ProcessedResponseLost_ShouldReconcileSucceeded_AndSuppressDuplicateBusinessMessage`
- Provider Mode：`ProcessedButResponseLost`
- 数据库：PostgreSQL 17 Testcontainer
- Queue：LocalStack 3 SQS
- Worker：真实 WorkerHost
- Exp3：1/1 passed

## 假设

```text
Provider 已处理 + Response Lost
不代表 Provider 未处理
系统必须先按稳定 Key 找回原结果
不能直接创建第二个 Provider Effect
```

## 实验设计

测试先关闭自动 Reconciliation BackgroundService，以便冻结并观察 Provider 已处理、
本地尚未知道结果的真实边界。Outbox Publisher、Processing Worker、Job/Lease、Inbox
和 SQS ACK 都保持运行。

```text
CreateContribution
  -> Outbox -> SQS -> Worker
  -> Attempt 1 Pending committed
  -> Provider creates Operation
  -> Provider throws simulated response-lost timeout
  -> Attempt 1 Unknown
  -> ProviderUnknown -> ReconciliationPending
  -> 手动执行真实 ReconcileContributionCommand
  -> QueryStatusByIdempotencyKey == Succeeded
  -> ProviderReference backfilled
  -> Contribution Succeeded
  -> 新 Outbox + JobRun 再次投递同一 Contribution
  -> terminal-state idempotent ACK
```

最后一次重复投递使用新的 MessageId，因此它验证的是“新的逻辑消息重复要求处理同一
Contribution”，不是 Inbox 对同一物理 MessageId 的去重。后者属于 Exp4。

## 学生视角：中间过程

### 第一次 Review：先区分 Provider Effect 和本地 Evidence

`ProcessedButResponseLost` 的关键并不是制造普通 Timeout。Sandbox Provider 的实际
顺序是：

```text
按 Provider Key 原子 GetOrAdd Operation
写入 reference index
然后抛 TaskCanceledException
```

所以第一次调用结束时，Provider Operation 确实存在；只是本地没有收到 Reference。
如果测试只检查最终 `Succeeded`，就无法证明中间的不确定性处理正确。

### 第一个观察点：Provider 已成功，本地仍 Unknown

在第一次消息已 Commit 并 ACK、自动 Reconciliation 尚未运行时读取：

```text
Contribution.State = ReconciliationPending
AttemptNumber = 1
Attempt.Status = Unknown
Attempt.ErrorCategory = Timeout
Attempt.ProviderReference = null
ProviderOperationCount = 1
ProviderReferenceCount = 0
ReconciliationRecordCount = 0
```

状态审计最后两步为：

```text
Processing -> ProviderUnknown
ProviderUnknown -> ReconciliationPending
```

这里最重要的是 `Operation=1` 与 `Reference=0` 可以同时成立：外部事实已经发生，
本地证据尚未补齐。

### 第二个观察点：按 Key 找回原结果

测试执行真实 `ReconcileContributionCommand`。因为本地没有 ProviderReference，
Handler 使用最新 Attempt 的 `ProviderIdempotencyKey` 查询。

```text
ProviderState = Succeeded
Difference = StateMismatch
Resolution = AutoFixed
ResolvedAt != null
ResolvedBy = ReconciliationHandler
Contribution.State = Succeeded
ProviderReferenceCount = 1
ProviderOperationCount = 1
```

ProviderReference 是查询后补写的恢复证据，不是伪造的新 Operation。

### 第三个观察点：再次投递业务消息

我在同一个数据库事务中新增一条 `ContributionCreated` Outbox 与对应 JobRun，消息使用
新的 MessageId，但指向已经 Succeeded 的 Contribution。

Worker 收到后走终态保护分支：

```text
Contribution in Succeeded
-> create Inbox for new MessageId
-> JobRun Succeeded
-> ACK
-> do not call Provider
```

最终结果：

```text
ProcessingAttemptCount = 1
UnknownAttemptCount = 1
ProviderOperationCount = 1
ProviderReferenceCount = 1
Succeeded transitions = 1
Queue Send/Receive/Delete = 2/2/2
Queue = empty
```

## PASS 条件逐项判定

| PASS 条件 | 实际证据 | 判定 |
| --- | --- | --- |
| ProviderOperationCount == 1 | 响应丢失后、Reconcile 后、重复投递后始终为 1 | PASS |
| ProviderReferenceCount == 1 | Reconciliation 补写 1 条，重复投递后仍为 1 | PASS |
| Contribution.State == Succeeded | Query by Key 后收敛 Succeeded | PASS |
| UnknownAttemptCount == 1 | 仅第一次 Attempt，状态 Unknown | PASS |
| 无第二个 Provider Effect | 新 MessageId 被终态保护 ACK，Operation 未增加 | PASS |

## 最终数据

| 数据 | 最终值 |
| --- | --- |
| Contribution | Succeeded |
| ProcessingAttempt | 1 / Unknown |
| ProviderOperation | 1 / Succeeded |
| ProviderReference | 1 / reconciliation backfill |
| ReconciliationRecord | 1 / Succeeded / AutoFixed |
| StateTransition | 6，Succeeded 转换仅 1 次 |
| Duplicate JobRun | Succeeded |
| Duplicate Inbox | Processed |
| DeadLetter | 0 |
| Queue Send/Receive/Delete | 2 / 2 / 2 |
| Queue | empty |

## 业务代码必要性 Review

```text
生产代码修改：0
数据库 Migration：0
既有测试修改：0
新增：1个 Exp3 聚合 E2E + 1份聚合报告
```

现有业务代码已经具备所需语义：

1. Sandbox Provider 在 Response Lost 前先按稳定 Key 创建 Operation；
2. `SubmitToProviderHandler` 将异常结果保存为 Unknown Attempt；
3. Worker 用 `ProviderUnknown -> ReconciliationPending` 表示未知外部结果；
4. Reconciliation 在无 Reference 时按最新 Attempt Key 查询；
5. Query Succeeded 会补写缺失 ProviderReference；
6. Contribution 终态分支不会再次调用 Provider；
7. Outbox、JobRun、Inbox 和 ACK 顺序已经支持重复消息安全结束。

仓库原有 `FinalE2ETests` 已覆盖相近最终路径，但它同时混入 Duplicate Callback。Exp3
只新增边界清晰的聚合证据，没有为了“做新实验”重复修改上述生产逻辑。

## 当前限制

1. Sandbox Provider 是进程内实现，真实 Provider 必须支持可靠的 Query-by-Key 合同。
2. Exp3 的重复消息使用新 MessageId；同一 SQS MessageId 的 ACK 前崩溃与 Inbox 去重
   由 Exp4 验证。
3. Callback 先于 Submit Response 的竞态不在本实验范围内，由 Exp9 验证。
4. Provider Operation 是 Sandbox 内存证据；真实 E4 需要 Provider 审计记录或 API 证据。

## 验证命令

```powershell
dotnet build Reliant.slnx -c Release --no-restore

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build `
  --filter "FullyQualifiedName~Integration.Phase3.Exp3" `
  --logger "console;verbosity=detailed"

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build
```

## 我的学习结论

Response Lost 场景下，本地数据库和 Provider 对事实的认知暂时不一致：

```text
Provider：Succeeded + Reference exists
Reliant：Unknown + Reference missing
```

解决方式不是再次 Submit，而是使用第一次调用前已经持久化的稳定 Key查询原事实，
再把证据补回本地。此时“一个 Unknown Attempt + 一个 Succeeded Provider Operation”
是正确审计结果，并不要求把原 Attempt 改写成 Succeeded。
