# Phase 2 / Experiment 2 — Duplicate Publish

## 一页结论

**PASS（E2：真实 PostgreSQL 17 + LocalStack 3）**

我把同一条 Outbox 消息依次发布了两次。LocalStack SQS 确实接收并交给
Consumer 两次，Consumer 入口日志也出现两次；但两次消息携带相同的稳定逻辑
`MessageId`（`OutboxMessage.Id`），第二次在 Inbox 检查处被去重并 ACK。

最终只有一条业务记录、一个 Inbox、一个 ProcessingAttempt、一个
ProviderReference 和一次 Provider 业务操作，没有重复状态转换或 Dead Letter。

```text
允许消息重复：是
允许业务副作用重复：否
```

## 实验信息

- 日期：2026-08-03
- 测试：
  `DuplicatePublishE2ETests.SameOutboxPublishedTwice_ShouldBeReceivedTwice_ButProduceOneBusinessEffect`
- 测试目录：`tests/Reliant.Tests/Integration/Phase2/Exp2/`
- 环境：Windows、.NET SDK 10.0.300、Docker Desktop 29.4.3
- 容器：`postgres:17`、`localstack/localstack:3`
- 专项测试结果：1/1 通过
- 相关回归测试：11/11 通过（LocalStack SQS、Duplicate Message、Lab 2）

## 我的假设

Outbox Publisher 可能已经把消息发到 SQS，却在把 Outbox 标记为 `Sent` 前
崩溃。重启后它会再次扫描到同一条 `Pending` Outbox，于是同一个逻辑消息可能
发布两次。

所以我不能要求 Queue 只出现一次消息。我真正要验证的是：

```text
同一个 OutboxMessage.Id 发布两次
→ Queue 可以收到两次
→ Consumer 可以进入两次
→ 业务副作用只能发生一次
```

## 实验前发现的问题

代码原本在发送时已经把 `OutboxMessage.Id` 放入 SQS message attribute：

```text
MessageId = OutboxMessage.Id
```

但接收端的 `SqsMessage.MessageId` 返回的是 SQS 自动生成的物理 ID。SQS 每次
`SendMessage` 都会生成新的物理 ID，所以同一 Outbox 发布两次时，Inbox 会把
它们误认为两个不同的逻辑消息。

这与 `ADR-0011` 的定义不一致：

```text
InboxMessage.MessageId 来自 OutboxMessage.Id
```

我修正了 `SqsQueueAdapter`：

1. 优先读取 Reliant 发送时写入的 `MessageId` attribute；
2. 如果外部消息没有该 attribute，再回退到 SQS 物理 ID。

修正后，同一 Outbox 无论发布多少次，Consumer 看到的逻辑 MessageId 都稳定。

## 实验方法

为了避免后台 Outbox Publisher 与实验的两次显式 Publish 发生竞争，我在测试
fixture 中先准备一条状态为 `Sent` 的 Outbox 记录，只把它的稳定身份、类型和
Payload 作为本实验输入。实验随后直接通过真实 `QueueMessagePublisher` 控制
两次发送。

完整路径仍然是真实的：

```text
同一个 OutboxMessage.Id
→ QueueMessagePublisher.PublishAsync #1
→ LocalStack SQS
→ ProcessingHandler
→ Inbox + Business + Provider
→ ACK
→ QueueMessagePublisher.PublishAsync #2
→ LocalStack SQS
→ ProcessingHandler 再次进入
→ Inbox 命中同一 MessageId
→ 跳过业务处理
→ ACK
```

## 中间过程与实际观察

本次 Outbox ID：

```text
aab4367b-0e9d-4294-afe3-a66ef57a0a3f
```

### 第一次 Publish

```text
QueueSend         = 1
QueueReceive      = 1
QueueDelete       = 1
InboxRows         = 1
BusinessState     = Succeeded
ProviderOperations = 1
```

第一次消息正常走完业务路径，Contribution 进入 `Succeeded`，Inbox 保存了
Outbox ID，Provider 产生一次真实业务操作。

### 第二次 Publish

第二次使用完全相同的：

- Outbox ID
- MessageType
- Payload
- CorrelationId

实际结果：

| 检查项 | 实际值 |
|---|---:|
| Queue Send | 2 |
| Queue Receive | 2 |
| Queue Delete / ACK | 2 |
| Consumer 入口 | 至少 2 次 |
| 两次 Consumer MessageId | 相同 Outbox ID |
| Inbox 行数 | 1 |
| Contribution 行数 | 1 |
| Contribution 状态 | Succeeded |
| ProcessingAttempt | 1 |
| ProviderReference | 1 |
| Provider Operation | 1 |
| 第二次新增状态转换 | 0 |
| Dead Letter | 0 |

第二条 SQS 消息不是被 Queue 隐藏了；它确实到达了 Consumer。Consumer 使用
稳定逻辑 MessageId 查询 Inbox，发现已经处理过，于是跳过业务处理并 ACK。

## 原始关键输出

```text
AFTER PUBLISH #1 | OutboxId=aab4367b-0e9d-4294-afe3-a66ef57a0a3f | QueueSend=1 | QueueReceive=1 | QueueDelete=1 | InboxRows=1 | BusinessState=Succeeded | ProviderOperations=1

AFTER PUBLISH #2 | OutboxId=aab4367b-0e9d-4294-afe3-a66ef57a0a3f | QueueSend=2 | QueueReceive=2 | QueueDelete=2 | ConsumerEntries>=2 | InboxRows=1 | ProcessingAttempts=1 | ProviderReferences=1 | ProviderOperations=1 | DuplicateStateTransitions=0 | DeadLetters=0

RESULT | PASS | StartedAt=2026-08-03T01:00:43.6342551Z | CompletedAt=2026-08-03T01:01:01.2862214Z
```

## PASS 条件核对

- [x] 强制同一 Outbox Message 发布两次
- [x] Queue 实际收到两条消息
- [x] 两条消息携带相同稳定逻辑 MessageId
- [x] Consumer 实际进入两次
- [x] 第二次触发 Inbox Dedup
- [x] 最终只有一条业务数据
- [x] Inbox 只有一条记录
- [x] Provider 业务副作用只有一次
- [x] 没有重复状态转换
- [x] 没有 Dead Letter

## 我的最终理解

`At-least-once` 的重点不是消灭重复消息，而是接受消息可能重复，再把业务处理
做成幂等。

这次实验中有两个不同的 ID：

```text
SQS 物理 MessageId：
每次 SendMessage 都可能不同，只代表 Queue 中的一次物理发送。

Outbox 逻辑 MessageId：
来自 OutboxMessage.Id，同一个业务事件重发时必须保持相同。
```

Inbox 必须使用后者。如果误用 SQS 物理 ID，Duplicate Publish 就绕过 Inbox。
修正后，防重复链路变成：

```text
稳定 Outbox MessageId
→ Inbox 唯一索引
→ Consumer 重复检查
→ Provider 稳定 Idempotency Key
→ Exactly-once Business Effect
```

这里实现的是业务效果只发生一次，不是消息只投递一次。

## 第三方复验命令

在仓库根目录执行：

```powershell
dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  --artifacts-path artifacts/phase2-exp2-run `
  --filter "FullyQualifiedName~DuplicatePublishE2ETests" `
  --logger "console;verbosity=detailed"
```

预期退出码为 `0`，测试总数和通过数均为 `1`。测试自动启动并销毁 PostgreSQL
和 LocalStack Testcontainer。

相关回归测试：

```powershell
dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  --artifacts-path artifacts/phase2-exp2-regression `
  --filter "FullyQualifiedName~LocalStackSqsTests|FullyQualifiedName~DuplicateMessageE2ETests|FullyQualifiedName~DuplicatePublishE2ETests"
```

本次实际结果为 11/11 通过。

## Known Limitations

1. 两次 Publish 在本实验中是依次执行，主要验证稳定 MessageId 和 Inbox 去重；
   两个 Publisher 并发发布同一 Outbox 的竞态应作为单独并发实验。
2. 当前 Inbox 的“先 SELECT、后 INSERT”并发窗口仍依赖数据库唯一索引兜底；
   并发 Duplicate Delivery 属于 Experiment 3。
3. 构建仍报告仓库现有的 NuGet 高危漏洞警告和 SQS SDK 过时 API 警告，需在
   独立依赖升级任务中处理。
