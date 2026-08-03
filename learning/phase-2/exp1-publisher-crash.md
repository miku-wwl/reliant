# Phase 2 / Experiment 1 — DB Commit 后 Publisher Crash

## 一页结论

**PASS（E2：真实 PostgreSQL 17 + LocalStack 3，进程内故障注入）**

业务事务提交后，我让 Publisher 停在真实 SQS `SendMessage` 之前并终止第一个
Worker Host。终止后，业务数据仍然存在，Outbox 仍为 `Pending`，SQS 中没有
消息。重启 Publisher 后，同一条 Outbox 成功发布并被 Worker 处理，最终业务、
Inbox、ProcessingAttempt 和 ProviderReference 都只有一份，没有静默丢失或
重复业务结果。

## 实验信息

- 日期：2026-08-03
- 自动化测试：
  `PublisherCrashE2ETests.DbCommitted_PublisherStoppedBeforeSend_ShouldRecoverWithoutDuplicateBusinessResult`
- 测试代码：`tests/Reliant.Tests/Integration/Phase2/Exp1/`
- 环境：Windows、.NET SDK 10.0.300、Docker Desktop 29.4.3
- 容器：`postgres:17`、`localstack/localstack:3`
- 测试结果：1/1 通过，28.5068 秒

## 我的假设

我想验证的是：业务数据和 Outbox 一旦一起提交到数据库，Publisher 即使在发
消息前停止，消息也不会凭空消失。Publisher 重启后应该再次找到这条
`Pending` Outbox，完成发送，而且最终不能产生重复业务结果。

## 实验前的代码确认

### 业务数据与 Outbox 的提交点

```text
CreateContributionCommand.cs:112  Contribution Add
CreateContributionCommand.cs:133  OutboxMessage Add
CreateContributionCommand.cs:135  SaveChangesAsync
```

`Contribution` 和 `OutboxMessage` 使用同一个 EF Core `DbContext`，最后只执行
一次 `SaveChangesAsync`。因此它们不是两次互相独立的数据库提交。

### Publisher 的执行顺序

```text
OutboxPublisherService.cs:35  读取 Pending
OutboxPublisherService.cs:48  PublishAsync
OutboxPublisherService.cs:49  MarkAsSentAsync
```

只有 Publish 成功返回后，Outbox 才会变成 `Sent`。如果在 Publish 前停止，
Outbox 应继续保持 `Pending`，供重启后的 Publisher 再次扫描。

## 我怎样制造故障

我没有在生产代码中加入测试开关，而是在实验目录中使用
`PauseBeforeSendQueueAdapter` 包装真实 SQS Adapter：

```text
DB commit 完成
→ Publisher 读取 Pending Outbox
→ 进入 IQueueAdapter.SendAsync
→ 在真实 SQS SendMessage 前暂停
→ 停止第一个 Worker Host
```

测试先等待暂停门闩发出信号，确认 Publisher 已经到达 Publish 前的精确窗口，
再停止 Host。取消令牌结束暂停中的调用，所以第一次运行没有真正发送 SQS
消息。

这比“创建业务数据后马上停止”更可靠，因为我能证明故障发生在数据库提交以后、
真实 Broker Send 以前。

## 中间过程与实际观察

### 第一步：创建业务数据

我通过真实 `CreateContributionCommand` 创建了一笔金额为 `100 NZD`、外部引用
为 `PHASE2-EXP1-001` 的 Contribution。

### 第二步：Publisher 到达 Send 前并停止

本次运行的标识：

```text
ContributionId = b8ed51d3-fafd-4c1b-bc8f-3a7e203d72ef
OutboxId        = 2d5e81db-fbab-49fd-860e-ab0ed5716714
```

停止后的快照：

| 检查项 | 实际值 |
|---|---:|
| Contribution 行数 | 1 |
| Contribution 状态 | Created |
| Outbox 行数 | 1 |
| Outbox 状态 | Pending |
| Outbox SentAt | null |
| Outbox SendCount | 0 |
| SQS 消息 | 无 |

我的判断：业务数据与发送意图都已持久化，而且 Publish 确实还没有发生。

### 第三步：重启 Publisher 和 Processing Worker

第二次启动时，我移除暂停 Adapter，恢复真实 LocalStack SQS Adapter，并统计
Send、Receive 和 Delete 操作。

恢复完成后的快照：

| 检查项 | 实际值 |
|---|---:|
| Contribution 行数 | 1 |
| Contribution 状态 | Succeeded |
| Outbox 行数 | 1 |
| Outbox 状态 | Sent |
| Outbox SentAt | 非 null |
| Processing Inbox | 1 |
| Processing Attempt | 1 |
| Successful Attempt | 1 |
| Provider Reference | 1 |
| SQS Send | 1 |
| SQS Receive | 1 |
| SQS Delete / ACK | 1 |

我的判断：重启后的 Publisher 使用数据库中的同一条 `Pending` 记录恢复发送；
业务最终成功，而且没有创建第二条业务结果。

## 原始关键输出

```text
BEFORE RESTART | ContributionId=b8ed51d3-fafd-4c1b-bc8f-3a7e203d72ef | OutboxId=2d5e81db-fbab-49fd-860e-ab0ed5716714 | BusinessRows=1 | OutboxRows=1 | State=Created | OutboxStatus=Pending | SentAt=null | QueueMessage=none

AFTER RESTART | ContributionId=b8ed51d3-fafd-4c1b-bc8f-3a7e203d72ef | OutboxId=2d5e81db-fbab-49fd-860e-ab0ed5716714 | BusinessRows=1 | BusinessState=Succeeded | OutboxRows=1 | OutboxStatus=Sent | InboxRows=1 | ProcessingAttempts=1 | ProviderReferences=1 | QueueSend=1 | QueueReceive=1 | QueueDelete=1

RESULT | PASS | StartedAt=2026-08-03T00:42:37.4366696Z | PublisherStoppedAt=2026-08-03T00:42:57.1463079Z | CompletedAt=2026-08-03T00:43:02.9654208Z
```

## PASS 条件核对

- [x] 创建一笔业务数据
- [x] 确认业务数据与 Outbox 使用同一个提交点
- [x] 在真实 Publish 前终止 Publisher Host
- [x] 终止后业务数据仍然存在
- [x] 终止后 Outbox 仍为 Pending
- [x] 终止前 SQS 中没有消息
- [x] 重启后消息成功发布
- [x] 最终业务结果没有重复
- [x] 无静默丢失

## 我的最终理解

Outbox 并不保证 Publisher 永远不会失败。它解决的是：先把“需要发送消息”这件
事变成数据库里的耐久事实。Publisher 可以停止，但 `Pending` 记录不会随进程
一起消失，因此新 Publisher 能继续完成发送。

本次实验实际验证了：

```text
DB 中有业务结果 + Pending Outbox
→ Publisher 停止
→ Pending Outbox 保留
→ Publisher 重启
→ 消息发布并处理
→ 最终只有一个业务结果
```

## 第三方复验命令

在仓库根目录执行：

```powershell
dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  --artifacts-path artifacts/publisher-crash-run `
  --filter "FullyQualifiedName~PublisherCrashE2ETests" `
  --logger "console;verbosity=detailed"
```

预期退出码为 `0`，测试总数与通过数均为 `1`。测试会自动启动并销毁 PostgreSQL
与 LocalStack Testcontainer，不需要手动执行 `docker compose up`。

## 实验中遇到的问题

第一次运行时命令等待时间设置过短，遗留的 `testhost` 暂时锁住默认 `bin`
目录。这个问题与业务实验无关。改用独立的 `--artifacts-path` 后，专项测试
正常通过。

构建还报告了仓库现有的 NuGet 高危漏洞警告和 SQS SDK 过时 API 警告。它们没
有改变本实验结果，但应该在依赖升级任务中处理。

## Known Limitations

1. 本实验是 E2：PostgreSQL 与 SQS 是真实容器，但 Publisher 停止由测试进程内
   的暂停门闩和 Host cancellation 控制，不等同于 `kill -9`、`docker kill`
   或 OOM Kill。
2. 当前 `OutboxMessage.SendCount` 没有在 Publisher 发送尝试时递增，数据库中
   的发送重试审计不完整。本实验使用 Queue Adapter 计数器证明实际发送次数。
3. 若要升级为 E3，应把 Publisher 放进独立进程或容器，在门闩信号出现后执行
   操作系统级强杀，再由另一个进程恢复。
