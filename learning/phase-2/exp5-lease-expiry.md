# Phase 2 / Experiment 5 — Lease Expiry

## 一页结论

**PASS（E1 原子领取 + E2 真实双 Docker Worker 接管）**

我先检查了原实现，发现 Lease 虽然会过期、Maintenance 也会释放过期 Lease，
但 ProcessingHandler 每次都使用随机 `JobRunId`，而且领取新消息时不检查同一
Job 的 active Owner。也就是说，原来的 Lease 更像运行记录，不能真正阻止其他
Worker 提前处理。

修复后，`JobRun` 与处理 Outbox 在同一个数据库提交中创建，并共用同一个 Id。
Worker 获取 Lease 时同步创建 `JobAttempt`、把 JobRun 改为 Running。数据库部分
唯一索引保证同一 JobRun 最多一个 active Lease。Worker B 在 Worker A 的 Lease
有效时收到重投消息，只记录 defer，不处理、不 ACK；Lease 到期后 Maintenance
scanner 在同一事务中释放 Lease、把 Attempt 1 标成 Abandoned、把 JobRun 放回
Pending，B 才能创建 Attempt 2 接管。

最终结果：

```text
Job 不永久卡在 Processing：是
只有一个有效 Owner：是
任务最终完成：是
```

## 实验信息

- 日期：2026-08-03
- 测试目录：`tests/Reliant.Tests/Integration/Phase2/Exp5/`
- 原子领取测试：
  `ConcurrentLeaseAcquisition_ShouldHaveExactlyOneWinner`
- 崩溃接管测试：
  `ExpiredLease_ShouldBeReleased_AndSecondWorkerShouldTakeOver`
- 迁移兼容测试：
  `Migration_ShouldBackfillLegacyLeaseAndAttemptJobRun`
- 环境：Windows、.NET SDK 10.0.300、Docker Desktop 29.4.3
- Worker 镜像：`mcr.microsoft.com/dotnet/runtime:10.0`
- 依赖容器：`postgres:17`、`localstack/localstack:3`
- SQS Visibility Timeout：2 秒
- SQS maxReceiveCount：20（覆盖 10 秒 Lease 内的合法 defer）
- Lease：10 秒
- Heartbeat：500 毫秒
- 专项测试：3/3 通过
- Lab 3 回归：1/1 通过
- Lab 4 回归：1/1 通过
- 相关消息 / Crash / Circuit / Retry 回归：13/13 通过
- 全量测试：154/154 通过

## 我的假设

Lease 的核心不是“数据库里有一条 Lease”，而是：

```text
同一个 Job 在任意时刻最多只有一个 active Owner
```

Worker 崩溃后不会再发 Heartbeat，所以 Lease 最终过期。其他 Worker 在过期之前
不能抢走 Job；过期之后必须能够发现并接管。

本实验中的 Job 是已经持久化的 `JobRun`：

```text
JobId = Outbox MessageId
Job Processing = Contribution Processing
JobAttempt = Worker 每次取得 Owner 后的一次执行
ProcessingAttempt = 对外部 Provider 的一次调用
```

## 实验前发现的缺口

### 1. JobRunId 每次随机生成

原来的 ProcessingHandler 创建 Lease 时执行：

```text
JobRunId = Guid.NewGuid()
```

同一条消息重投后会获得另一个随机 JobRunId，所以 Worker B 无法根据 JobId 找到
Worker A 的 Lease。

### 2. Worker 不检查 active Owner

收到消息后，原实现直接创建新 Lease，没有先判断同一个 Job 是否已经有人处理。
因此 Lease 到期与否不会阻止新 Worker 继续执行。

### 3. Maintenance 只有释放动作

原来的 Maintenance scanner 已经可以：

```text
查找 IsActive=true 且 ExpiresAt < now
→ Release Lease
```

但由于消息处理端没有以稳定 JobId 领取，释放动作无法形成完整接管闭环。

## 修复设计

### 1. MessageId 映射稳定 JobId

如果 MessageId 是 GUID，直接使用它：

```text
JobRunId = Guid.Parse(MessageId)
```

非 GUID MessageId 使用 SHA-256 的前 16 bytes 生成稳定 GUID。相同 MessageId
始终得到相同 JobId。

### 2. JobRun 与 Outbox 同事务创建

创建 Contribution 和调度 Retry 时，代码在同一个 `SaveChanges` / transaction
里同时写入：

```text
OutboxMessage(Pending)
JobRun(Pending)
JobRun.Id = OutboxMessage.Id
```

因此 Publisher crash 不会留下“有消息但没有 JobRun”的新数据。Worker 仍保留
幂等 `EnsurePending`，只用于升级时已经在 SQS 中的旧消息。

### 3. 数据库约束保证原子 TryAcquire

我没有使用不安全的：

```text
SELECT active lease
如果没有，再 INSERT
```

因为两个 Worker 可以同时 SELECT 到“没有”。

数据库增加部分唯一索引：

```sql
CREATE UNIQUE INDEX "IX_leases_JobRunId"
ON leases ("JobRunId")
WHERE "IsActive";
```

领取使用：

```sql
INSERT INTO leases (...)
VALUES (...)
ON CONFLICT ("JobRunId")
    WHERE "IsActive"
    DO NOTHING;
```

两个 Worker 不需要先读后写，由 PostgreSQL 唯一约束直接裁决 winner。

### 4. Lease 与 JobAttempt 同事务开始

成功获得 Lease 后，同一事务继续执行：

```text
JobRun: Pending → Running
JobRun.AttemptCount += 1
JobAttempt: Running
JobAttempt.LeaseId = 当前 Lease
JobAttempt.WorkerId = 当前 Worker
```

Lease 插入和 Attempt 创建之间发生异常时，整个事务回滚，不会留下“有 Owner
但没有 Attempt”的半状态。

### 5. active Lease 存在时不 ACK

`TryAcquireAsync` 返回 false 时，Worker：

```text
记录现有 Owner 和 ExpiresAt
不执行业务处理
不删除 SQS 消息
等待下一次 redelivery
```

### 6. 扩展现有过期扫描器

没有增加新的扫描服务。Worker B 容器中已有的
`ScheduledMaintenanceHandlerService` 在一个数据库事务中执行：

```text
Lease: Active → Inactive
JobAttempt 1: Running → Abandoned
JobRun: Running → Pending
```

释放提交后，下一次 SQS redelivery 才能成功 TryAcquire。

### 7. Lease / Heartbeat 可配置

生产默认值仍然是：

```text
Lease = 30 秒
Heartbeat = 10 秒
```

Lab 使用：

```text
Lease = 10 秒
Heartbeat = 500 毫秒
```

只缩短实验时间，不改变生产默认行为。

复核时还发现原 Heartbeat task 与业务处理共用 scoped DbContext。EF Core DbContext
不支持并发操作，因此现在每次 Heartbeat 都创建独立 DI scope 和 DbContext，
避免长任务续租因为并发访问而静默停止。

## 怎样稳定制造 Worker A 崩溃

我先锁住 PostgreSQL 的 `processing_attempts` 表：

```sql
LOCK TABLE processing_attempts
IN ACCESS EXCLUSIVE MODE;
```

因此 Worker A 可以完成：

```text
Receive 消息
原子获得 Lease
JobRun: Pending → Running
JobAttempt 1: Running
Contribution: Created → Accepted → Processing
提交两条状态转换
```

但它会在查询 / 创建 Provider Attempt 前等待表锁。数据库确认 Job 已经是
Processing、active Owner 只有 Worker A 后，我执行真实：

```powershell
docker kill reliant-exp5-worker-a-cb1ddd7651
```

Worker A ExitCode 为 `137`，没有机会进入 finally 释放 Lease。

## 第一次运行：测试夹具 FAIL

第一次运行没有被我标成系统 FAIL 或 PASS。测试为了确认 kill 前 Attempt=0，
在仍持有 `ACCESS EXCLUSIVE` 表锁时查询了 `processing_attempts`。测试自己的
SELECT 也被锁住，最终出现：

```text
NpgsqlException: Exception while reading from stream
Timeout during reading attempt
```

这是实验夹具自锁，不是 Lease 接管失败。

修正方式是：

```text
持锁期间只检查 Contribution、Lease、StateTransition
docker kill Worker A
释放表锁
再检查 Attempt=0、ProviderReference=0
```

修正后专项测试通过；接入完整 JobRun 后又增加 migration 兼容测试，最终 3/3
通过。

## 原子领取测试

两个独立 DbContext 同时为同一个 JobId 调用 TryAcquire：

```text
Contenders = 2
Winners = 1
ActiveOwners = 1
```

实际输出：

```text
ATOMIC ACQUIRE | JobId=90ae462d-52a3-4a20-9568-03cf56970de5 | Contenders=2 | Winners=1 | ActiveOwners=1 | Winner=worker-a
```

这证明“只有一个 Owner”不是依赖测试执行顺序，而是由数据库原子裁决。

## Migration 兼容验证

正式项目不能假设数据库永远是空的。我把测试库先降到上一版 migration，写入
旧结构的 JobAttempt 和没有对应 JobRun 的 Lease，再升级到
`CompleteJobExecutionModel`。

实际输出：

```text
MIGRATION | LegacyJobId=9dd5fb60-bf23-40d5-af57-78ea0f88c589 | JobRunBackfilled=true | JobStatus=Succeeded | AttemptCount=1 | AttemptStatus=Succeeded | LeasePreserved=true
```

这确认 migration 会：

```text
为旧 Lease / JobAttempt 补建 JobRun
保留旧 Attempt 的成功结果
保留 Lease 历史
再启用 JobRun 外键和唯一约束
```

## 真实接管过程

### Worker A

```text
Container = reliant-exp5-worker-a-e0e80feb3d
JobId = a0308531-01f3-41cb-9108-c334c58d14a2
JobRun = Running
JobAttempt 1 = Running
Contribution = Processing
LeaseId = 2667e9cb-fd4a-4c08-bf66-b919196de75e
ActiveOwners = 1
docker kill ExitCode = 137
```

kill 前数据库：

| 检查项 | 实际值 |
|---|---:|
| JobRun 状态 | Running |
| JobRun AttemptCount | 1 |
| JobAttempt 1 | Running |
| JobAttempt 1 Owner | Worker A |
| Contribution 状态 | Processing |
| active Lease | 1 |
| Lease Owner | Worker A |
| ProcessingAttempt | 0 |
| ProviderReference | 0 |
| StateTransition | 2 |

### Worker B 在 Lease 有效时

SQS Visibility Timeout 是 2 秒，短于 10 秒 Lease，所以 Worker B 会在 Lease
到期前看到重投消息。

Exp6 接通原生 SQS DLQ 后，本实验把 `maxReceiveCount` 显式设为 20。该阈值必须
覆盖 Lease 有效期间的合法 defer；若仍使用压缩实验参数下的 5，消息可能在 Lease
到期前被 Broker 误送入 DLQ。

实际结果：

```text
WorkerBDeferrals = 2
```

每次都因为 Worker A 仍是 active Owner 而退出，不 ACK、不创建自己的 Lease、
不调用 Provider。

这时数据库仍然：

```text
ActiveOwners = 1
Owner = Worker A
Worker B Lease = 0
```

### Lease 到期与扫描

```text
ExpiresAt = 2026-08-03T03:46:30.3728530Z
Scanner observed release at =
2026-08-03T03:46:32.1320261Z
```

扫描器确认过期后释放 Worker A Lease，并在同一事务记录：

```text
Worker A Lease = Inactive
JobAttempt 1 = Abandoned
JobRun = Pending
```

### Worker B 接管

下一次 SQS redelivery 中，Worker B 成功 TryAcquire：

```text
Worker B acquired Lease
JobRun: Pending → Running
JobAttempt 2: Running
Contribution 当前为 Processing，所以从断点继续
创建一个 ProcessingAttempt
Provider 成功
Processing → Succeeded
写入 Inbox
JobAttempt 2: Running → Succeeded
JobRun: Running → Succeeded
ACK
```

从 Worker A 被 kill 到最终恢复约：

```text
17819 ms
```

## 数据库最终状态

| 检查项 | 实际值 |
|---|---:|
| JobRun | 1 |
| JobRun 状态 | Succeeded |
| JobRun AttemptCount | 2 |
| JobAttempt 历史 | 2 |
| JobAttempt 1 | Abandoned / Worker A |
| JobAttempt 2 | Succeeded / Worker B |
| Contribution | 1 |
| Contribution 状态 | Succeeded |
| active Owner | 0（任务已完成） |
| Lease 历史 | 2（Worker A、Worker B 各一条） |
| Worker A Lease | Inactive |
| Worker B Lease | Inactive |
| Inbox | 1 |
| ProcessingAttempt | 1 |
| ProviderReference | 1 |
| Created → Accepted | 1 |
| Accepted → Processing | 1 |
| Processing → Succeeded | 1 |
| StateTransition 总数 | 3 |
| Dead Letter | 0 |
| Queue | Empty / 已 ACK |

Worker A 在 Provider Attempt 前崩溃，所以最终只有 Worker B 的一个业务 Attempt
和一个 ProviderReference。

## 原始关键输出

```text
WORKER A | Container=reliant-exp5-worker-a-e0e80feb3d | JobId=a0308531-01f3-41cb-9108-c334c58d14a2 | JobStatus=Running | Attempt=1/Running | BusinessState=Processing | LeaseId=2667e9cb-fd4a-4c08-bf66-b919196de75e | ActiveOwners=1 | dockerKillExitCode=137

LEASE EXPIRY | LeaseSeconds=10 | WorkerBDeferrals=2 | ScannerReleased=true | ExpiredAt=2026-08-03T03:46:30.3728530Z | ReleasedObservedAt=2026-08-03T03:46:32.1320261Z

WORKER B | Container=reliant-exp5-worker-b-e0e80feb3d | Takeover=true | Attempt=2/Succeeded | ProcessedAndAcked=true | RecoveryAfterKillMs=17819

FINAL | JobStatus=Succeeded | JobAttempts=2 | Attempt1=Abandoned | Attempt2=Succeeded | BusinessState=Succeeded | ActiveOwners=0 | LeaseHistory=2 | ProcessingAttempts=1 | ProviderReferences=1 | StateTransitions=3 | DeadLetters=0

RESULT | PASS | StartedAt=2026-08-03T03:45:43.8579163Z | CompletedAt=2026-08-03T03:46:39.1967038Z
```

## PASS 条件核对

- [x] Worker A 获取稳定 JobId 对应的 Lease
- [x] JobRun 进入 Running
- [x] JobAttempt 1 进入 Running
- [x] active Owner 只有 Worker A
- [x] Worker A 被真实 `docker kill`
- [x] Worker A ExitCode 为 137
- [x] Worker B 在 Lease 有效时拒绝提前接管
- [x] Worker B 不 ACK 被拒绝的 redelivery
- [x] 等待 Lease 到期
- [x] Worker B 的 Maintenance scanner 发现过期 Lease
- [x] 过期 Lease 被释放
- [x] JobAttempt 1 被记录为 Abandoned
- [x] JobRun 被放回 Pending
- [x] Worker B 原子获取新 Lease
- [x] Worker B 创建 JobAttempt 2
- [x] Worker B 接管并完成
- [x] JobAttempt 2 最终为 Succeeded
- [x] JobRun 最终为 Succeeded
- [x] 最终没有 active Lease
- [x] Lease 历史可解释 A、B 两个 Owner
- [x] ProcessingAttempt 只有一个
- [x] ProviderReference 只有一个
- [x] 状态转换完整且不重复
- [x] Queue 最终为空
- [x] 两个并发 contender 只有一个 TryAcquire winner

## 我的最终理解

Lease、Visibility Timeout 和 Inbox 解决的是不同问题：

```text
SQS Visibility Timeout：
让未 ACK 消息重新出现。

Lease：
防止旧 Owner 尚有效时，其他 Worker 提前执行。

JobRun / JobAttempt：
提供任务整体状态和每次 Worker 执行的持久化审计。

Maintenance scanner：
发现崩溃后停止 Heartbeat 的过期 Owner，关闭旧 Attempt，
把 Job 放回 Pending 并释放处理权。

Inbox / Provider 幂等：
保护 ACK 丢失或未知 Provider 结果等其他重复窗口。
```

只有过期时间但 Worker 不检查 Lease，Lease 没有所有权意义。只有检查但不是原子
领取，两个 Worker 仍可能同时成为 Owner。完整闭环必须是：

```text
Outbox 与 JobRun 同事务创建
→ 原子 TryAcquire
→ JobAttempt Running
→ Heartbeat
→ Crash 后停止 Heartbeat
→ Scanner: Lease Inactive / Attempt Abandoned / Job Pending
→ Redelivery
→ 新 Worker TryAcquire / Attempt 2
→ Job Succeeded
```

## 第三方复验命令

```powershell
dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  --artifacts-path artifacts/phase2-exp5-final `
  --filter "FullyQualifiedName~LeaseExpiryDockerE2ETests" `
  --logger "console;verbosity=detailed"
```

预期：`3 passed, 0 failed, 0 skipped`。

相关回归：

```powershell
dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  --artifacts-path artifacts/phase2-exp5-regression `
  --filter "FullyQualifiedName~Phase2.Exp1|FullyQualifiedName~Phase2.Exp2|FullyQualifiedName~Phase2.Exp3|FullyQualifiedName~CrashBeforeAckE2ETests|FullyQualifiedName~DuplicateMessageE2ETests|FullyQualifiedName~CircuitOpenE2ETests|FullyQualifiedName~SafeRetryE2ETests|FullyQualifiedName~FinalE2ETests" `
  --logger "console;verbosity=minimal"
```

实际结果：`13 passed, 0 failed, 0 skipped`。

全量回归：

```powershell
dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release `
  --logger "console;verbosity=minimal"
```

实际结果：`153 passed, 0 failed, 0 skipped`。

## Known Limitations

1. Lease 只阻止新 Worker 获得 Owner。若旧 Worker 只是长时间暂停、Lease 过期后
   又恢复运行，还需要 fencing token 阻止旧 Owner 继续提交；本实验的 Worker A
   是 ExitCode 137 的确定死亡进程。
2. 为稳定命中窗口，测试使用 PostgreSQL 表锁暂停 Worker A；生产环境不会使用
   这把测试锁。
3. Heartbeat 当前续约数据库 Lease，但没有同步延长 SQS Visibility Timeout。
   长任务需要增加 ChangeMessageVisibility，避免重复 receive 次数过早进入 DLQ。
4. Job / Attempt / Lease 历史需要在后续 Phase 增加 retention、容量指标和清理策略。
5. 实验运行于 Docker Desktop + LocalStack，不是 AWS 真实 SQS。
6. 构建仍报告仓库已有的 NuGet 高危漏洞和 SQS SDK 过时 API 警告，需独立处理。
