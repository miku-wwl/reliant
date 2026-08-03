# Phase 2 / Experiment 3 — Duplicate Delivery

## 一页结论

**PASS（E2：真实 PostgreSQL 17 + LocalStack 3，并发故障注入）**

我向 LocalStack SQS 并发发送了两条物理消息，两条消息携带完全相同的稳定逻辑
`MessageId`。两个独立的 ProcessingHandler 执行任务同时通过首次 Inbox 查询，
并在提交初始业务状态前一起释放。

第一次实验真实发现了竞态双写：两个 Worker 都成功写入
`Created → Accepted`，因为 `Contribution.Version` 虽然声明为并发令牌，却从未
递增。修复后，PostgreSQL 乐观并发只允许一个 Worker 提交；另一个 Worker 留下
明确日志、不 ACK，消息重投后被 Inbox 去重。

最终结果：

```text
最终只有一个业务结果：是
重复处理有可解释记录：是
无竞态导致的双写：是
```

## 实验信息

- 日期：2026-08-03
- 测试：
  `DuplicateDeliveryE2ETests.SameMessageIdDeliveredConcurrently_ShouldCommitOneBusinessResult_AndExplainTheLoser`
- 测试目录：`tests/Reliant.Tests/Integration/Phase2/Exp3/`
- 环境：Windows、.NET SDK 10.0.300、Docker Desktop 29.4.3
- 容器：`postgres:17`、`localstack/localstack:3`
- 修复后专项测试：1/1 通过
- 相关回归测试：63/63 通过，0 失败，0 跳过

## 我的假设

同一个逻辑 MessageId 可能因为 Queue redelivery 或重复发送而同时出现在多个
Worker 中。普通的“先 SELECT Inbox，再处理”不能单独保证安全，因为两个 Worker
可能同时查询到“不存在”。

我想验证的是：

```text
MessageId = A 的物理消息 #1
MessageId = A 的物理消息 #2
→ 两个 Worker 并发进入
→ 数据库只允许一个结果提交
→ 输家有明确日志和恢复路径
```

## 我怎样稳定制造并发

测试中设置了两道屏障。

### Queue 领取屏障

第一个 ProcessingHandler 从 SQS 领取消息后先暂停，直到第二个
ProcessingHandler 也领取到相同 MessageId 的另一条物理消息，再同时返回。

这证明不是一个 Worker 顺序处理两次，而是两个并发执行路径都真实拿到了消息。

### 初始状态提交屏障

两个 Worker 都完成：

```text
首次 Inbox SELECT → 未找到
读取 Contribution = Created
内存中执行 Created → Accepted → Processing
```

然后在数据库提交前同时等待。两者到齐后一起执行 `SaveChangesAsync`，强制制造
同一 Contribution、同一旧 Version 的并发更新。

## 第一次运行：FAIL

第一次运行时，Lab 3 没有被我误判成 PASS。实际数据库出现：

```text
Created → Accepted 记录数 = 2
```

测试失败信息：

```text
Assert.Single() Failure: The collection contained 2 items
```

虽然 Provider 的幂等键和 Attempt 唯一约束阻止了第二次外部业务操作，但数据库
审计已经发生重复写入，不符合：

```text
无竞态导致的双写
```

所以“最终 Provider Operation 只有一次”还不足以判定系统通过。

## 根因

`Contribution.Version` 在 EF Core 中配置为 `IsConcurrencyToken()`，但原来的
`TransitionTo` 只修改：

```text
State
UpdatedAt
```

它从不递增 `Version`。两个 Worker 都用 `Version=0` 更新时，数据库看到的条件
没有变化，因此两个 UPDATE 都能成功，乐观并发实际上没有生效。

## 修复

### 1. 状态转换递增 Version

每次合法 `Contribution.TransitionTo` 现在都会执行：

```text
Version++
```

两个 Worker 同时从 Version 0 开始时：

```text
Worker A:
UPDATE ... WHERE Version = 0
→ 成功，Version 写成 2

Worker B:
UPDATE ... WHERE Version = 0
→ 影响 0 行
→ DbUpdateConcurrencyException
→ 整个 SaveChanges 事务回滚
```

由于状态转换记录和 Contribution 更新位于同一次 `SaveChangesAsync`，Worker B
写入的两条状态转换也一起回滚，不会留下重复审计。

### 2. 并发输家有明确日志

ProcessingHandler 现在单独处理 `DbUpdateConcurrencyException`，日志说明：

```text
lost optimistic concurrency;
leaving unacknowledged for Inbox recovery
```

它不删除 SQS 消息。Visibility Timeout 到期后，消息重新出现；此时赢家已经
提交 Inbox，重投消息进入：

```text
already processed (inbox dedup)
```

然后安全 ACK。

### 3. 使用测试专用数据库拦截器

为了不让 Lab 专用控制逻辑进入生产 Handler，我在测试中实现了 EF Core
`SaveChangesInterceptor`。它只拦截从 `Created` 变为 `Processing` 的两次并发
提交，等两个 Worker 都到达后再同时释放。

WorkerHostFixture 只增加一个可选的测试 interceptor 参数；正常启动不传该参数，
生产应用也不会注册或执行这段并发屏障。

## 修复后的实际观察

本次通过运行的 Outbox / MessageId：

```text
aa748729-0f42-4909-81a7-505d5a643cbe
```

### 并发阶段

| 检查项 | 实际值 |
|---|---:|
| Queue Send | 2 |
| 初始并发 Worker 路径 | 2 |
| 状态提交屏障到达数 | 2 |
| 两次 MessageId | 相同 |

### 恢复阶段

| 检查项 | 实际值 |
|---|---:|
| Queue Receive | 3 |
| Queue Delete / ACK | 2 |
| 并发输家日志 | lost optimistic concurrency |
| 重投日志 | already processed (inbox dedup) |

`Receive=3` 的原因是：

1. Winner 首次领取；
2. Loser 首次领取；
3. Loser 未 ACK，Visibility Timeout 后再次领取。

`Delete=2` 的原因是 Winner 完成后 ACK 一条，Loser 的重投被 Inbox 去重后再 ACK
一条。

### 数据库最终状态

| 检查项 | 实际值 |
|---|---:|
| Contribution | 1 |
| Contribution 状态 | Succeeded |
| Inbox | 1 |
| ProcessingAttempt | 1 |
| ProviderReference | 1 |
| Provider Operation | 1 |
| Created → Accepted | 1 |
| Accepted → Processing | 1 |
| Processing → Succeeded | 1 |
| StateTransition 总数 | 3 |
| Dead Letter | 0 |

## 原始关键输出

```text
CONCURRENT DELIVERY | OutboxId=aa748729-0f42-4909-81a7-505d5a643cbe | QueueSend=2 | InitialConcurrentWorkers=2 | StateCommitBarrierArrivals=2

RECOVERY | QueueReceive=3 | QueueDelete=2 | ConcurrentLoser='lost optimistic concurrency' | Redelivery='already processed (inbox dedup)'

FINAL | Contributions=1 | BusinessState=Succeeded | InboxRows=1 | ProcessingAttempts=1 | ProviderReferences=1 | ProviderOperations=1 | StateTransitions=3 | DeadLetters=0

RESULT | PASS | StartedAt=2026-08-03T01:32:29.9851046Z | CompletedAt=2026-08-03T01:32:51.1553764Z
```

## PASS 条件核对

- [x] 同一 MessageId 投递两次
- [x] 两条物理消息并发发送
- [x] 两个 Worker 执行路径同时进入处理
- [x] 两个 Worker 都通过首次 Inbox 查询
- [x] 数据库乐观并发只允许一个状态提交
- [x] 并发输家的事务完全回滚
- [x] 并发输家不 ACK，并在 Visibility Timeout 后重投
- [x] 重投由 Inbox 明确去重
- [x] 最终业务数据只有一份
- [x] 状态转换没有双写
- [x] Provider 业务副作用只有一次
- [x] 日志可以解释第二次处理结果

## 我的最终理解

这次实验让我看到三层保护各自负责不同问题：

```text
Contribution Version 乐观并发：
阻止两个 Worker 同时提交业务状态和状态转换。

Inbox MessageId 唯一性：
识别已经完成的逻辑消息，重投时直接跳过。

Provider Idempotency Key：
即使应用竞态越过前两层，也防止外部业务副作用重复。
```

只做一次普通 Inbox SELECT 不够，因为两个 Worker 可以同时看到“不存在”。必须
有数据库级约束或并发令牌参与最终裁决，而且输家不能静默 ACK；它需要留下日志
并让消息进入可恢复路径。

## 第三方复验命令

在仓库根目录执行：

```powershell
dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  --artifacts-path artifacts/phase2-exp3-run `
  --filter "FullyQualifiedName~DuplicateDeliveryE2ETests" `
  --logger "console;verbosity=detailed"
```

预期退出码为 `0`，测试总数和通过数均为 `1`。

由于本实验修复了生产代码中的 Version 递增，我还执行了覆盖状态机、状态审计、
重复消息、崩溃恢复、Provider 并发、回调、重试、对账和最终 E2E 的相关回归：

```powershell
dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  --artifacts-path artifacts/phase2-exp3-regression `
  --filter "FullyQualifiedName~ContributionStateMachineTests|FullyQualifiedName~StateTransitionAuditTests|FullyQualifiedName~DuplicateDeliveryE2ETests|FullyQualifiedName~DuplicateMessageE2ETests|FullyQualifiedName~CrashBeforeAckE2ETests|FullyQualifiedName~ProviderConcurrencyTests|FullyQualifiedName~CallbackTests|FullyQualifiedName~RetrySchedulingTests|FullyQualifiedName~ReconciliationTests|FullyQualifiedName~FinalE2ETests" `
  --logger "console;verbosity=minimal"
```

实际结果：`63 passed, 0 failed, 0 skipped`。

## Known Limitations

1. 两个“Worker”是同一个 WorkerHost 进程中的两个并发 ProcessingHandler 任务，
   各自有独立 DI scope、DbContext、Lease 和 WorkerId；尚未覆盖两个独立容器或
   两台主机。
2. 并发由测试专用 `SaveChangesInterceptor` 精确放大，属于 E2，不是生产负载
   下的概率性压力测试。
3. `Version` 是应用递增的整数令牌；所有未来绕过 `TransitionTo` 的状态更新都
   必须同样维护 Version。
4. 构建仍报告仓库现有的 NuGet 高危漏洞与 SQS SDK 过时 API 警告，需独立处理。
