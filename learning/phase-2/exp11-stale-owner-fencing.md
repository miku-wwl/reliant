# Phase 2 / Experiment 11 — Stale Owner Fencing

## 一页结论

**PASS（E2：真实 Docker pause/unpause + PostgreSQL + LocalStack SQS）**

我让 Worker A 取得 Job 的第一个 Lease，并在 Provider 调用中使用 `docker pause`
冻结整个进程。暂停后数据库 Lease Heartbeat 停止，Worker B 扫描到过期 Lease，
以更大的 Fencing Token 接管并完成任务。

```text
Worker A：Lease Token=1
Worker B：Lease Token=2
Worker B：Contribution/JobRun Succeeded，消息 ACK
Worker A 恢复：Token=1 条件锁定失败，AffectedRows=0
Worker A：不写 Inbox、不覆盖 JobRun、不 ACK
```

最终只有一个业务结果和一次 Provider effect。两个 ProcessingAttempt 使用同一个
稳定 Provider Idempotency Key。

## 实验信息

- 日期：2026-08-03
- 测试目录：`tests/Reliant.Tests/Integration/Phase2/Exp11/`
- 测试：`PausedOwner_ShouldBeFencedAfterLeaseTakeover`
- Worker A：真实 .NET Worker Docker 容器
- Worker B：真实 WorkerHost
- 数据库：PostgreSQL 17 Testcontainer
- Queue：LocalStack 3 SQS
- Visibility Timeout：2 秒
- Lease：5 秒
- Worker A Provider：15 秒延迟后 `TimeoutBeforeProcessing`
- Exp11：1/1 passed
- Phase 2 Exp1–Exp11：13/13 passed
- 全量：159/159 passed

## 为什么原实现不够

Exp5 已经证明：

```text
同一时刻最多一个 Active Lease
Lease 过期后 Worker B 可以接管
```

但 Active Lease 唯一约束只控制“谁能开始”，无法控制已经开始执行的旧进程。
如果 Worker A 只是暂停，它仍保留内存中的 Contribution、JobAttempt 和 Provider
结果；恢复后原实现没有世代号可以证明它已经失去所有权。

原模型中不存在：

```text
JobRun.FencingToken
Lease.FencingToken
JobAttempt.FencingToken
提交时的 Token 条件锁定
```

因此 Exp11 在修复前属于设计级 FAIL：测试要求的 Token=N/N+1 和
`AffectedRows=0` 根本没有可观察字段或数据库操作。

## 修复设计

### 1. JobRun 保存单调递增 Token

JobRun、Lease 和 JobAttempt 新增 `FencingToken bigint`。旧数据迁移后为0，
第一次成功领取获得1，下一次接管获得2。

### 2. Token 分配与 Lease 领取原子执行

`LeaseRepository.TryAcquireAsync` 使用一个 PostgreSQL CTE：

```text
锁定 JobRun
→ 计算 FencingToken + 1
→ 尝试插入 Active Lease
→ 只有插入成功才更新 JobRun Token
```

如果 Active Lease 冲突，Token 不会空增。原 Exp5 两个并发 contender 测试仍然
确认只有一个 winner。

### 3. 提交时锁定当前 Owner

每个关键数据库提交都在一个事务内执行：

```text
SELECT JobRun + Lease FOR UPDATE
WHERE JobRun.Token = 我的 Token
  AND Lease.Token = 我的 Token
  AND Lease.IsActive
  AND Lease.ExpiresAt > now

匹配成功 → 保存业务状态并提交
匹配失败 → Rollback + StaleJobOwnerException
```

锁定 JobRun 和 Lease 后，Scanner 释放 Lease 或新 Owner 增加 Token 必须等待当前
事务结束，避免“先检查、后提交”之间再次产生竞态。

受保护的提交包括：

- Contribution 进入 Processing；
- Provider 前 Pending Attempt；
- Provider 返回后的 Attempt/Reference；
- Contribution、Inbox、JobAttempt、JobRun 最终提交；
- terminal/idempotent ACK 前的数据库提交。

### 4. Stale Owner 不做补偿写入

Worker 捕获 `StaleJobOwnerException` 后只记录结构化 warning：

```text
WorkerId
JobRunId
LeaseId
FencingToken
message left unacknowledged
```

它不会尝试把已经由 Scanner 标为 Abandoned 的 Attempt 改回其他状态，也不会
ACK。消息已经由 Worker B 使用新的 receipt handle 完成。

### 5. Fencing 不替代 Provider Idempotency

Token 只能阻止数据库提交，不能撤销已经发生的外部调用。实验让 Worker A 使用
`TimeoutBeforeProcessing`，确保 A 没有 Provider effect；Worker B 产生唯一 effect。

同时核对两个 ProcessingAttempt 的 Provider Idempotency Key 完全相同。对于
“A 已经被 Provider 处理后才暂停”的情况，仍必须由真实 Provider 的全局幂等键
抑制第二次外部副作用；Fencing 不能代替这条契约。

## 学生视角：中间过程

### 第一次 Review：设计级 FAIL

我最初以为 Exp5 的 Active Lease 唯一索引已经足够。反查提交路径后发现，Lease
只能阻止 Worker B 在 A 有效时领取，不能让恢复后的 A 忘记内存中的旧工作。

这让我区分了：

```text
Lease = 当前谁有资格工作
Fencing Token = 过期 Worker 的提交是否还有效
```

### 第一次 Exp11 运行：PASS

完成 Token、原子领取和 fenced commit 后，真实 pause/takeover/resume 场景通过：

```text
WORKER A | Token=1 | Attempt=1/Running | ProviderAttempt=Pending

TAKEOVER | LeaseExpired=true | WorkerBToken=2 |
TokenStrictlyIncreased=true | JobStatus=Succeeded

FENCE | StaleToken=1 | ConditionalMatch=false |
AffectedRows=0 | AckedByStaleOwner=false

FINAL | Contribution=Succeeded | Inbox=1 | JobAttempts=2 |
Tokens=1,2 | ProviderAttempts=2 | StableProviderKeys=1 |
ProviderEffects=1 | References=1 | DeadLetters=0
```

### 第一次全量回归：FAIL

15个直接测试 `SubmitToProviderCommand` 的旧测试没有启动 Worker，也没有注册
Lease Repository。Fencing 依赖被错误地设成 Handler 必选构造依赖，导致 DI
创建失败。

修复后，Lease Repository 只在 Command 携带 `ExecutionFence` 时强制要求：

- Worker 调用：必须有 Fence，缺少 Repository 立即失败；
- 独立 Provider 测试：没有 Fence，保持原有调用方式。

受影响的18个 Provider/Circuit/Recovery 测试随后全部通过。

### 第二次全量回归：FAIL

一个 Provider 并发测试暴露了既有 race：数据库唯一约束正确拒绝重复
ProviderReference，但 loser 又尝试保存同一个冲突实体。

成功结果落库改为 `TrySaveChanges` 后：

```text
winner → 提交 ProviderReference
loser → 识别并发 winner，返回幂等成功，不重复保存冲突实体
```

该并发测试连续运行5轮全部通过。

## 最终状态

| 检查项 | 实际值 |
| --- | --- |
| JobRun FencingToken | 2 |
| JobAttempt 1 | Abandoned，Token=1 |
| JobAttempt 2 | Succeeded，Token=2 |
| Lease 历史 | 2，Token=1/2，最终均 inactive |
| Stale 条件匹配 | false / AffectedRows=0 |
| Contribution | Succeeded |
| Inbox | 1 |
| ProcessingAttempt | 2：Pending + Succeeded |
| Provider 幂等键 distinct | 1 |
| Provider effect | 1 |
| ProviderReference | 1 |
| StateTransition | 3 |
| DeadLetter | 0 |
| Queue | empty |

第一个 ProcessingAttempt 保持 Pending 是保守审计：A 在 Provider 调用期间被暂停，
过期 Owner 不允许回来改写它。B 使用相同幂等键完成第二个 Attempt。Pending/历史
Attempt 的 retention 与告警仍属于后续生产准备策略。

## PASS 条件逐项判定

| PASS 条件 | 证据 | 判定 |
| --- | --- | --- |
| Token 严格递增 | Worker A=1，Worker B=2 | PASS |
| 过期 Owner 无法提交 | Token1 条件匹配 false，AffectedRows=0 | PASS |
| 只有 Worker B 结果有效 | JobAttempt2/Contribution/JobRun Succeeded | PASS |
| 数据库副作用一份 | Inbox=1、Reference=1、Transitions=3 | PASS |
| Provider 副作用一份 | A=TimeoutBeforeProcessing，B Effect=1，相同稳定键 | PASS |
| 拒绝原因可审计 | Attempt1 Abandoned + Fence warning 含 Job/Lease/Token | PASS |

## 代码影响与必要性

| 修改 | 原因 |
| --- | --- |
| JobRun/Lease/JobAttempt Token | 持久化 Owner 世代和审计 |
| 原子 Lease + Token CTE | 避免并发领取造成 Token 空增或双 Owner |
| Transaction + `FOR UPDATE` Fence | 消除检查与提交之间的竞态 |
| Provider Command 携带 Fence | Provider 前后持久化也必须拒绝旧 Owner |
| Worker fenced saves | Contribution/Inbox/Job 状态不能被旧 Owner 覆盖 |
| 数据库迁移 | 现有数据从 Token=0 安全升级 |
| Exp3 permissive fake 小幅更新 | 它刻意绕过 Lease 来测试 Version race，必须实现新增接口 |
| ProviderReference loser 收敛 | 全量回归暴露的真实并发缺口 |

这次生产修改是必要的正确性修复，不是为了让测试通过而增加测试开关。

## 当前限制

1. 本实验使用 Sandbox Provider 的 `TimeoutBeforeProcessing`，证明本场景只有一次
   effect；真实跨实例 Provider 的全局幂等仍需 E4 契约/Smoke 验证。
2. Fencing Token 保护当前 Contribution Processing Handler；未来新增 Handler
   必须使用相同 fenced commit 边界。
3. 历史 Pending/Abandoned Attempt 的 retention、容量指标和清理告警仍在 Phase 3
   生产准备实验中完成。
4. Exp12 的 SQS Visibility Heartbeat 仍未实现；数据库 Lease Heartbeat 不能替代
   Queue Visibility 续约。

## 验证命令

```powershell
dotnet build Reliant.slnx -c Release --no-restore

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build `
  --filter "FullyQualifiedName~Integration.Phase2.Exp11" `
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
Exp11: 1/1 passed
Phase 2 Exp1–Exp11: 13/13 passed
Full suite: 159/159 passed
```

仓库既存 package vulnerability、obsolete API 和未使用参数 warning 未被伪装成
已处理。

## 我的学习结论

Lease、Heartbeat 和 Fencing Token 是三个不同层次：

```text
Lease：声明当前 Owner
Heartbeat：延续健康 Owner 的有效期
Fencing Token：拒绝已经过期但后来恢复的旧 Owner
```

只做 Lease expiry 能恢复任务，却不能阻止“僵尸 Worker”回来提交。真正的 takeover
必须让每次 Owner 世代单调递增，并让数据库在提交瞬间判断 Token，而不是只依靠
应用进程中的一次 if 检查。
