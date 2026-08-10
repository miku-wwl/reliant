# Phase 3 / Experiment 11 — Circuit Open No ACK

## 一页结论

**PASS（E2：真实 WorkerHost + LocalStack SQS + PostgreSQL）**

Circuit Open 期间，同一逻辑消息经过 3 秒 Visibility Timeout 后真实重新投递。SQS 原生
`ApproximateReceiveCount` 达到 2，两个物理 JobAttempt 都以 Deferred 结束；但系统没有调用
Provider、没有创建业务 ProcessingAttempt、没有消耗 Contribution Retry Budget、没有写
Inbox，也没有 Delete/ACK 消息。

```text
OPEN
Receive / ApproxReceive = 2 / 2
JobAttempt = 2 / all Deferred
ProviderOperation = 0
ProcessingAttempt / RetryCount / Inbox / Delete = 0 / 0 / 0 / 0
```

显式关闭 Circuit 后，下一次 redelivery 只创建一个 Provider Operation，Contribution 收敛为
Succeeded，Inbox 与 ACK 各一次，队列最终清空。

## 实验信息

- 日期：2026-08-11
- 测试目录：`tests/Reliant.Tests/Integration/Phase3/Exp11/`
- 测试类：`CircuitOpenNoAckE2ETests`
- 数据库：PostgreSQL 17 Testcontainer
- 队列：LocalStack SQS，Visibility Timeout=3 秒
- Worker：真实 Outbox Publisher + Processing Handler
- Provider：Sandbox Success mode，Circuit 人工打开/关闭
- MaxReceiveCount：10，避免实验窗口内过早 DLQ
- Heartbeat：1 秒，小于 Visibility 3 秒和 Lease 30 秒
- Exp11：1/1 passed

## 假设

```text
Circuit Open 表示 Provider 当前不可调用
消息必须保留给未来恢复，不应 ACK
没有 Provider call 就不应产生业务 Attempt 或消耗 Retry Budget
Circuit 恢复后，同一逻辑消息应继续完成
```

## 实验时间线

```text
open Circuit
CreateContribution transaction
  -> Contribution + Outbox + JobRun
Outbox -> LocalStack SQS

Receive #1 / Approx=1
  -> JobAttempt #1 Deferred
  -> no Provider / no Inbox / no ACK

Visibility expires
Receive #2 / Approx=2
  -> JobAttempt #2 Deferred
  -> no Provider / no Inbox / no ACK

close Circuit
Visibility expires
Receive #3
  -> ProcessingAttempt #1 Succeeded
  -> ProviderReference + Inbox + Job success
  -> SQS Delete once
```

## 学生视角：中间过程

### 第一次 Review：旧 E2E 已有正确方向

仓库根目录的 `CircuitOpenE2ETests` 已使用 Raw AWS SDK Adapter 读取 SQS 原生
ApproximateReceiveCount，也断言了 Open 时不调用 Provider、不写 Inbox、不 ACK。这说明生产
业务语义已经存在，不应该为了 Exp11 再修改 Worker 或 Submit Handler。

我把测试升级为真实 `CreateContributionCommand`，使创建事务自动产生 Contribution、Outbox
和 JobRun，并补充以下证据：

- Open 阶段 JobRun / JobAttempt 状态；
- 业务 ProcessingAttempt 与物理 JobAttempt 的区别；
- ProviderReference、Lease、DeadLetter；
- 最终 Attempt、Inbox、ACK 都只有一次；
- 正确的 PostgreSQL dependency trait。

### 首次运行失败：发现测试 Adapter 的 MessageId 契约不一致

第一次增强后，日志已清楚显示 9 次 redelivery 和多次 Circuit Deferred，但测试查询不到原始
JobRun 的 attempts。检查后发现：

```text
production SqsQueueAdapter:
  MessageId = message attribute "MessageId"（Outbox Id）

test RawSqsQueueAdapter before fix:
  MessageId = AWS physical msg.MessageId
```

SQS 每次 Send 会生成 transport ID，它不是 Reliant 用于 Inbox/Job 的稳定逻辑 MessageId。
测试 helper 使用物理 ID 后，Worker 会走 rolling-deployment fallback，创建另一条 JobRun，导致
测试把正确行为查询到了错误业务 ID 上。

修复只发生在测试 helper：优先读取 `MessageAttributes["MessageId"]`，缺失时才 fallback 到
AWS physical ID，与生产 `SqsQueueAdapter` 完全一致。没有改生产代码。

### Open 阶段：物理重投不等于业务重试

稳定运行后，第二次 Deferred 已持久化时读取：

```text
Contribution = Processing
JobRun = Pending / AttemptCount=2
JobAttempt #1 = Deferred / Provider circuit is open
JobAttempt #2 = Deferred / Provider circuit is open
Active Lease = 0

ProcessingAttempt = 0
Contribution.RetryCount = 0
NextRetryAt = null
ProviderReference = 0
Inbox = 0
SQS Delete = 0
ProviderOperation = 0
```

这里最重要的学习点是区分两种 Attempt：

```text
JobAttempt      = 消息被 Worker 领取过，可记录 Deferred
ProcessingAttempt = 真正开始一次 Provider 业务调用
```

Circuit Open 时允许前者增长以便运维审计，但后者和业务 Retry Budget 必须保持 0。

### Circuit 关闭后的恢复

调用 `RecordSuccess()` 显式关闭 Circuit。下一次相同逻辑 MessageId redelivery 执行正常提交：

```text
ProviderOperation = 1
ProcessingAttempt = 1 / Succeeded
ProviderReference = 1
Contribution = Succeeded / RetryCount=0
Processing Inbox = 1
JobRun = Succeeded
JobAttempt Succeeded = 1
SQS Delete = 1
DeadLetter = 0
Queue = empty
```

Open 阶段没有提前写 Inbox，因此恢复投递不会被错误 dedup；Open 阶段也没有 Delete，因此消息
没有静默丢失。

## PASS 条件逐项判定

| 条件 | 实际值 | 判定 |
| --- | --- | --- |
| Open ProviderOperationCount=0 | 0 | PASS |
| Open AttemptCount=0 | ProcessingAttempt=0 | PASS |
| Open RetryCount=0 | 0 | PASS |
| Open InboxCount=0 | 0 | PASS |
| Open DeleteCount=0 | 0 | PASS |
| Open ReceiveCount≥2 | Receive=2 / Approx=2 | PASS |
| 恢复 ProviderOperationCount=1 | 1 | PASS |
| Contribution=Succeeded | Succeeded | PASS |
| 消息最终 ACK | Delete=1，Queue empty | PASS |

## 最终数据

| 数据 | Open（两次 Deferred 后） | 恢复后 |
| --- | --- | --- |
| Circuit | Open | Closed |
| Contribution | Processing / RetryCount=0 | Succeeded / RetryCount=0 |
| Provider Operation | 0 | 1 |
| ProcessingAttempt | 0 | 1 / Succeeded |
| JobAttempt | 2 / Deferred | Deferred 保留 + 1 Succeeded |
| ProviderReference | 0 | 1 |
| Inbox | 0 | 1 |
| SQS Delete | 0 | 1 |
| Active Lease | 0 | 0 |
| DeadLetter | 0 | 0 |

## 业务代码与测试聚合 Review

```text
生产代码修改：0
数据库 Migration：0
测试 helper 修改：RawSqsQueueAdapter 逻辑 MessageId 解析与生产对齐
删除：根目录 CircuitOpenE2ETests
新增：Phase3/Exp11/CircuitOpenNoAckE2ETests
测试总数净变化：0
```

生产代码无需修改。`SubmitToProviderHandler` 在 Circuit Open 时、创建 ProcessingAttempt 之前就
返回 Deferred；`ProcessingHandlerService` 把 JobAttempt 标记 Deferred、JobRun 放回 Pending，
然后不写 Inbox、不 Delete SQS。现有实现已经符合实验假设。

本次唯一公共 helper 修改是测试正确性修复，不进入生产程序集，也不改变业务行为。

## 当前限制

1. 实验通过进程内控制面打开/关闭 Circuit；生产环境应由失败率、窗口和 HalfOpen probe 自动
   驱动，并暴露 circuit state metric。
2. Open 状态会按 Visibility 周期产生 Deferred JobAttempt；长时间 outage 需要 retention 和
   容量策略，防止审计表无界增长。
3. SQS redrive policy 必须给 Circuit outage 留出足够 receive budget；本实验设置 10，只运行
   两次 Open redelivery。生产值应结合最大 outage 和 Visibility 计算。
4. 当前 Raw test adapter 使用已弃用的 AWS SDK `AttributeNames` API 来请求系统属性，编译会
   给警告；后续 SDK 清理可迁移到 `MessageSystemAttributeNames`，不影响本实验语义。

## 验证命令

```powershell
dotnet build tests/Reliant.Tests/Reliant.Tests.csproj -c Release --no-restore

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build `
  --filter "FullyQualifiedName~Integration.Phase3.Exp11" `
  --logger "console;verbosity=detailed"

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build
```

## 我的学习结论

Circuit Breaker 的 Open 语义不是“快速失败并丢弃工作”，而是“暂停外部调用，把可恢复工作留
在消息系统里”。

```text
transport delivery may repeat
transport JobAttempt may be Deferred
business retry budget stays untouched
business Provider effect starts only after recovery
```

把 transport attempt 与 business attempt 分开审计，才能既解释队列为何反复投递，又证明
Provider 没有被调用、业务重试额度没有被偷偷消耗。
