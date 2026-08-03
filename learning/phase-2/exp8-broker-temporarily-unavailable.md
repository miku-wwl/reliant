# Phase 2 / Experiment 8 — Broker Temporarily Unavailable

## 一页结论

**PASS（E2：PostgreSQL Testcontainer + LocalStack SQS + 真实 WorkerHost）**

我在 WorkerHost 正常运行时暂停 LocalStack，让 SQS 端点暂时无法响应，然后通过
真实 `CreateContributionCommand` 创建一笔业务请求。

Broker 不可用期间，数据库中的 Contribution、OutboxMessage 和 JobRun 都保留：

```text
Contribution = Created
OutboxMessage = Pending
Outbox SentAt = null
JobRun = Pending
```

Publisher 将 3 次失败分类为 transient `Timeout`，原子记录 `SendCount=3`，并按
`500ms → 1000ms → 2000ms` 退避。LocalStack 恢复后约 1.9 秒，原 Pending
Outbox 被成功发送，Contribution 最终进入 Succeeded。

最终只有一条 Inbox、一条 ProcessingAttempt、一条 ProviderReference 和一次
Provider 业务副作用。恢复后再观察 3 秒，没有新增发送失败或继续重试：

```text
业务状态保留：是
Outbox 消息保留：是
Broker 恢复后继续发布：是
静默丢失：否
无限高速重试：否
重复业务副作用：否
```

## 实验信息

- 日期：2026-08-03
- 测试目录：`tests/Reliant.Tests/Integration/Phase2/Exp8/`
- 测试：
  `BrokerOutage_ShouldPreserveOutbox_AndPublishAfterRecovery`
- 环境：.NET 10、PostgreSQL 17 Testcontainer、LocalStack 3、真实 WorkerHost
- 故障注入：Docker pause / unpause LocalStack
- Publish 请求超时：1 秒
- AWS SDK 内部错误重试：0（实验中关闭，单独观察应用层重试）
- 应用层 Backoff：500ms、1000ms、2000ms cap
- Jitter：实验中设为 0，便于精确断言；生产默认保留 0–250ms
- Exp8 专项：1/1 通过
- Phase 2 Exp1–Exp8：10/10 通过
- 最终全量：156/156 通过

## 我的假设

业务事务不应该依赖 Broker 当时是否可用。正确边界应当是：

```text
业务事务
  ├─ Contribution
  ├─ OutboxMessage
  └─ JobRun
       同一数据库事务提交

Publisher
  ├─ Broker 不可用 → Outbox 保持 Pending
  ├─ 失败可分类、可计数
  ├─ 重试有 Backoff + Jitter
  └─ Broker 恢复 → 继续 Publish
```

因此，Broker 故障可以延迟异步业务，但不能回滚已经提交的业务请求，也不能把
Pending Outbox 静默改成 Sent 或丢弃。

## 实验前的代码 Review

### 1. SQS 调用没有应用级时间边界

原 `SqsQueueAdapter` 直接等待 AWS SDK 调用。LocalStack 停止后，Publisher 可能
长时间阻塞在一次 `SendMessage`，应用层既无法记录失败，也无法执行自己的退避。

### 2. SendCount 实际上从未增加

`OutboxMessage` 已有 `SendCount`，Publisher 也检查：

```text
SendCount >= MaxSendAttempts - 1
```

但是失败路径没有更新 `SendCount`。所以最大次数判断永远不可达，数据库也没有
Broker 故障的持久化审计。

### 3. Publisher 没有错误分类

原日志只有：

```text
Failed to send outbox message
```

无法区分 Timeout、NetworkFailure、RateLimited、ServerError 或永久 4xx，也无法
决定应该保留 Pending 还是最终标为 Failed。

### 4. 一个批次会连续冲击故障 Broker

Publisher 每次最多读取 50 条 Pending Outbox。如果 Broker 不可用，原循环会对
批次中的每条消息各发送一次。积压越大，单轮故障请求越多，容易放大故障。

## 学生视角：中间过程

### 第一次运行：FAIL — 调用卡住

我先写真实故障测试，再运行未修复代码。LocalStack 停止后，30 秒内没有观察到
3 条 Publisher 失败日志：

```text
Publisher did not expose three classified failures
```

日志只有 Worker 启动记录。这说明测试不是“等得不够久”，而是一次 SDK 请求没有
及时返回，应用层的错误分类、计数和退避根本没有机会执行。

### 第二次运行：FAIL — 超时范围过大

第一次修复把 SQS 客户端全局 Timeout 设成 1 秒。Publisher 很快返回了，但 Worker
的正常 SQS Long Poll 是 5 秒，也被 1 秒全局超时取消。

这个失败让我确认：Publish 短请求和 Receive Long Poll 不能共享同一个超时边界。
最终改成按操作设置：

```text
Queue provisioning / delete：RequestTimeout
Publish：PublishTimeout
Receive：至少 7 秒，覆盖 5 秒 Long Poll
```

### 第三次运行：FAIL — 测试容器临时端口变化

我最初使用 Testcontainers 的 stop/start。LocalStack 容器恢复了，但测试使用随机
Host Port；stop/start 后已构建的 WorkerHost 仍指向旧测试端口，于是继续收到
Connection Refused。

这不是生产 Publisher 的恢复缺陷，而是测试环境的动态端口副作用。生产 Broker
通常通过稳定 DNS/Service Endpoint 暴露。

最终故障注入改为 Docker pause/unpause：

- Broker 在故障窗口内真实不可响应；
- Host Port 和 Queue 数据保持不变；
- WorkerHost 不重启；
- 恢复后必须由同一个 Publisher 自动继续。

### 第四次运行：PASS

最终真实输出：

```text
OUTAGE | Broker=LocalStackPaused | BusinessState=Created | OutboxStatus=Pending | SentAt=null | Failures=3 | Category=Timeout | Transient=true

BACKOFF | Failure1=500ms | Failure2=1000ms | Failure3=2000ms

RECOVERY | BrokerRestarted=true | OutboxStatus=Sent | SendCount=3 | BusinessState=Succeeded | RecoveryMs=1901

FINAL | BusinessRows=1 | OutboxRows=1 | InboxRows=1 | ProcessingAttempts=1 | ProviderReferences=1 | ProviderEffects=1 | SilentLoss=false

STABILITY | WaitMs=3000 | SendCountBefore=3 | SendCountAfter=3 | FailureLogsBefore=3 | FailureLogsAfter=3 | ContinuedRetry=false

RESULT | PASS | DurationMs=25390
```

## 修复设计

### 1. 为 Queue 操作设置正确的超时边界

`SqsQueueAdapter` 新增两个配置：

```text
Queue:RequestTimeoutSeconds
Queue:PublishTimeoutSeconds
```

Publish 使用较短超时；Receive 的超时必须覆盖 5 秒 Long Poll。AWS SDK 的
`MaxErrorRetry` 也可配置，避免 SDK 内部重试和应用层重试叠加后变得不可预测。

### 2. 统一 Publisher 错误分类

`QueueMessagePublisher` 将 Queue 异常转换为 `QueuePublishException`，携带：

```text
ErrorCategory
IsTransient
InnerException
```

当前分类包括：

| Queue 故障 | ErrorCategory | Transient |
| --- | --- | --- |
| Timeout / TaskCanceled | Timeout | true |
| HTTP / IO 连接故障 | NetworkFailure | true |
| HTTP 429 | RateLimited | true |
| HTTP 5xx | ServerError | true |
| 401 / 403 | AuthenticationFailure | false |
| 其他明确 4xx | PermanentBusinessRejection | false |

Host 正常关闭产生的 cancellation 不会被包装成 Broker 故障。

### 3. 原子持久化失败次数

`IOutboxRepository.RecordSendFailureAsync` 使用数据库原子更新：

```text
SendCount = SendCount + 1
```

并只更新仍处于 Pending 的消息。这样每条结构化失败日志都能和数据库中的
`SendCount` 对应。

### 4. Publisher 使用指数 Backoff + Jitter

连续失败时：

```text
delay = min(base × 2^(failure-1), cap) + jitter
```

达到 cap 后仍保留 jitter，避免多个 Publisher 在长期故障时同步唤醒。

本实验关闭 jitter 后精确得到：

| Failure | Delay |
| ---: | ---: |
| 1 | 500ms |
| 2 | 1000ms |
| 3 | 2000ms |

### 5. Broker 故障时停止当前批次

第一条消息 Publish 失败后，Publisher 不再继续冲击本批次剩余消息，而是等待
Backoff 后重新扫描。这把故障请求频率从“每轮最多 50 次”降到“每个 Backoff
窗口 1 次”。

### 6. Transient 和 Permanent 分开处理

Transient Broker 故障继续保留 Pending，不能因为暂时停机达到任意次数就丢失
自动恢复能力。

明确的 Permanent Queue 错误最多尝试 10 次，之后标记 Failed。这个次数沿用原
Publisher 常量，但现在 `SendCount` 已真实更新，所以该保护第一次变得可达。

## 数据库状态核对

### Broker 故障期间

| 检查项 | 实际值 |
| --- | --- |
| Contribution | Created |
| OutboxMessage | 1 |
| Outbox Status | Pending |
| SentAt | null |
| SendCount | 3 |
| JobRun | Pending |
| ProcessingAttempt | 0 |

### Broker 恢复后

| 检查项 | 实际值 |
| --- | ---: |
| Contribution | Succeeded |
| OutboxMessage | 1 |
| Outbox Status | Sent |
| SendCount | 3 |
| Inbox | 1 |
| ProcessingAttempt | 1 |
| ProviderReference | 1 |
| Provider 业务副作用 | 1 |

## PASS 条件逐项判定

| PASS 条件 | 证据 | 判定 |
| --- | --- | --- |
| 业务状态保留 | Broker 暂停期间 Contribution=Created | PASS |
| Outbox 消息保留 | Pending、SentAt=null、SendCount=3 | PASS |
| Broker 恢复后继续发布 | 恢复后约 1901ms 进入 Sent | PASS |
| 无静默丢失 | 最终完整到达 Worker 和 Provider | PASS |
| 无无限重试 | 有限故障窗口只发生 3 次退避；Sent 后停止 | PASS |
| 无重试风暴 | 500/1000/2000ms 指数退避，每轮故障只尝试一条 | PASS |

## “无无限重试”的准确边界

这里不能把 transient outage 在第 10 次后直接标记 Failed。否则 Broker 在第
11 次等待期间恢复时，Outbox 已被放弃，反而违反“恢复后继续发送”。

因此本实验中的“无无限重试”准确含义是：

```text
没有无等待的无限紧循环
没有每批消息同时冲击 Broker
延迟指数增长并达到上限
消息 Sent 后立即停止
```

如果 Broker 永远不恢复，Pending 消息仍会以 `cap + jitter` 的低频率尝试。这是
保证 transient 故障最终恢复的 deliberate behavior，不应伪装成“有限次数后
成功或失败”。生产准备阶段仍需要配套：

- Outbox oldest-pending-age 告警；
- 连续 Publisher failure 指标；
- Broker outage runbook；
- 长期 Pending 的人工处置策略。

这些可观测性和运营策略不属于本实验已经证明的范围。

## 为什么这些生产代码修改是必要的

| 修改 | 不修改的后果 |
| --- | --- |
| Queue 请求超时 | Broker 故障可能让一次调用长期卡住 |
| 错误分类 | 无法区分 transient 和 permanent |
| SendCount 原子更新 | Attempt 不可审计，最大次数判断永远不可达 |
| Backoff + Jitter | 故障期间形成高频重试或同步重试 |
| 失败后中止当前批次 | Backlog 越大，对故障 Broker 的冲击越大 |

没有修改 Contribution 交易规则、Consumer 幂等逻辑、Provider 或 Job/Lease
状态机。这次改动只补齐 Outbox Publisher 面对 Broker 故障的恢复边界。

## 验证命令

```powershell
dotnet build Reliant.slnx -c Release --no-restore

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build `
  --filter "FullyQualifiedName~Phase2.Exp8" `
  --logger "console;verbosity=detailed"

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build `
  --filter "FullyQualifiedName~Integration.Phase2.Exp"

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build
```

最终结果：

```text
Build: 0 errors
Exp8: 1/1 passed
Phase 2 Exp1–Exp8: 10/10 passed
Full suite: 156/156 passed
```

构建仍报告仓库已有的 package vulnerability / obsolete API 警告，本实验没有把
这些既有警告误写为已解决。

## 我的学习结论

这次最重要的学习不是“加一个 retry”，而是区分三层时间边界：

1. SDK 内部 Retry 必须有边界；
2. 单次 Queue 操作必须有适合自身语义的 Timeout；
3. 应用层 Retry 必须可分类、可审计、带 Backoff 和 Jitter。

另外，故障测试工具本身也可能制造假问题。stop/start 导致随机 Host Port 变化，
不能被误诊为生产恢复失败；pause/unpause 才能在这个 Testcontainers 环境中保持
稳定端点，同时真实制造 Broker 暂时不可响应。

最终我不仅证明了“数据还在”，还证明了：

```text
故障被看见
失败被分类
次数被持久化
重试被限速
恢复后自动收敛
业务副作用只有一次
```
