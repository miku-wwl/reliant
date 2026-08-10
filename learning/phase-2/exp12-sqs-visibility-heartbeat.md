# Phase 2 / Experiment 12 — SQS Visibility Heartbeat

## 一页结论

**PASS（E2：真实 Docker kill + PostgreSQL + LocalStack SQS）**

我把 SQS Visibility Timeout 设置为5秒，让 Worker A 执行一个超过20秒的可控
长任务，并将 Heartbeat 设置为1秒。健康窗口内，数据库 Lease 的 `ExpiresAt`
持续向后移动，SQS 消息始终保持不可见，Worker B 没有提前进入业务处理路径，
`ApproximateReceiveCount` 保持为1。

随后我使用真实 `docker kill` 非正常终止 Worker A。最后一次 Visibility 到期后，
Worker B 收到相同逻辑 MessageId，`ApproximateReceiveCount=2`，取得新的 Lease 和
Fencing Token，并完成任务。

```text
健康 20 秒：Lease Heartbeat 样本=20，ReceiveCount=1，Worker B 未进入
docker kill Worker A
约 5.6 秒后：Worker B 收到同一 MessageId，ReceiveCount=2
最终：Contribution/JobRun Succeeded，Inbox=1，Provider effect=1
Queue=empty，DLQ=empty，DeadLetter=0
```

## 实验信息

- 日期：2026-08-10
- 测试目录：`tests/Reliant.Tests/Integration/Phase2/Exp12/`
- 主测试：`HealthyLongTask_ShouldRenewLeaseAndVisibility_ThenRedeliverAfterKill`
- 失败测试：`VisibilityFailures_ShouldBeClassifiedLogged_AndStopHeartbeat`
- Worker A：真实 .NET Worker Docker 容器
- Worker B：真实 WorkerHost
- 数据库：PostgreSQL 17 Testcontainer
- Queue：LocalStack 3 SQS
- Visibility Timeout：5秒
- Lease：4秒
- Heartbeat：1秒（失败分类测试为500ms）
- Worker A Provider Delay：60秒
- 健康观察窗口：20秒
- Exp12：2/2 passed

## 为什么原实现不够

Exp5 已经实现数据库 Lease Heartbeat，但旧 Heartbeat 只做：

```text
UPDATE Lease.ExpiresAt
```

SQS 的初始 Visibility Timeout 不会因为数据库更新而自动延长。一个20秒任务如果
只设置5秒 Visibility，会出现：

```text
Worker A 仍健康处理
→ 第5秒 SQS Message 再次可见
→ Worker B 提前 Receive
→ 增加无意义 Attempt / ReceiveCount
→ 严重时过早进入 DLQ
```

旧实现还有两个问题：

1. Lease 续约只按 LeaseId 更新，不检查 Active、Expiry 和 Fencing Token；
2. Heartbeat 的所有异常都被空 `catch` 吞掉，没有日志，也无法判断为何停止。

因此修复前不是测试覆盖不足，而是生产存活协议缺少 Queue 的另一半。

## 修复设计

### 1. Queue Adapter 增加 Visibility 续约抽象

`IQueueAdapter.RenewVisibilityAsync` 隔离具体 Broker SDK。SQS Adapter 使用当前
delivery 的 Queue URL 和 ReceiptHandle 调用：

```text
ChangeMessageVisibility(VisibilityTimeoutSeconds)
```

Application 和 Worker 不直接依赖 AWS SDK。

### 2. Lease Heartbeat 绑定当前 Fence

数据库续约改成条件更新：

```text
LeaseId = 当前 Lease
JobRunId = 当前 Job
FencingToken = 当前 Token
IsActive = true
ExpiresAt > heartbeatAt
```

只有影响1行才表示当前 Worker 仍是有效 Owner。过期或已经被接管的 Worker 无法
靠迟到的 Heartbeat 复活旧 Lease。

### 3. 每个周期先 Lease、再 SQS

每个 Heartbeat 周期执行：

```text
1. 条件续约数据库 Lease
2. Lease 成功后 ChangeMessageVisibility
3. 两者成功后进入下一周期
```

PostgreSQL 和 SQS 之间不存在共享事务，所以不能声称二者原子提交。先验证并续约
Lease 可以避免已经失去数据库所有权的 Worker 继续隐藏 Queue Message。

如果 SQS 续约失败，本周期数据库 Lease 可能已经向后延长一次；Heartbeat 随即
停止，Lease 和 Message 都会在各自最后一次成功续约后自然到期。恢复可能稍有
延迟，但不会静默丢失消息。

### 4. Heartbeat 失败结构化记录

Visibility 续约错误分为：

```text
InvalidReceiptHandle
RateLimited
Timeout
TransientServiceFailure
PermanentFailure
```

Warning 日志包含 MessageId、JobRunId、LeaseId、FencingToken、FailureKind 和
IsTransient，但不记录敏感的 ReceiptHandle。未知数据库或运行时异常记录 Error。

### 5. 配置安全检查

Worker 启动时检查 Heartbeat Interval 是否短于 Lease 和 Visibility。配置不安全
时记录 Warning。SQS Visibility 被限制在合法的1–43200秒范围。

## 学生视角：中间过程

### 第一次 Review：发现数据库和 Queue 是两套时钟

我一开始看到已有 `HeartbeatLoop`，以为长任务续约已经完成。继续追踪后发现它只
更新 PostgreSQL：

```text
Lease ExpiresAt 在移动
SQS Visibility 截止时间完全没变
```

这让我理解到 Lease 和 Visibility 不能互相替代：

```text
Lease：应用数据库判断谁有资格提交
Visibility：Broker 判断消息什么时候可以再次 Receive
```

### 第二次 Review：旧 Lease 续约条件过宽

旧 `RenewAsync(leaseId)` 即使 Lease 已失效也可能执行更新。Exp11 已经建立了
Fencing Token，因此 Heartbeat 也必须携带同一个 Fence，不能成为旧 Owner 绕过
Fencing 的特殊写路径。

### 第一次 Build：发现测试 Adapter 的接口连带修改

Queue Adapter 增加方法后，Exp1、Exp2、Exp3 和两个共享测试 Adapter 需要实现
同一接口。这些修改只把调用委托给内部真实 Adapter；没有改变原实验逻辑，也
没有增加测试开关。

### 第一次 Exp12 运行：2/2 PASS

主场景实际输出：

```text
HEALTHY | DurationSeconds=20 | VisibilitySeconds=5 |
HeartbeatMs=1000 | LeaseHeartbeatSamples=20 |
ReceiveCount=1 | WorkerBEntered=false

CRASH | WorkerA=docker-kill |
KilledAt=2026-08-10T11:04:45.0730505Z |
RedeliveredAt=2026-08-10T11:04:50.6837611Z |
ReceiveCount=2

FINAL | Contribution=Succeeded | Inbox=1 | JobAttempts=2 |
Tokens=1,2 | ProviderEffects=1 | DeadLetters=0 |
Queue=empty | DLQ=empty
```

失败路径实际输出：

```text
InvalidReceiptHandle=InvalidReceiptHandle | Transient=False
RateLimitLog=true | RenewalAttempts=1 | HeartbeatStopped=true
```

无效 ReceiptHandle 使用真实 LocalStack/SQS 调用验证。SQS 限流难以稳定地由
LocalStack 制造，所以使用只存在于 Exp12 测试中的 Queue Adapter 注入
`RateLimited` 异常；Worker 的日志和停止 Heartbeat 行为仍走真实生产代码。

### 第一次全量回归：160/161 FAIL

Exp12 单独和 Phase 2 回归都通过，但第一次全量并行运行时，Worker B 在 kill 前
Receive 过一次消息。全量测试同时启动大量 PostgreSQL/LocalStack 容器，5秒实验
窗口暴露了两个问题：

1. 原 Heartbeat Loop 启动后先等待1秒，Lease 获取后的第一次 Queue 续约不够早；
2. 这种精确依赖5秒时钟的实验与十余个容器并行运行，会把测试机资源争用混入
   “健康 Broker”假设。

修复后，Heartbeat 在取得 Lease 后立即执行第一次联合续约，后续才按1秒间隔；
Exp12 被放入禁用并行的 xUnit Collection，确保故障条件只有实验主动注入的
`docker kill` 或续约异常，而不是测试机 CPU/容器争用。

### 第二次全量回归：161/161 PASS

```text
Build：0 errors
Exp12：2/2 passed
Phase 2 Exp1–Exp12：15/15 passed
Full suite：161/161 passed，0 failed，0 skipped
```

## PASS 条件逐项判定

| PASS 条件 | 证据 | 判定 |
| --- | --- | --- |
| 健康长任务不提前 Redelivery | 20秒内 ReceiveCount=1，Worker B 未进入 | PASS |
| Lease 持续续约 | 20个 LastHeartbeat/ExpiresAt 样本持续前移 | PASS |
| SQS Visibility 持续续约 | 4个初始 Timeout 后消息仍 NotVisible | PASS |
| Crash 后最终 Redelivery | docker kill 后约5.6秒 ReceiveCount=2 | PASS |
| 最终完成且副作用不重复 | Inbox=1、Provider effect=1、Reference=1 | PASS |
| Heartbeat 失败可观察 | Invalid Receipt 分类 + RateLimit 结构化 warning | PASS |
| 不过早进入 DLQ | 最终 DLQ=empty、DeadLetter=0 | PASS |

## 最终状态

| 检查项 | 实际值 |
| --- | --- |
| 健康窗口 | 20秒 |
| DB Heartbeat distinct | 20 |
| 健康期 ApproximateReceiveCount | 1 |
| Worker B 健康期业务进入 | 0 |
| Crash 后 ApproximateReceiveCount | 2 |
| JobRun FencingToken | 2 |
| JobAttempt | Abandoned Token1 + Succeeded Token2 |
| Contribution | Succeeded |
| Inbox | 1 |
| Provider effect / Reference | 1 / 1 |
| DeadLetter | 0 |
| Processing Queue / DLQ | empty / empty |

## 代码影响与必要性

| 修改 | 原因 |
| --- | --- |
| Queue Visibility 抽象 | Worker 不泄漏 AWS SDK，支持不同 Broker 实现 |
| SQS ChangeMessageVisibility | 真正延长当前 delivery 的隐藏时间 |
| Fence-aware Lease Renew | 防止过期 Owner 通过 Heartbeat 复活 |
| Worker 联合 Heartbeat | 同时维持数据库所有权和 Broker delivery |
| 失败分类与日志 | ReceiptHandle、限流、超时不再静默 |
| ReceiveCount 日志 | 审计是否发生提前 Redelivery |
| Fixture 配置参数 | 实验可设置5秒 Visibility、1秒 Heartbeat和长任务 |
| 旧测试 Adapter 委托 | 新接口的编译兼容，不改变旧实验行为 |

本实验不需要数据库 Migration，也没有修改 Contribution、Provider 或状态机业务
规则。生产修改集中在 Queue Adapter、Lease Repository 和 Processing Worker。

## 当前限制

1. PostgreSQL Lease 与 SQS Visibility 无法跨系统原子续约；实现选择安全顺序和
   最终到期恢复，而不是伪装成分布式事务。
2. 成功 Heartbeat 使用 Debug 日志，避免生产环境每个任务每秒产生大量 Info；
   正式生产指标和 Dashboard 仍在 Phase 3 完成。
3. RateLimit 使用确定性异常注入，因为 LocalStack 不提供稳定的 SQS throttling
   控制面；真实 AWS E4 Smoke 仍需验证 AWS 返回码和权限。
4. Visibility Timeout、Lease 和 Heartbeat 参数目前由配置管理；Phase 3 需要增加
   配置校验 Gate、告警和容量基线。
5. Exp12 的5秒时序实验禁用测试并行；这是为了隔离实验变量，不代表生产 Worker
   依赖单实例运行。生产 Worker 的多实例并发由同一 E2E 中的 Worker A/B 验证。

## 验证命令

```powershell
dotnet build Reliant.slnx -c Release --no-restore

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build `
  --filter "FullyQualifiedName~Integration.Phase2.Exp12" `
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
Exp12: 2/2 passed
Phase 2 Exp1–Exp12: 15/15 passed
Full suite: 161/161 passed
```

## 我的学习结论

可靠长任务的存活协议不是一个 Heartbeat，而是两套独立系统的期限管理：

```text
数据库 Lease Heartbeat
+ SQS Visibility Heartbeat
+ Fencing Token
= 健康 Worker 不被提前抢占，失效 Worker 最终可恢复，旧 Owner 无法提交
```

Heartbeat 的目标不是让任务永远不可见，而是在 Worker 健康时延长所有权；一旦
续约停止，系统必须依靠有限期限恢复。这也是为什么失败后应停止续约并记录原因，
而不是无限吞错或永久隐藏消息。
