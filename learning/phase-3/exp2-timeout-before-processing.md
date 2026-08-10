# Phase 3 / Experiment 2 — Timeout Before Processing

## 一页结论

**PASS（E2：真实 PostgreSQL + LocalStack SQS + WorkerHost）**

Provider 在处理业务前 Timeout 时，第一次调用没有创建 Provider Operation。Worker
将 Attempt 记为 `Unknown` 并停在 `ReconciliationPending`，没有立即生成 Retry，
也没有消耗普通 Retry 次数。

只有 Reconciliation 按第一次 Attempt 的稳定 Provider Key 查询并取得 `NotFound`
证据后，Contribution 才进入 `RetryPending`。Scheduler 到期后发出一条 Retry
消息；Provider 恢复后第二次 Attempt 复用同一 Key，最终只产生一个 Provider Effect。

```text
UNKNOWN  | Attempt=1/Unknown | ProviderOperation=0 | RetryOutbox=0
NOTFOUND | Resolution=SafeRetry | State=RetryPending
FINAL    | Attempt=2/Succeeded | ProviderKeys=1 | ProviderOperation=1
```

## 实验信息

- 日期：2026-08-11
- 测试目录：`tests/Reliant.Tests/Integration/Phase3/Exp2/`
- 测试：`TimeoutBeforeProcessing_ShouldWaitForNotFound_ThenRetryWithSameProviderKey`
- Provider 初始 Mode：`TimeoutBeforeProcessing`
- Provider 恢复 Mode：`Success`
- 数据库：PostgreSQL 17 Testcontainer
- Queue：LocalStack 3 SQS
- Worker：真实 WorkerHost
- Exp2：1/1 passed

## 假设

```text
Timeout 不等于 Provider 未处理
因此第一次 Timeout 不能直接 Retry
只有 Query by stable key == NotFound 才能证明安全重试
```

如果不经过 Query 就重试，真实 Provider 可能已经处理但响应丢失，从而产生重复外部
副作用。Exp2 专门验证“处理前 Timeout”场景中的证据门槛。

## 实验设计

测试用真实 `CreateContributionCommand` 创建业务数据、Outbox 和 JobRun，然后启动
Outbox Publisher 与 Processing Worker。Reconciliation BackgroundService 暂时关闭，
目的是在第一次调用完成后冻结并读取中间状态；Retry Scheduler 仍然是真实服务。

流程如下：

```text
CreateContribution
  -> Outbox -> SQS -> Worker
  -> Attempt 1 persisted
  -> Provider TimeoutBeforeProcessing
  -> Attempt 1 Unknown
  -> ProviderUnknown -> ReconciliationPending
  -> 手动执行一次真实 ReconcileContributionCommand
  -> Query by ProviderIdempotencyKey returns NotFound
  -> RetryPending + NextRetryAt
  -> Provider Mode switches to Success
  -> Scheduler -> Retry Outbox -> SQS -> Worker
  -> Attempt 2 uses the same ProviderIdempotencyKey
  -> Succeeded
```

手动触发 Reconciliation 不是替代生产 Handler，而是把一次真实
`ReconcileContributionCommand` 放在可观察边界上，避免 300ms 后台扫描让中间证据
一闪而过。

## 学生视角：中间过程

### 第一次 Review：旧测试证明了结果，但没有冻结关键边界

仓库已有 `SafeRetryE2ETests`，它覆盖：

```text
TimeoutBeforeProcessing -> NotFound -> Retry -> Succeeded
```

但旧测试开启了自动 Reconciliation，并直接等待 `RetryPending`。这能证明最终链路，
却不能单独展示在 Provider Query 之前是否已经生成 Retry。

所以我没有修改或复制业务实现，而是新增 Exp2 专项测试，并在第一次 SQS 消息 ACK
后、Reconciliation 前读取独立 DbContext。

### 第一个观察点：Unknown，但绝不盲目 Retry

```text
Contribution.State = ReconciliationPending
AttemptNumber = 1
Attempt.Status = Unknown
Attempt.ErrorCategory = Timeout
ProviderOperationCount = 0
ProviderReferenceCount = 0
RetryCount = 0
NextRetryAt = null
ReconciliationRecordCount = 0
ContributionRetryRequested Outbox = 0
```

状态审计最后两步是：

```text
Processing -> ProviderUnknown
ProviderUnknown -> ReconciliationPending
```

这说明系统将“Timeout”记录为不确定结果，并没有把它误判成失败或 NotFound。

### 第二个观察点：NotFound 是 Retry 的授权证据

测试执行一次真实 Reconciliation，Sandbox Provider 使用第一次 Attempt 的 Key 查询。
因为 `TimeoutBeforeProcessing` 根本没有创建 Operation，Query 返回 `NotFound`。

持久化证据为：

```text
ReconciliationRecord.ProviderState = NotFound
Difference = ProviderNotFound
Resolution = SafeRetry
ResolvedAt != null
ResolvedBy = ReconciliationHandler
Contribution.State = RetryPending
NextRetryAt != null
ProviderOperationCount = 0
```

因此 Retry 的先后关系是可证明的：`NotFound Evidence -> RetryPending`。

### 第三个观察点：Scheduler 后复用稳定 Key

Provider 在 Retry 到期前切换为 `Success`。真实 Maintenance Scheduler 生成
`ContributionRetryRequested` Outbox，经 LocalStack SQS 再次进入 Worker。

```text
Attempt 1 = Unknown
Attempt 2 = Succeeded
Distinct ProviderIdempotencyKey = 1
ProviderOperationCount = 1
ProviderReferenceCount = 1
RetryOutboxCount = 1
Queue Send/Receive/Delete = 2/2/2
Queue = empty
```

第二次调用虽然是新的 Attempt，但业务操作 Key 没变，因此 Retry 不会变成新的外部
业务操作。

## PASS 条件逐项判定

| PASS 条件 | 实际证据 | 判定 |
| --- | --- | --- |
| 第一次 ProviderOperationCount == 0 | Unknown 边界和 NotFound 后均为 0 | PASS |
| 第一次 Attempt == Unknown | Attempt 1 = Unknown / Timeout | PASS |
| NotFound 后才 Retry | Query 前 RetryOutbox=0、NextRetryAt=null；NotFound 后进入 RetryPending | PASS |
| 两次 Attempt 使用同一 Key | Attempts=2，Distinct Key=1 | PASS |
| 最终 ProviderOperationCount == 1 | 恢复后 Operation=1 | PASS |
| 最终 Succeeded | Contribution=Succeeded，Attempt 2=Succeeded | PASS |

## 最终数据

| 数据 | 最终值 |
| --- | --- |
| Contribution | Succeeded |
| ProcessingAttempt | 2：Unknown、Succeeded |
| Provider Idempotency Key | 1 个 distinct value |
| ReconciliationRecord | 1：NotFound / SafeRetry / Resolved |
| Retry Outbox | 1 / Sent |
| ProviderOperation | 1 |
| ProviderReference | 1 |
| DeadLetter | 0 |
| Queue Send/Receive/Delete | 2 / 2 / 2 |
| Queue | empty |

## 业务代码必要性 Review

```text
生产代码修改：0
数据库 Migration：0
既有测试修改：0
新增：1个 Exp2 聚合 E2E + 1份聚合报告
```

检查过的现有生产能力：

1. `SubmitToProviderHandler` 在调用前持久化 Pending Attempt；
2. Timeout 异常被记录为 `AttemptStatus.Unknown`，不是普通 Failed；
3. Worker 将 Unknown 推进到 `ReconciliationPending`，不会设置 `NextRetryAt`；
4. Reconciliation 使用最新 Attempt 的稳定 Key 查询 Provider；
5. 只有 `ProviderStatus.NotFound` 才写入 `SafeRetry` 和 `NextRetryAt`；
6. Retry Scheduler 到期后原子创建 Retry Outbox 与 JobRun；
7. Provider Key Factory 对同一 Contribution 返回稳定 Key。

这些语义已经满足 Exp2。为实验增加生产分支、测试专用延迟或第二套 Retry 逻辑都没有
必要，因此不修改业务代码。

## 当前限制

1. Sandbox Provider 是进程内实现；真实 Provider 的 Query-by-Key 合同仍需 E4 验证。
2. 本实验验证 NotFound 安全重试，不覆盖“Provider 已处理但响应丢失”；后者属于 Exp3。
3. Reconciliation 在测试中按边界手动触发一次；生产环境仍由后台扫描服务自动触发。
4. Retry Backoff 的统计分布和容量影响不在本实验范围内。

## 验证命令

```powershell
dotnet build Reliant.slnx -c Release --no-restore

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build `
  --filter "FullyQualifiedName~Integration.Phase3.Exp2" `
  --logger "console;verbosity=detailed"

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build
```

## 我的学习结论

安全重试的关键不是“Timeout 后等一会再试”，而是先取得可以解释的 Provider 证据：

```text
Unknown -> Query by stable key -> NotFound -> SafeRetry
```

时间延迟只能降低碰撞概率，`NotFound` 证据和稳定幂等 Key 才真正控制重复 Provider
Effect。Attempt 需要每次独立审计，而 Provider Key 必须跨 Attempt 保持不变。
