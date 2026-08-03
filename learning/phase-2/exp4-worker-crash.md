# Phase 2 / Experiment 4 — Worker Crash

## 一页结论

**PASS（E2：真实 Docker Worker + PostgreSQL 17 + LocalStack 3）**

我启动了一个真正运行 `Reliant.Worker.dll` 的 Docker 容器 Worker A。它从
LocalStack SQS 收到消息后，我在它提交业务状态和 ACK 之前执行了：

```powershell
docker kill reliant-exp4-worker-a-1c6936550c
```

容器退出码为 `137`，证明它是被 SIGKILL 非正常终止，而不是应用主动退出。

等待 SQS Visibility Timeout 后，同一个逻辑 MessageId 再次出现，
`ApproximateReceiveCount` 从 1 增加到 2。随后我启动独立的 Worker B 容器，它
处理重投消息、提交业务结果并 ACK。

最终结果：

```text
消息重新投递：是
任务最终恢复：是
业务结果不重复：是
```

## 实验信息

- 日期：2026-08-03
- 测试：
  `WorkerCrashDockerE2ETests.DockerKilledWorker_ShouldRedeliverToSecondWorker_WithoutDuplicateEffect`
- 测试目录：`tests/Reliant.Tests/Integration/Phase2/Exp4/`
- 环境：Windows、.NET SDK 10.0.300、Docker Desktop 29.4.3
- Worker 镜像：`mcr.microsoft.com/dotnet/runtime:10.0`
- 依赖容器：`postgres:17`、`localstack/localstack:3`
- Visibility Timeout：5 秒
- 专项测试：1/1 通过
- Queue / Crash / Dedup 相关回归：11/11 通过，0 失败，0 跳过

## 我的假设

SQS 的 Receive 不是消息删除。Worker 收到消息时，SQS 只是暂时隐藏消息；只有
Worker 最后调用 DeleteMessage，消息才真正完成 ACK。

所以我预期：

```text
Worker A Receive
→ 消息进入不可见窗口
→ Worker A 在 DeleteMessage 前被 docker kill
→ 没有 ACK
→ Visibility Timeout 到期
→ 同一消息重新可见
→ Worker B Receive、处理、ACK
```

## 为什么要使用真实 docker kill

仓库已有的 `CrashBeforeAckE2ETests` 使用进程内 fault injector 抛异常，它可以
验证未 ACK 后的重投逻辑，但不能证明操作系统级的 Worker 进程死亡也能恢复。

这次 Lab 4 没有向生产代码增加故障点。我把 Worker 发布到临时目录，并用官方
.NET Runtime 镜像分别启动 Worker A 和 Worker B：

```text
Worker A container
  └─ dotnet Reliant.Worker.dll

Worker B container
  └─ dotnet Reliant.Worker.dll
```

两个 Worker 使用相同的 PostgreSQL 和 LocalStack SQS，但它们是不同容器、不同
进程和不同 DI 容器。

## 怎样稳定命中“Receive 后、ACK 前”

正常的 Sandbox Provider 很快，如果只看到日志后再执行 kill，Worker 可能已经
完成 ACK。为了让实验可重复，我在测试侧先对目标 Contribution 获取 PostgreSQL
行锁：

```sql
SELECT 1
FROM contributions
WHERE "Id" = @contributionId
FOR UPDATE;
```

因此 Worker A 可以：

```text
成功 Receive SQS 消息
输出 Processing message 日志
读取 Contribution
在提交 Created → Processing 时等待数据库锁
```

但它不能：

```text
提交业务状态
创建 ProcessingAttempt
调用 Provider
写入 Inbox
DeleteMessage / ACK
```

确认 Worker A 已输出指定 MessageId 的 Receive 日志后，我检查数据库仍为
`Created`，且 Attempt、ProviderReference、Inbox 都为 0，然后执行
`docker kill`。

这道行锁只存在于测试进程，不修改生产 Worker。

## 实际执行过程

### 1. 准备消息

我创建：

```text
Organization = 1
Campaign = 1
Contribution = 1，状态 Created
OutboxMessage = 1，状态 Sent
```

然后向真实 LocalStack SQS 发送一条消息。稳定逻辑 MessageId 为：

```text
b64999f7-523d-46e4-9842-23f9860334f0
```

### 2. Worker A 收到消息

Worker A：

```text
Container = reliant-exp4-worker-a-1c6936550c
Received = true
MessageId = b64999f7-523d-46e4-9842-23f9860334f0
```

此时数据库检查：

| 检查项 | kill 前实际值 |
|---|---:|
| Contribution 状态 | Created |
| ProcessingAttempt | 0 |
| ProviderReference | 0 |
| Inbox | 0 |

这说明消息已经被 Worker A Receive，但业务副作用尚未开始。

### 3. 非正常终止 Worker A

测试实际调用：

```text
docker kill reliant-exp4-worker-a-1c6936550c
```

结果：

```text
Container ExitCode = 137
```

`docker kill` 默认发送 SIGKILL。Worker 没有机会执行优雅关闭、finally 清理或
DeleteMessage。

### 4. 等待消息重新出现

测试释放数据库行锁，并等待 SQS Visibility Timeout。随后直接读取 SQS 消息属性：

| 检查项 | 实际值 |
|---|---:|
| Visibility Timeout | 5 秒 |
| 重投 MessageId | 与首次相同 |
| ApproximateReceiveCount | 2 |
| kill 后观察到重投 | 约 15.3 秒 |

`ApproximateReceiveCount=2` 是 SQS 级证据：同一条物理消息至少被领取了两次，
不是测试重新发送了一条新消息。

### 5. Worker B 恢复

我在确认消息重新出现之后才启动 Worker B：

```text
Container = reliant-exp4-worker-b-1c6936550c
ReceivedRedelivery = true
ProcessedAndAcked = true
```

Worker B 使用同一数据库和队列，独立完成任务。

## 数据库最终状态

| 检查项 | 实际值 |
|---|---:|
| Contribution | 1 |
| Contribution 状态 | Succeeded |
| OutboxMessage | 1 |
| Inbox | 1 |
| ProcessingAttempt | 1 |
| ProviderReference | 1 |
| Created → Accepted | 1 |
| Accepted → Processing | 1 |
| Processing → Succeeded | 1 |
| StateTransition 总数 | 3 |
| Dead Letter | 0 |
| Queue 最终状态 | Empty / 已 ACK |

Worker A 在 Provider 调用之前死亡，所以 kill 前数据库中的 Attempt 和
ProviderReference 均为 0。Worker B 恢复后它们各为 1，没有第二份业务副作用。

## 原始关键输出

```text
WORKER A | Container=reliant-exp4-worker-a-1c6936550c | MessageId=b64999f7-523d-46e4-9842-23f9860334f0 | Received=true | dockerKillExitCode=137

REDELIVERY | VisibilityTimeoutSeconds=5 | ApproximateReceiveCount=2 | ElapsedAfterKillMs=15322

WORKER B | Container=reliant-exp4-worker-b-1c6936550c | ReceivedRedelivery=true | ProcessedAndAcked=true

FINAL | Contributions=1 | BusinessState=Succeeded | InboxRows=1 | ProcessingAttempts=1 | ProviderReferences=1 | StateTransitions=3 | DeadLetters=0 | StaleWorkerALeases=0

RESULT | PASS | StartedAt=2026-08-03T01:48:07.2550632Z | CompletedAt=2026-08-03T01:49:18.5310296Z
```

## PASS 条件核对

- [x] Worker A Receive 消息
- [x] 在 ACK 前执行真实 `docker kill`
- [x] Worker A 非正常退出，ExitCode 137
- [x] 等待 Visibility Timeout
- [x] 同一 MessageId 重新出现
- [x] SQS `ApproximateReceiveCount >= 2`
- [x] 在确认重投后启动独立 Worker B
- [x] Worker B 收到重投消息
- [x] Worker B 最终完成并 ACK
- [x] Contribution 最终为 Succeeded
- [x] Inbox 最终只有一条
- [x] ProcessingAttempt 最终只有一条
- [x] ProviderReference 最终只有一条
- [x] 状态转换没有重复
- [x] Queue 最终为空

## 我的最终理解

这次实验说明可靠性来自 Queue 的 ACK 语义，而不是 Worker 本身永远不崩溃：

```text
Receive != 完成
DeleteMessage / ACK = 完成
```

只要 ACK 放在数据库和业务结果提交之后，Worker 在 ACK 前死亡时，Queue 就可以
通过 Visibility Timeout 把消息交给其他 Worker。

同时，重投意味着系统必须继续依赖 Inbox、业务状态、唯一约束和 Provider
Idempotency Key。Lab 4 选择的是“Provider 调用前崩溃”窗口，因此直接观察到
Worker A 副作用为 0、Worker B 副作用为 1；Provider 调用之后但 ACK 之前的窗口
还需要继续依靠 Inbox 和 Provider 幂等保护。

## 第三方复验命令

在仓库根目录执行：

```powershell
dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  --artifacts-path artifacts/phase2-exp4-run `
  --filter "FullyQualifiedName~WorkerCrashDockerE2ETests" `
  --logger "console;verbosity=detailed"
```

测试会：

1. 拉取或复用 `mcr.microsoft.com/dotnet/runtime:10.0`；
2. 启动 PostgreSQL 17 和 LocalStack 3；
3. 临时发布 Worker；
4. 启动 Worker A；
5. 执行真实 `docker kill`；
6. 启动 Worker B；
7. 完成断言后删除两个 Worker 容器和临时发布目录。

预期退出码为 `0`，测试总数和通过数均为 `1`。

我还执行了现有 Visibility Timeout、Crash-before-ACK 和重复消息相关回归：

```powershell
dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  --artifacts-path artifacts/phase2-exp4-regression `
  --filter "FullyQualifiedName~CrashBeforeAckE2ETests|FullyQualifiedName~DuplicateMessageE2ETests|FullyQualifiedName~LocalStackSqsTests" `
  --logger "console;verbosity=minimal"
```

实际结果：`11 passed, 0 failed, 0 skipped`。

## Known Limitations

1. 这是 Docker Desktop + LocalStack 的 E2 实验，不是 AWS 真实 SQS。
2. 为稳定命中故障窗口，测试使用 PostgreSQL 行锁把 Worker A 暂停在首次状态
   提交；生产环境不会使用这把测试锁。
3. 本实验覆盖 Provider 调用前崩溃。Provider 已处理但 Worker 尚未持久化响应的
   更危险窗口，需要单独验证 Provider Idempotency Key 和 Reconciliation。
4. SQS 的重投观察约 15.3 秒，高于配置的 5 秒 Visibility Timeout；Timeout
   表示“至少隐藏这么久”，并不承诺到期瞬间就被下一次 long poll 观察到。
5. 构建仍报告仓库已有的 NuGet 高危漏洞和 SQS SDK 过时 API 警告，需独立处理。
