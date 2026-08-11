# Phase 2 / Experiment 6 — Poison Message

## 一页结论

**PASS（E2：PostgreSQL Testcontainer + LocalStack SQS + 真实 WorkerHost）**

我同时向 Processing Queue 投递了三条消息：

```text
1. 无法反序列化的 JSON
2. JSON 合法、但 Version=99 的不受支持消息
3. Version=1 的正常 Contribution 消息
```

两条坏消息都被 Worker 明确识别为永久的 Contract / Validation Failure。Worker
没有 ACK 它们，因此 SQS 按 `RedrivePolicy` 重投；达到本实验配置的 3 次接收后，
消息连同原始 Payload 和 Message Attributes 被原生 SQS redrive 到
`<main-queue>-dlq`。同一时刻，正常消息没有等待坏消息耗尽重试，在约 913ms 内
完成，最终只有一个业务结果。

数据库为两条坏消息各保存一条 `DeadLetterRecord`，包含 MessageId、MessageType、
原始 Payload、错误分类、错误详情、AttemptCount 和 DeadLetteredAt。最终结论：

```text
Poison Message 进入 DLQ：是
正常消息不受阻塞：是
错误可审计：是
```

## 实验信息

- 日期：2026-08-03
- 测试目录：`tests/Reliant.Tests/Integration/Phase2/Exp6/`
- 测试：
  `PoisonMessages_ShouldEnterNativeDlq_WithoutBlockingNormalMessage`
  `DeadLetterReplay_ShouldBeExplicitAtomicAndAudited`
- 环境：.NET 10、PostgreSQL 17 Testcontainer、LocalStack 3、真实 WorkerHost
- 主队列：每次测试生成唯一名称
- DLQ：`<主队列名称>-dlq`
- SQS Visibility Timeout：2 秒
- 本实验 `maxReceiveCount`：3
- Poison 类型：Malformed JSON、Unsupported Version
- 专项测试：2/2 通过（2026-08-11 补全受控 Replay）
- Exp5 交叉回归：3/3 通过
- 初次全量：154/154 通过；当前全量结果见 `docs/current-state.md`

## 我的假设

Poison Message 是“重复执行也不会自己恢复”的消息，例如 JSON 已损坏、必填字段
缺失或版本不受支持。它与网络超时等 transient failure 不一样：

```text
Transient failure：以后可能恢复，需要 Backoff / Jitter。
Poison message：相同代码再次读取仍会失败，需要有限次接收后隔离。
```

本实验要证明的不是“不允许重复投递”，而是：

```text
允许 Broker 做有限次 redelivery
→ 不让坏消息进入业务处理路径
→ 达到上限后隔离到 DLQ
→ 正常消息继续处理
→ 错误和原消息可供 Operator 审计
```

## 实验前的代码 Review

### 1. 主队列没有真正配置 DLQ

`SqsQueueAdapter.GetOrCreateQueueAsync` 原来只创建主队列，没有创建
`<queue>-dlq`，也没有给主队列设置 `RedrivePolicy`。因此文档虽然描述了 SQS
DLQ，运行时却没有原生 redrive 路径。

### 2. 反序列化发生在业务异常处理之外

原来的 Processing Handler 在进入业务处理前直接执行 JSON 反序列化。Malformed
JSON 只会到最外层日志，消息不 ACK，但也没有有限次数、DLQ 审计或明确终态。
它会不断重新出现。

### 3. Version 没有校验

`ContributionProcessingMessage.Version` 已经存在，但原代码没有拒绝未知版本。
`Version=99` 仍可能进入当前 Handler，产生“新消息被旧代码错误解释”的兼容风险。

### 4. DeadLetterRecord 没有接入 Poison 路径

数据库已有 `DeadLetterRecord` 模型，但 malformed / unsupported contract 没有写入
它。Operator 即使在 SQS DLQ 看到 Payload，也无法直接在业务数据库查询错误分类、
最后错误和接收次数。

## 第一次运行：预期 FAIL

我先写 Exp6 E2E，再对未修复代码运行。测试在第一道基础设施断言就失败：

```text
Processing queue has no SQS RedrivePolicy

0 passed, 1 failed
```

这次失败很重要，因为它证明实验不是先改代码再写一个永远会绿的测试；原系统的
确没有报告中要求的 DLQ 行为。

## 修复设计

### 1. 创建原生 SQS DLQ 和 RedrivePolicy

`SqsQueueAdapter` 首次取得队列时执行：

```text
创建或取得 <queue>-dlq
→ 读取 DLQ ARN
→ 创建或取得主队列
→ 设置 RedrivePolicy(deadLetterTargetArn, maxReceiveCount)
→ 缓存 QueueUrl
```

同一进程并发首次访问队列时使用一个 provisioning gate，避免多个 Worker
同时重复创建和修改队列。

本实验验证的是 LocalStack 对 SQS 原生 redrive 的实现，不是在测试中手工把消息
复制到另一个队列。

### 2. 在业务处理前增加 Contract Gate

Processing Handler 现在先验证：

```text
Payload 可以反序列化
MessageType 属于当前 Processing Handler
Version = 1
ContributionId 非空
OrganizationId 非空
Trigger 非空
CorrelationId 非空
```

验证失败后不会创建 Inbox、ProcessingAttempt、ProviderReference，也不会推进
Contribution 状态。

### 3. 有限接收，但不在 Worker 中提前 ACK

Worker 从 SQS 的 `ApproximateReceiveCount` 读取当前接收次数：

```text
receive < maxReceiveCount：
    记录明确错误
    不 ACK
    等待 Visibility Timeout 后 redelivery

receive >= maxReceiveCount：
    持久化 / 更新 DeadLetterRecord
    不 ACK
    由 SQS RedrivePolicy 移入 DLQ
```

这里不能在最后一次由 Worker `DeleteMessage`，否则消息会被删除，而不是进入原生
DLQ。

### 4. 数据库留下可查询审计

在最后一次接收时写入：

```text
OriginalMessageId
MessageType
原始 Payload
ErrorCategory = ValidationFailure
ErrorMessage
AttemptCount
DeadLetteredAt
Status = Pending
```

如果这条消息已经有对应 JobRun，则同时把 JobRun 置为 DeadLettered。Malformed
JSON 无法可信取得 OrganizationId，因此记录使用 `Guid.Empty`，并保留完整原始
Payload 供系统级 Operator 查询。

### 5. 不把正常消息排在 Poison 后面等待

测试先投递两条 Poison，再立即投递正常消息。真实 WorkerHost 的多个处理槽可以
继续接收其他消息，所以正常 Contribution 在 Poison 等待下一次 Visibility
Timeout 时已经完成。

## 实际实验过程

### 1. 队列拓扑

测试读取主队列 Attributes，确认：

```text
RedrivePolicy 存在
deadLetterTargetArn 指向 <queue>-dlq
maxReceiveCount = 3
```

### 2. 投递顺序

```text
Malformed JSON
→ Unsupported Version 99
→ Normal Version 1
```

Poison 被故意放在正常消息前面。如果 Handler 是单条坏消息无限循环或整个循环
被异常打断，正常消息就无法按时完成。

### 3. 正常消息结果

实际输出：

```text
NORMAL | MessageId=3447b9f8-5a75-478e-924a-891ff0d8fc86 | State=Succeeded | InboxRows=1 | ProcessingAttempts=1 | ProviderReferences=1 | CompletedMs=913
```

数据库结果：

| 检查项 | 实际值 |
| --- | ---: |
| Contribution | 1 |
| Contribution 状态 | Succeeded |
| JobRun 状态 | Succeeded |
| Inbox | 1 |
| ProcessingAttempt | 1 |
| ProviderReference | 1 |

### 4. Poison 重投和 DLQ

两条 Poison 的最终记录都是：

```text
AttemptCount = 3
ErrorCategory = ValidationFailure
Status = Pending
```

测试随后直接从原生 DLQ Receive，按 SQS Message Attribute 中的逻辑 MessageId
找到两条消息，并逐字比较 DLQ Body 与原始 Payload。

实际输出：

```text
POISON | MalformedId=12958d0e-663f-44d1-bdca-e409288c2780 | UnsupportedId=167417e3-0eb6-4c01-8b42-e9049c96a4ad | Attempts=3 | AuditRows=2

DLQ | NativeMessages=2 | MainQueueDrained=True | PayloadsPreserved=true

FINAL | BusinessResults=1 | PoisonBusinessEffects=0 | DeadLetterRecords=2 | ErrorCategory=ValidationFailure | ErrorsAuditable=true

RESULT | PASS | StartedAt=2026-08-03T06:38:02.9450683Z | CompletedAt=2026-08-03T06:38:27.8250915Z | DurationMs=24880
```

## 交叉回归中学到的问题

Exp6 首次接入原生 RedrivePolicy 后，Exp5 的压缩时间参数暴露了一个配置关系：

```text
Exp5 Visibility Timeout = 2 秒
Exp5 Lease = 10 秒
原默认 maxReceiveCount = 5
```

Worker B 在 Lease 仍属于 A 时会合法地多次收到消息并 defer。SQS 不知道这是
“合法 Lease 等待”，只会增加 `ApproximateReceiveCount`。如果 DLQ 阈值太低，
消息可能在 Lease 到期前进入 DLQ，Worker B 就无法接管。

因此 Exp5 的隔离测试队列显式使用 `maxReceiveCount=20`，覆盖它的最长合法 Lease
等待窗口；Exp6 保持 3，用于快速验证 Poison。这个过程让我确认：

```text
SQS maxReceiveCount 不是单纯的业务 Retry 次数。
它必须覆盖合法 redelivery、Lease 等待和预期 transient retry。
```

生产环境必须按 Worker 最大处理窗口、Visibility、Lease、Heartbeat 和 Retry
Budget 一起设定阈值，不能无条件复制 Lab 的 3 或默认的 5。

回归还发现 Docker 测试使用的精简 .NET runtime 不包含 GSS 原生库。测试
PostgreSQL 使用密码认证，不需要 GSS，因此 Exp4 / Exp5 的容器连接明确设置
`GssEncryptionMode=Disable`。这只修正测试运行环境，不改变生产数据库认证策略。

修正两个根因后，我保留 xUnit 默认并行策略重新执行全量测试；没有通过强制串行
隐藏竞态或资源问题。最终全量结果为 `154 passed, 0 failed, 0 skipped`。

## PASS 条件核对

- [x] 投递无法反序列化的消息
- [x] 投递违反版本约束的消息
- [x] Poison 之前投递后仍立即投递正常消息
- [x] 两条 Poison 的接收次数最终都是 3
- [x] Worker 每次拒绝都有可解释日志
- [x] 两条 Poison 都进入原生 SQS DLQ
- [x] DLQ 保存原始 Payload
- [x] DLQ 保存逻辑 MessageId Attribute
- [x] 正常 Contribution 最终 Succeeded
- [x] 正常消息只有一个 Inbox
- [x] 正常消息只有一个 ProcessingAttempt
- [x] 正常消息只有一个 ProviderReference
- [x] Poison 没有业务副作用
- [x] 主队列最终为空
- [x] 数据库有两条 DeadLetterRecord
- [x] ErrorCategory、ErrorMessage、AttemptCount、时间可审计
- [x] CorrelationId / CausationId 可审计
- [x] Replay 必须由 Operator 显式确认
- [x] Replay 原子 claim，重复操作被拒绝
- [x] Replay 生成新 MessageId，通过 Outbox 投递
- [x] Replay 与 AuditEvent 同事务提交
- [x] 原始 Payload 保留，修复后 Payload 可显式替换
- [x] 专项测试 2/2 通过
- [x] Exp5 回归 3/3 通过
- [x] 全量测试 154/154 通过

## 我的最终理解

原生 SQS DLQ 和数据库 DeadLetterRecord 不是二选一：

```text
SQS DLQ：
隔离消息本体，停止它继续占用主队列消费能力。

DeadLetterRecord：
让应用和 Operator 查询错误原因、分类、次数、时间和处理状态。
```

完整 Poison 路径是：

```text
Receive
→ Contract Gate 失败
→ 记录 receive N/max，不 ACK
→ Visibility Timeout
→ Redelivery
→ 最后一次持久化 DeadLetterRecord
→ 不 ACK
→ SQS 原生 Redrive 到 DLQ
→ 正常消息继续处理
```

本实验的“3 次”是 Broker delivery attempt，不等于后续 Exp7 要验证的业务
Transient Retry / Backoff / Jitter。Poison 隔离已经通过，但整个 Retry 与
Broker Poison 隔离与受控 Replay 是两条独立路径；2026-08-11 已补充第二个
PostgreSQL 集成测试验证 Replay，但业务 Transient Retry 仍由 Exp7 单独证明。

## 第三方复验

专项实验：

```powershell
dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release `
  --filter "FullyQualifiedName~Phase2.Exp6" `
  --logger "console;verbosity=detailed"
```

预期：

```text
2 passed, 0 failed, 0 skipped
```

全量回归：

```powershell
dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release `
  --logger "console;verbosity=minimal"
```

预期：

```text
当前总数见 `docs/current-state.md`
```

## 已知限制和正式化事项

1. 本实验是 E2 LocalStack 证据，不是 AWS E4 真实账户证据。
2. 当前由应用启动时创建 DLQ 和设置 RedrivePolicy；正式环境应由 Terraform /
   IaC 持有队列拓扑、KMS、Retention、Redrive Allow Policy 和告警。
3. Malformed JSON 没有可信 OrganizationId，当前以 `Guid.Empty` 记录；Operator
   系统级查询必须能绕过租户过滤，并明确显示“未知租户”。
4. `DeadLetterRecord` 当前用“先查询再插入”减少重复，没有
   `OriginalMessageId + MessageType` 唯一约束；极端并发 redelivery 下仍需数据库
   约束提供最终裁决。
5. 如果最后一次接收时数据库不可用，SQS 仍可能把消息移入 DLQ，但数据库审计
   可能缺失；正式项目需要 DLQ audit scanner 做补偿。
6. 当前只接通 Processing Queue 的 Contract Gate；Notification 等其他 Handler
   需要按各自契约实现相同隔离原则。
7. `reliantctl deadletter list/replay` 已接通 PostgreSQL；Replay 要求
   `--organization`、`--operator` 和 `--confirm`，并在同一事务写入 Outbox、
   `DeadLetterRecord` claim 和 `AuditEvent`。Replay 使用新 MessageId，所以会经过
   新的 Inbox 检查；业务层仍必须依赖状态机和 Provider stable key 防止副作用重复。
8. `jobs retry` 不允许直接篡改终态 JobRun；CLI 会拒绝并引导通过受控
   Dead-letter Replay。针对业务终态的“重新打开”仍需独立审批语义，不能伪装成
   通用 Replay。
9. Retry Budget、Transient Retry Exhaustion、Backoff / Jitter 属于后续实验，
   不能用本次 Poison PASS 代替。
10. 2026-08-11 补全回归同时升级了 Microsoft.OpenApi 2.7.5 和
    System.Security.Cryptography.Xml 10.0.10，并更新测试辅助 SQS API；当前
    build 为 0 warning，`dotnet list package --vulnerable --include-transitive`
    为 0 已知漏洞，CI 已增加硬 Gate。
