# Phase 3 / Experiment 10 — Concurrent Reconciliation

## 一页结论

**PASS（E2：两个独立 DbContext + PostgreSQL）**

使用 Provider query 双执行者屏障，确保两个 Reconciliation 执行者都在任何一方提交前
读取同一个 `ReconciliationPending / Version=0` 快照。聚合测试复现两个恢复分支：

```text
NotFound  -> SafeRetry / RetryPending
Succeeded -> AutoFixed / Succeeded
```

每个场景 Provider Query 都真实发生 2 次，但数据库只接受 1 个恢复动作：

```text
StateTransition = 1
ReconciliationRecord = 1
Contribution.Version = 1
loser = Concurrent reconciliation already applied
```

SafeRetry 场景只有一个 `NextRetryAt`；Succeeded 场景只有一个 ProviderReference，没有异常、
双写或非法状态跳转。

## 实验信息

- 日期：2026-08-11
- 测试目录：`tests/Reliant.Tests/Integration/Phase3/Exp10/`
- 测试类：`ConcurrentReconciliationTests`
- 数据库：PostgreSQL 17 Testcontainer
- 执行者：每个场景两个独立 DI Scope / DbContext
- 同步方式：Provider status query 双执行者屏障
- 场景：NotFound SafeRetry、Succeeded AutoFix
- Exp10：1/1 passed（一个测试聚合两个场景）

## 假设

```text
两个 Reconciliation Worker 可以同时读到旧状态
Provider query 可以重复发生
恢复写入必须只有一个 winner
loser 必须安全退出，不能留下部分数据
```

## 为什么需要确定性屏障

原有用例使用：

```text
taskA = senderA.Send(...)
taskB = senderB.Send(...)
await Task.WhenAll(taskA, taskB)
```

这只表示两个 Task 同时存在，并不能证明两边都在 winner 提交前读取了旧 Version。调度器
可能让 A 完整结束后 B 才读取数据库，此时只是顺序幂等，不是真正的写入竞态。

新测试的 Provider 在 status query 处等待两个调用都到达：

```text
Scope A: load Version=0 -> provider query --\
                                         barrier -> both released
Scope B: load Version=0 -> provider query --/
```

`ReconcileContributionHandler` 在 Provider query 前已经读取 Contribution、现有
ReconciliationRecord、ProviderReference 和 ProcessingAttempt。因此两个 query 都到达屏障，
就证明两个独立 DbContext 都持有同一个旧业务快照。

## 学生视角：中间过程

### 第一次 Review：旧测试方向正确，但证据不够强

旧测试已经断言 SafeRetry 最终只有一个 Transition 和一个 Record，说明生产代码有并发保护。
但我发现两个不足：

1. 没有屏障，`Task.WhenAll` 可能退化为顺序执行；
2. 它只走 NotFound，ProviderReference 最终为 0，无法真正证明并发创建 Reference 不重复。

所以我删除旧测试，替换为一个放在 `Phase3/Exp10/` 的聚合测试。测试数量保持不变，
同时加入 SafeRetry 与 terminal Succeeded 两条路径。

### 场景一：NotFound -> SafeRetry

两个执行者都查询相同 ProviderIdempotencyKey，并同时得到 NotFound。二者都尝试计算：

```text
ReconciliationPending -> RetryPending
NextRetryAt = now + backoff
ReconciliationRecord = SafeRetry
```

实际结果：

```text
Provider Query = 2
results = SafeRetry + Concurrent reconciliation already applied
Contribution = RetryPending / Version=1
StateTransition = 1
ReconciliationRecord = 1 / SafeRetry
NextRetryAt = one effective value
ProviderReference = 0
```

RetryCount 保持 0，因为 Reconciliation 只是证明“可以安全重试”并安排下一次时间，真正 Provider
attempt 尚未开始，不应提前消耗 Retry Budget。

### 场景二：Succeeded -> AutoFixed

两个执行者同时得到同一个 ProviderReference，并都尝试：

```text
ReconciliationPending -> Succeeded
ReconciliationRecord = AutoFixed
insert ProviderReference
```

实际结果：

```text
Provider Query = 2
results = AutoFixed + Concurrent reconciliation already applied
Contribution = Succeeded / Version=1
StateTransition = 1
ReconciliationRecord = 1 / AutoFixed
ProviderReference = 1
NextRetryAt = null
```

loser 没有留下第二条 Transition、Record 或 ProviderReference，说明冲突事务整体回滚，不是只
保护 Contribution 单表。

## 生效的并发保护

```text
Contribution.Version is concurrency token
              +
Contribution + StateTransition + ReconciliationRecord
             + ProviderReference in one SaveChanges
              |
              +-- winner commits all
              +-- loser gets DbUpdateConcurrency/constraint conflict
                    -> TrySaveChanges returns false
                    -> safe idempotent result
```

关键点是原子边界。如果 StateTransition 或 ProviderReference 在独立事务提前提交，Contribution
的 Version 冲突也无法撤回它们；当前实现将恢复动作放在同一个 `SaveChanges`，所以 loser
不会留下半成品。

## PASS 条件逐项判定

| PASS 条件 | SafeRetry | Succeeded | 判定 |
| --- | --- | --- | --- |
| 只有一个有效状态转换 | 1 | 1 | PASS |
| 一个 Retry Schedule 或一个终态 | NextRetryAt=1 | Succeeded=1 | PASS |
| 无重复 ProviderReference | 0 | 1 | PASS |
| 无非法状态跳转 | Pending→RetryPending | Pending→Succeeded | PASS |
| 另一个执行者安全退出 | concurrent-applied | concurrent-applied | PASS |

## 最终数据

| 数据 | SafeRetry 场景 | Succeeded 场景 |
| --- | --- | --- |
| Provider Query | 2 | 2 |
| Contribution | RetryPending / Version=1 | Succeeded / Version=1 |
| StateTransition | 1 | 1 |
| ReconciliationRecord | 1 / SafeRetry | 1 / AutoFixed |
| NextRetryAt | 1 个有效值 | null |
| ProviderReference | 0 | 1 |
| Outbox / DeadLetter | 0 / 0 | 0 / 0 |
| 未处理异常 | 0 | 0 |

## 业务代码与测试聚合 Review

```text
生产代码修改：0
数据库 Migration：0
删除：ReconciliationClosureTests 中1条非确定性并发用例
新增：Exp10 中1条确定性双场景聚合用例
测试总数净变化：0
```

现有业务代码已经具备本实验所需的正确语义，因此没有增加分布式锁、Reconciliation lease
或额外唯一索引。对当前单个 Contribution 的恢复写入而言，乐观并发和事务原子性已经能让
一个执行者获胜、另一个安全退出。

## 当前限制

1. `UnitOfWork.TrySaveChangesAsync` 当前把所有 `DbUpdateException` 映射为 `false`。本实验
   能证明已知并发 loser 安全，但生产观测应区分 concurrency/unique conflict 与 schema、
   connection 等意外数据库错误，避免错误分类过宽。
2. Provider status query 允许重复；本实验要求 Provider query 本身是只读且幂等的。若供应商
   把查询设计成有副作用，需要 Adapter 额外隔离。
3. `Pending/Unavailable` 这类不推进 Contribution Version 的观察记录可能由两个扫描器各写一条；
   它们属于多次观察审计，不是本实验定义的重复恢复动作。
4. 这里验证两个独立 DbContext 共享 PostgreSQL；多进程/跨节点网络抖动将在 E3/E4 部署实验
   中补充，但数据库裁决语义相同。

## 验证命令

```powershell
dotnet build tests/Reliant.Tests/Reliant.Tests.csproj -c Release --no-restore

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build `
  --filter "FullyQualifiedName~Integration.Phase3.Exp10" `
  --logger "console;verbosity=detailed"

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build
```

## 我的学习结论

“启动两个 Task”不等于“复现并发”。可靠的并发实验必须证明两个执行者都跨过同一个读取
边界，再让它们争夺提交。

对于 Reconciliation，Provider 查询重复通常可以接受；必须严格唯一的是后续业务恢复动作：

```text
read may happen twice
query may happen twice
business recovery commits once
```

这正是乐观并发和单事务原子提交共同提供的语义。
