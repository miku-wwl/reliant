# Reliant Phase 3 Gate Review & Learning Checklist

> 用途：Phase 3（External Provider Reliability、Unknown Outcome、Reconciliation、Callback、Safe Retry）代码实现完成后，由 Owner 主导学习、验证和签发 Gate。  
> 原则：Agent 可以实现代码和测试，但不能替你判断 Provider 副作用、状态收敛和故障恢复是否真的可靠。  
> 进入 Phase 4 Observability 前，必须完成本文件中的核心学习、实验、证据和口头验收。

> 2026-08-11 Engineering Audit：15/15 实验均可发现并复验；详细完成度见
> `learning/phase-2-3-3.1-completion-audit.md`。Phase 4 信号定义已冻结在
> `learning/phase-3/observability-contract.md`；运行时 OTel/Dashboard 未提前实现。
> Owner 口试、画图和 Gate 签字仍必须由 Owner 本人完成。

---

## 1. 当前目标

Phase 3 的目标不是“把 Provider、Callback 和 Reconciliation 接起来”，而是证明：

```text
Provider 请求结果未知时，系统不会盲目 Retry
Provider 已成功但响应丢失时，不会产生第二次业务效果
Provider 明确 NotFound 后，可以安全 Retry
Callback 重复或乱序时，不会覆盖正确终态
两个 Worker 或 Reconciliation Worker 并发时，只会有一个有效收敛结果
Circuit Open 时不会继续压垮 Provider，也不会错误 ACK 消息
Worker 在任意关键点崩溃后，系统最终可以恢复到可解释状态
```

Phase 3 的核心不变量：

```text
At-least-once Message Delivery
+
Worker Crash
+
Provider Result Uncertainty
+
Duplicate / Out-of-order Callback
+
Concurrent Recovery
=
At-most-one Provider Business Effect
+
Eventually Consistent Local State
+
Complete Audit Evidence
```

Phase 3 的核心链路：

```text
Worker
→ Persist ProcessingAttempt
→ Submit to Provider
→ Provider Operation / ProviderReference
→ Success / Failure / Unknown
→ Reconciliation
→ Safe Retry or State Convergence
→ Callback
→ Final State
```

---

## 2. 立即停止 Phase 4 新功能

在进入 Phase 4 前：

- [x] 停止增加 OpenTelemetry、Dashboard、SLI/SLO 和 k6 Gate
- [x] 停止扩展新的 Phase 3 Scope
- [x] 确认 Phase 3.1 实现已冻结
- [x] 确认所有 Phase 3.1 代码已通过 CI
- [x] 确认当前 Phase 3 Evidence Pack 与代码一致
- [x] 创建 Phase 3 Owner 学习与实验目录
- [ ] 由 Owner 亲自完成关键实验
- [ ] 由 Owner 决定 `ACCEPT / VALIDATION / BLOCKED`

推荐学习分支：

```bash
git switch -c learning/phase-3-owner-validation
```

注意：

```text
这个分支主要用于实验记录、注释、脚本和学习 Evidence。
不要为了学习再次大规模改写已通过 Gate 的核心代码。
```

---

## 3. 让 Agent 生成 Phase 3 Review Packet

将下面的提示词直接交给 Agent：

```text
停止增加 Phase 4 功能。

请为当前 Reliant Phase 3 生成 Owner Review Packet，
但不要自行签发 Owner Knowledge Gate。

需要输出：

1. 当前已经完成的 Phase 3 Scope；
2. 尚未完成、仅为 Skeleton 或属于 Phase 4 的内容；
3. Provider Submit、ProcessingAttempt、ProviderReference、
   Reconciliation、Retry Scheduler、Callback、Circuit Breaker 的代码地图；
4. Contribution 状态机和所有 Phase 3 状态转换；
5. ProviderIdempotencyKey 的完整生命周期；
6. 每一个外部调用前后的数据库持久化点；
7. Provider Result 分类：
   - Succeeded
   - Failed
   - Pending
   - NotFound
   - ProviderUnavailable
   - Unknown
8. 每个 Phase 3 Failure Scenario 的：
   - 故障注入方法
   - 运行命令
   - 预期结果
   - 实际结果
   - Evidence 路径
9. Callback Security、Dedup 和 Ordering 的调用链；
10. Retry Budget、Backoff、Scheduler Claim 和 Max Attempts；
11. Circuit Open、Half-Open 和消息 ACK 行为；
12. 当前测试列表和测试类型；
13. 与 Phase 3 Final Gate 的逐项映射；
14. Known Limitations；
15. Open Risks。

禁止：
- 自行声明 Owner 已经掌握；
- 只给最终结论，不给代码路径和证据；
- 用 Unit Test 代替需要 PostgreSQL、LocalStack 或 WorkerHost 的实验；
- 把 Timeout 统一解释为 Retryable Failure；
- 声称 Provider Exactly-once Delivery；
- 开始 Phase 4。
```

---

# 4. Phase 3 必须掌握的知识

## 4.1 Provider 外部副作用与事务边界

### 必须理解

数据库事务只能管理 PostgreSQL，不能回滚 Provider 已经产生的外部业务效果。

危险窗口：

```text
Provider 已完成业务操作
→ Worker 尚未把结果写入 PostgreSQL
→ Worker 崩溃
→ SQS 消息重新投递
```

如果第二次 Submit 使用新的业务 Key：

```text
Provider 可能再次执行
→ 重复扣款 / 重复捐款 / 重复外部操作
```

### 必须在代码中找到

- [ ] `SubmitToProviderCommand`
- [ ] Provider 调用发生在哪一行
- [ ] Provider 调用前已经持久化哪些数据
- [ ] Provider 调用后写入哪些数据
- [ ] Provider 调用是否被长数据库事务包围
- [ ] Provider 返回后是否重新加载 Contribution
- [ ] Provider 结果如何映射到本地状态

### 核心问题

1. 为什么数据库事务不能保护 Provider 调用？
2. Provider 成功后、本地 Commit 前崩溃会发生什么？
3. 为什么不能只依赖 Inbox 防止重复 Provider Effect？
4. Provider 调用前必须保存什么证据？

---

## 4.2 ProcessingAttempt

### 必须理解

`ProcessingAttempt` 是一次 Provider 交互的持久化证据。

它通常记录：

```text
ContributionId
AttemptNumber
ProviderName
ProviderIdempotencyKey
StartedAt
CompletedAt
Status
ErrorCategory
ErrorMessage
ProviderReference
Request / Response Metadata
```

它的作用：

```text
Worker 重启后仍然知道调用发生过
Reconciliation 可以找到 Provider Key
审计可以解释每次尝试
Retry 可以复用稳定业务 Key
```

### 必须在代码中找到

- [ ] Attempt 在 Provider 调用前创建
- [ ] Attempt 是否在 Provider 调用前 Commit
- [ ] AttemptNumber 如何生成
- [ ] `(ContributionId, AttemptNumber)` 是否有唯一约束
- [ ] Unknown Attempt 如何记录
- [ ] Retry 后是否创建新的 Attempt
- [ ] 所有 Attempt 是否复用同一 ProviderIdempotencyKey

### 核心不变量

```text
Provider 调用发生前，本地必须已经存在可恢复的 Attempt 证据。
```

---

## 4.3 Stable Provider Idempotency Key

### 必须理解

Provider Idempotency Key 表达的是：

```text
同一个逻辑业务操作
```

不是：

```text
某一次网络尝试
```

因此同一 Contribution 的：

```text
初次 Submit
SQS Redelivery
Worker Restart
Scheduled Retry
Crash Recovery
```

必须使用同一个 Key。

Key 不能包含：

```text
AttemptNumber
当前时间
随机 UUID
WorkerId
SQS MessageId
```

推荐组成：

```text
ProviderName
OrganizationId
ContributionId
OperationType
KeyVersion
```

### 必须在代码中找到

- [ ] `IProviderOperationKeyFactory`
- [ ] Key 是否跨 Attempt 稳定
- [ ] 不同 Organization 是否得到不同 Key
- [ ] 同 Key 不同 Payload 如何处理
- [ ] Provider Operation Store 是否原子创建
- [ ] 并发 Submit 是否只产生一个 Provider Operation
- [ ] ProviderReference 是否有唯一约束

### 核心问题

1. 为什么 AttemptNumber 不能进入 Key？
2. 相同 Key、不同金额应该怎么处理？
3. `ConcurrentDictionary` 普通先查后写为什么仍然不安全？
4. 如何证明 Provider Operation 最多一个？

---

## 4.4 Error Classification

### 必须区分

#### Validation Failure

```text
请求格式错误
金额非法
缺少必需字段
```

通常：

```text
不 Retry
```

#### Definitive Business Failure

```text
Provider 明确拒绝
业务规则不允许
```

通常：

```text
进入 Failed
```

#### Retryable Infrastructure Failure

```text
明确未产生业务效果的临时故障
```

可以：

```text
进入 Retry 流程
```

#### Unknown Outcome

```text
请求已经发出
但无法确认 Provider 是否处理
```

不能：

```text
直接 Retry
```

### 必须在代码中找到

- [ ] `ErrorCategory`
- [ ] Exception 到 ErrorCategory 的映射
- [ ] 哪些异常进入 Unknown
- [ ] 哪些异常进入 Retryable
- [ ] 哪些错误计入 Circuit
- [ ] Validation / Business Failure 是否错误进入 Retry
- [ ] 未知异常是否被统一吞掉

### 核心问题

1. Timeout 是 Error Category 还是 Business Outcome？
2. HTTP 500 是否一定代表 Provider 没有处理？
3. 429、500、Timeout 是否应该使用相同策略？
4. 为什么不能 `catch Exception → Retry`？

---

## 4.5 Unknown Outcome

### 必须理解

Timeout 只证明：

```text
调用方没有得到可信响应
```

它不证明：

```text
Provider 没有处理
```

可能存在：

```text
请求尚未到达
Provider 正在处理
Provider 已成功
Provider 已失败
响应在网络途中丢失
```

因此推荐状态路径：

```text
Processing
→ ProviderUnknown
→ ReconciliationPending
```

### 两个状态的区别

```text
ProviderUnknown
→ 描述本次外部调用结果未知

ReconciliationPending
→ 描述系统选择通过对账来恢复
```

### 必须在代码中找到

- [ ] Unknown Attempt 如何保存
- [ ] `Processing → ProviderUnknown`
- [ ] `ProviderUnknown → ReconciliationPending`
- [ ] 是否写了两条独立 `StateTransition`
- [ ] 是否立即增加 RetryCount
- [ ] 是否立即重新 Submit
- [ ] Reconciliation 如何找到 Provider Key

### 核心不变量

```text
Unknown Outcome 不能被当成普通失败直接 Retry。
```

---

## 4.6 Timeout Before Processing

### 场景

```text
请求没有被 Provider 处理
→ Provider 未创建 Operation
→ Worker 收到 Timeout
```

正确流程：

```text
Processing
→ ProviderUnknown
→ ReconciliationPending
→ Query by IdempotencyKey
→ NotFound
→ RetryPending
→ Scheduled Retry
→ Processing
→ Succeeded
```

### 必须观察

```text
第一次 ProviderOperationCount == 0
第一次 Attempt == Unknown
Contribution == ReconciliationPending
Query Result == NotFound
Contribution == RetryPending
第二次 Attempt 使用相同 Key
最终 ProviderOperationCount == 1
```

### 核心问题

> 为什么第一次 Timeout 不能直接 Retry，但 Provider 返回 NotFound 后可以 Retry？

正确答案：

```text
第一次 Timeout 没有证明 Provider 未处理。
NotFound 是 Reconciliation 获得的新事实，
它改变了操作的安全性。
```

---

## 4.7 Processed but Response Lost

### 场景

```text
Provider 收到请求
→ 创建 ProviderOperation
→ 创建 ProviderReference
→ 保存 Succeeded
→ Response 丢失
→ Worker 收到 Timeout
```

正确流程：

```text
Attempt = Unknown
Contribution:
Processing
→ ProviderUnknown
→ ReconciliationPending

Reconciliation:
Query by IdempotencyKey
→ Provider Succeeded
→ 补写 ProviderReference
→ Contribution Succeeded
```

### 必须观察

```text
ProviderOperationCount == 1
ProviderReferenceCount == 1
UnknownAttemptCount == 1
Contribution.State == Succeeded
UnresolvedReconciliationCount == 0
DuplicateBusinessEffectCount == 0
```

### 必须检查状态审计

```text
Processing → ProviderUnknown
ProviderUnknown → ReconciliationPending
ReconciliationPending → Succeeded
```

必须是三条记录。

### 核心问题

1. Provider 为什么必须先保存 Operation，再抛 Timeout？
2. 为什么不能让 SandboxProvider 先抛 Timeout 再保存结果？
3. 为什么没有 ProviderReference 时仍然可以对账？
4. 重复消息到来时为什么不会再次产生 Provider Effect？

---

## 4.8 Reconciliation

### 本质

Reconciliation 不是 Retry。

它负责：

```text
比较本地事实和 Provider 事实
→ 选择正确恢复动作
→ 让状态最终收敛
```

### 必须掌握的决策表

| Provider 结果 | 本地动作 |
|---|---|
| Succeeded | Contribution → Succeeded |
| Failed | Contribution → Failed |
| Pending | 保持 ReconciliationPending |
| NotFound | Contribution → RetryPending |
| ProviderUnavailable | 保持 ReconciliationPending |
| InvalidResponse | 保持等待或 ManualRequired |
| MissingLocalAttempt | ManualRequired |
| MaxReconciliationCount | ManualRequired + Alert |

### 必须在代码中找到

- [ ] `ReconcileContributionCommand`
- [ ] Query by ProviderReference
- [ ] Query by IdempotencyKey
- [ ] ProviderReference Upsert
- [ ] ReconciliationRecord
- [ ] `ResolvedAt`
- [ ] `Resolution`
- [ ] `ManualRequired`
- [ ] 最大 Reconciliation 次数
- [ ] 两个 Reconciliation Worker 并发时的行为

### 核心问题

1. ProviderUnavailable 为什么不等于 NotFound？
2. Pending 为什么不能标记 Resolved？
3. NotFound 为什么允许 Safe Retry？
4. ManualRequired 是否表示系统已经解决？
5. 两个 Reconciliation Worker 如何避免重复收敛？

---

## 4.9 Safe Retry 与 Retry Scheduling

### 必须区分三种 Retry

```text
Publisher Retry
→ Outbox 发 Queue 失败

SQS Redelivery
→ Worker 未 ACK，Visibility 到期

Business Scheduled Retry
→ Reconciliation 已确认 Provider NotFound
```

### Business Retry 流程

```text
ReconciliationPending
→ Provider Query: NotFound
→ RetryPending
→ NextRetryAt
→ Scheduler Atomic Claim
→ Retry Outbox
→ SQS
→ Worker
→ Processing
→ Provider Success
```

### 必须在代码中找到

- [ ] `RetryPending`
- [ ] `RetryCount`
- [ ] `NextRetryAt`
- [ ] Retry Message Contract
- [ ] Scheduler Claim
- [ ] Scheduler 并发保护
- [ ] Max Retry Attempts
- [ ] DeadLetterRecord
- [ ] Operator Alert
- [ ] Retry 后是否使用原 Provider Key

### Retry Budget

Circuit Open 不应消耗 Retry Budget，因为：

```text
Provider 根本没有被调用
```

### 核心问题

1. Publisher Retry 与 Business Retry 有何区别？
2. 为什么 Retry Message 最好只传 ContributionId？
3. 两个 Scheduler 如何避免重复入队？
4. RetryCount 应在什么时候增加？
5. Max Retry 后进入什么终态？

---

## 4.10 Callback Security

### 必须具备

```text
HMAC Signature
Strict Timestamp
EventId
Inbox Dedup
ProviderReference Lookup
IdempotencyKey Lookup
Contribution State Update
Optimistic Concurrency
Orphan Persistence
```

### HMAC

通常验证：

```text
timestamp + raw request body
```

必须使用：

```text
FixedTimeEquals
```

### Timestamp

用于防止 Replay Attack。

必须检查：

```text
格式是否可解析
是否为 UTC
是否太旧
是否过度超前
是否在允许 Clock Skew 内
```

### 必须在代码中找到

- [ ] Callback Controller
- [ ] Callback Verifier
- [ ] Secret 获取方式
- [ ] Header 名称
- [ ] HMAC 输入格式
- [ ] Fixed-time comparison
- [ ] TimeProvider
- [ ] Invalid Signature 返回状态
- [ ] Invalid Timestamp 是否修改数据库

### 核心不变量

```text
无效 Callback 不能改变 Contribution、Inbox 或 StateTransition。
```

---

## 4.11 Callback Dedup、Ordering 与 Orphan

### Duplicate Callback

Provider 可能因为：

```text
网络超时
接收方响应慢
Provider 自身 Retry
```

多次发送相同 EventId。

数据库需要唯一约束：

```text
ProviderName + EventId
```

不能只做：

```text
SELECT 不存在
→ INSERT
```

### Callback Before Submit Response

时间线：

```text
Worker 调用 Provider
→ Provider Commit
→ Callback 先到
→ Callback 将 Contribution 改为 Succeeded
→ Submit Response 后到
→ Worker 仍持有旧 Entity
```

如果 Worker 直接保存旧 Entity，可能造成：

```text
Lost Update
```

### Orphan Callback

如果 Reference 和 Key 都找不到 Contribution：

```text
必须持久化 Orphan Callback
```

不能只写日志。

### 必须在代码中找到

- [ ] Callback Inbox 唯一约束
- [ ] Reference Lookup
- [ ] IdempotencyKey Lookup
- [ ] Orphan Callback Entity
- [ ] Terminal Confirmation
- [ ] Terminal State Conflict
- [ ] Callback Before Response 的 Reload
- [ ] Concurrent Duplicate Callback 的处理

### 核心问题

1. Callback 为什么可能早于 Submit Response？
2. EventId 为什么需要数据库唯一约束？
3. Orphan Callback 为什么不能只记录日志？
4. 本地 Failed、Callback Succeeded 时怎么办？
5. 重复 Callback 返回 200 是否合理？

---

## 4.12 EF Core Tracking 与 Optimistic Concurrency

### Lost Update 场景

```text
Worker 读取 Processing
Callback 读取 Processing
Callback 保存 Succeeded
Worker 仍持有旧 Processing Entity
Worker 保存 ProviderUnknown
→ 覆盖正确终态
```

### Optimistic Concurrency

典型 SQL：

```sql
UPDATE contributions
SET state = @newState,
    version = @newVersion
WHERE id = @id
  AND version = @oldVersion;
```

更新 0 行表示：

```text
数据已经被其他执行者修改
```

此时需要：

```text
Reload
→ 重新评估当前状态
→ 安全退出或重新决策
```

### EF Tracking

同一个 DbContext 再查询同一 Entity，可能返回旧 Tracking Entity。

真正 Reload 方式：

```text
Entry.ReloadAsync()
ChangeTracker.Clear() + Query
New DbContext Scope
```

### 必须在代码中找到

- [ ] Concurrency Token
- [ ] Version 字段
- [ ] DbUpdateConcurrencyException
- [ ] Provider 返回后的 Reload
- [ ] Callback 更新后的 Worker 行为
- [ ] Terminal State Guard

---

## 4.13 State Machine 与 StateTransition

### 必须理解

状态机控制：

```text
哪些状态可以进入哪些状态
```

典型 Phase 3 状态：

```text
Created
Accepted
Processing
ProviderUnknown
ReconciliationPending
RetryPending
Succeeded
Failed
Completed
```

### 状态重投策略

| 当前状态 | 新消息到达时 |
|---|---|
| Created | 正常进入处理 |
| Accepted | 继续或恢复 |
| Processing | 恢复，不重新初始化 |
| ProviderUnknown | 不盲目 Submit |
| ReconciliationPending | 交给 Reconciliation |
| RetryPending | 等待 Scheduler 或进入 Retry |
| Succeeded | 幂等 ACK |
| Failed | 根据策略 ACK / DLQ |
| Completed | 幂等 ACK |

### StateTransition 审计

每次实际状态变化必须对应一条记录。

正确：

```text
Created → Accepted
Accepted → Processing
```

错误：

```text
Created → Processing
```

如果中间真的进入过 Accepted，就不能省略。

### 必须在代码中找到

- [ ] 所有允许转换
- [ ] 所有非法转换
- [ ] `FromState` 是否在 Transition 前读取
- [ ] 是否有连续 Transition 但只写一条记录
- [ ] 终态是否可以重新进入 Processing
- [ ] Callback 与 Worker 的冲突处理

---

## 4.14 Circuit Breaker

### 三种状态

```text
Closed
Open
Half-Open
```

### Open 时正确行为

```text
不调用 Provider
不创建业务 Attempt
不增加 RetryCount
不消耗 Retry Budget
不写 Processed Inbox
不 ACK 当前消息
允许 Visibility 后重投
```

Open 应表达为：

```text
DeferredBecauseCircuitOpen
```

而不是：

```text
Failed
```

### Half-Open

只允许：

```text
一个 Probe
```

不能让所有 Worker 一起穿透。

### 必须在代码中找到

- [ ] Circuit State
- [ ] Failure Threshold
- [ ] Open Duration
- [ ] TimeProvider
- [ ] Half-Open Single Probe
- [ ] 哪些 ErrorCategory 计入 Circuit
- [ ] Open 时 Worker 如何处理 SQS
- [ ] Circuit 恢复后的消息路径
- [ ] 多进程下 Circuit 状态是否共享

### 核心问题

1. Circuit Breaker 是否是 Retry 工具？
2. Circuit Open 为什么不能 ACK？
3. Circuit Open 为什么不能增加 RetryCount？
4. Half-Open 为什么只允许一个 Probe？
5. 内存 Circuit 在多 Worker 实例下有什么限制？

---

## 4.15 Retry Storm、Queue Backpressure 与 Recovery

### 因果链

```text
Provider Latency / Error Rate 上升
→ Worker Service Time 上升
→ Processing Throughput 下降
→ Arrival Rate > Completion Rate
→ Queue Depth 上升
→ Oldest Message Age 上升
→ Retry 增加
→ Provider 压力进一步上升
```

### 为什么增加 Worker 可能更糟

增加 Worker 只增加：

```text
并发请求
```

不会增加：

```text
Provider 容量
数据库连接池容量
网络吞吐
外部限额
```

### 必须理解的控制手段

```text
Circuit Breaker
Concurrency Limit
Rate Limit
Backoff + Jitter
Admission Control
Load Shedding
Queue Backpressure
Adaptive Concurrency
```

### 必须观察

- [ ] Queue Depth
- [ ] Oldest Message Age
- [ ] Provider Call Count
- [ ] Provider Error Rate
- [ ] Worker Concurrency
- [ ] RetryPending Count
- [ ] Retry Count
- [ ] Circuit State
- [ ] Processing Latency
- [ ] Queue Drain Time

---

# 5. Phase 3 必做故障实验

> 至少第一次由 Owner 本人执行命令并观察结果。  
> Agent 可以生成脚本和解释代码，但不能只告诉你“测试已经通过”。

---

## Experiment 1 — Happy Path with Provider Evidence

### 假设

正常处理时：

```text
Attempt 在 Provider 调用前持久化
Provider 只产生一个 Operation
Contribution 状态完整推进
Inbox 与业务状态同事务
Commit 后才 ACK
```

### 步骤

- [x] 创建一笔 Contribution
- [x] 观察 Contribution + Outbox
- [x] 启动 Publisher 和 Worker
- [x] 检查 ProcessingAttempt
- [x] 检查 Provider Operation
- [x] 检查 ProviderReference
- [x] 检查 StateTransition
- [x] 检查 Inbox
- [x] 检查 SQS 最终为空

### PASS 条件

```text
ProviderOperationCount == 1
ProviderReferenceCount == 1
Contribution.State == Succeeded
Attempt 在 Provider 调用前存在
状态转换完整
消息最终 ACK
```

### 执行结果

```text
PASS（E2）
Provider 调用前：Attempt=Pending、ProviderOperation=0、Inbox=0、ACK=0
ACK 前：Contribution/JobRun/Attempt=Succeeded、Reference=1、Inbox=Processed、ACK=0
状态审计：Created→Created→Accepted→Processing→Succeeded
最终：ProviderOperation=1、ProviderReference=1、Inbox=1
Queue Send/Receive/Delete=1/1/1，Queue empty，DeadLetter=0
生产代码修改：0
```

聚合实验报告：
[`learning/phase-3/exp1-happy-path-provider-evidence.md`](phase-3/exp1-happy-path-provider-evidence.md)

---

## Experiment 2 — Timeout Before Processing

### 假设

Provider 未处理时，系统不会在第一次 Timeout 后盲目 Retry，而是在 NotFound 证据出现后安全重试。

### 步骤

- [x] Provider Mode 设置为 `TimeoutBeforeProcessing`
- [x] 创建 Contribution
- [x] 等待进入 ReconciliationPending
- [x] 检查 ProviderOperationCount
- [x] 执行 Reconciliation
- [x] 确认 Query 返回 NotFound
- [x] 确认进入 RetryPending
- [x] 等待 Scheduler 到期
- [x] Provider 切换 Success
- [x] 等待最终 Succeeded
- [x] 对比两次 Attempt 的 Provider Key

### PASS 条件

```text
第一次 ProviderOperationCount == 0
第一次 Attempt == Unknown
NotFound 后才 Retry
两次 Attempt 使用同一 Key
最终 ProviderOperationCount == 1
最终 Succeeded
```

### 执行结果

```text
PASS（E2）
Reconciliation 前：Attempt=1/Unknown/Timeout、ProviderOperation=0
Reconciliation 前：RetryCount=0、NextRetryAt=null、RetryOutbox=0
Query by stable key：ProviderState=NotFound、Resolution=SafeRetry
最终：Contribution=Succeeded、Attempts=2、Distinct Provider Key=1
最终：ProviderOperation=1、ProviderReference=1、RetryOutbox=1
Queue Send/Receive/Delete=2/2/2，Queue empty，DeadLetter=0
生产代码修改：0
```

聚合实验报告：
[`learning/phase-3/exp2-timeout-before-processing.md`](phase-3/exp2-timeout-before-processing.md)

---

## Experiment 3 — Processed but Response Lost

### 假设

Provider 已处理但响应丢失时，系统通过 Reconciliation 找回原结果，不产生第二个 Provider Effect。

### 步骤

- [x] Provider Mode 设置为 `ProcessedButResponseLost`
- [x] 创建 Contribution
- [x] 等待 Provider 创建 Operation
- [x] 检查 Attempt == Unknown
- [x] 检查状态进入 ProviderUnknown
- [x] 检查状态进入 ReconciliationPending
- [x] 触发 Reconciliation
- [x] 检查 Query by Key 返回 Succeeded
- [x] 检查 ProviderReference 补写
- [x] 再次投递业务消息

### PASS 条件

```text
ProviderOperationCount == 1
ProviderReferenceCount == 1
Contribution.State == Succeeded
UnknownAttemptCount == 1
无第二个 Provider Effect
```

### 执行结果

```text
PASS（E2）
Response Lost 后：Attempt=1/Unknown/Timeout、ProviderOperation=1
Response Lost 后：AttemptReference=null、ProviderReferenceCount=0
Reconciliation：QueryByKey=Succeeded、Resolution=AutoFixed
Reconciliation 后：Contribution=Succeeded、ProviderReferenceCount=1
新 MessageId 再投递：终态幂等 ACK、AttemptCount 仍为 1
最终：UnknownAttempt=1、ProviderOperation=1、ProviderReference=1
Queue Send/Receive/Delete=2/2/2，Queue empty，DeadLetter=0
生产代码修改：0
```

聚合实验报告：
[`learning/phase-3/exp3-processed-response-lost.md`](phase-3/exp3-processed-response-lost.md)

---

## Experiment 4 — Same SQS Message Redelivery

### 假设

数据库已 Commit、ACK 前崩溃时，同一个 SQS MessageId 重投，但 Inbox 会阻止重复业务处理。

### 步骤

- [x] 开启 `BeforeMessageAck` Fault
- [x] 创建 Contribution
- [x] 等待 Provider Success
- [x] 检查 Contribution + Inbox 已 Commit
- [x] 确认 Worker 在 Delete 前崩溃
- [x] 等待 Visibility Timeout
- [x] 确认相同 MessageId 重投
- [x] 检查 Provider 是否再次调用
- [x] 检查消息最终 Delete

### PASS 条件

```text
ReceiveCount >= 2
ProviderOperationCount == 1
InboxCount == 1
AttemptCount == 1
Queue 最终为空
```

### 执行结果

```text
PASS（E2）
ACK 前故障：Contribution/Inbox/JobRun 已 Commit、ProviderOperation=1、Delete=0
Redelivery：同一逻辑 MessageId，ReceiveCount=2
SQS 原生证据：ApproximateReceiveCount=2
去重日志：already processed (inbox dedup)
最终：Inbox=1、Attempt=1、ProviderReference=1、ProviderOperation=1
Queue Send/Receive/Delete=1/2/1，Queue empty，DeadLetter=0
生产代码修改：0；旧同题测试迁入 Phase3/Exp4，没有复制文件
```

聚合实验报告：
[`learning/phase-3/exp4-same-sqs-message-redelivery.md`](phase-3/exp4-same-sqs-message-redelivery.md)

---

## Experiment 5 — Different MessageId, Same Contribution

### 假设

新的 SQS MessageId 指向已经 Succeeded 的同一 Contribution 时，业务状态和 Provider 幂等阻止重复处理。

### 步骤

- [x] 完成一笔 Contribution
- [x] 创建新的 OutboxMessage
- [x] 使用新的 MessageId
- [x] Payload 指向相同 ContributionId
- [x] 观察 Worker 行为
- [x] 检查 Provider Operation
- [x] 检查 StateTransition

### PASS 条件

```text
ProviderOperationCount 仍为 1
ProviderReferenceCount 仍为 1
Contribution 仍为 Succeeded
无新的业务状态推进
```

### 执行结果

```text
PASS（E2）
Message A != Message B，两个 Payload 指向同一 ContributionId
两个 Outbox=Sent、两个 Inbox=Processed、两个 JobRun=Succeeded
第二条消息日志：idempotent ACK without submit
Contribution 保持 Succeeded，StateTransition 保持 4
ProcessingAttempt=1、ProviderReference=1、ProviderOperation=1
Queue Send/Receive/Delete=2/2/2，Queue empty，DeadLetter=0
生产代码修改：0
测试聚合：删除旧 DuplicateMessageE2ETests 5项，由 Exp4+Exp5 两项综合测试替代
```

聚合实验报告：
[`learning/phase-3/exp5-different-message-id-same-contribution.md`](phase-3/exp5-different-message-id-same-contribution.md)

---

## Experiment 6 — Worker Crash after Provider Processed

### 假设

Provider 已经处理成功，但 Worker 在处理响应前崩溃时，重投使用稳定 Key 并最终收敛。

### 步骤

- [x] 开启 `AfterProviderProcessed` 或等价 Fault
- [x] 创建 Contribution
- [x] 确认 ProviderOperation 已存在
- [x] 强制 Worker 崩溃
- [x] 等待消息重投
- [x] 检查第二次 Submit 的 Key
- [x] 检查 Provider 返回原结果
- [x] 检查最终状态

### PASS 条件

```text
ProviderOperationCount == 1
最终 Succeeded
没有重复 ProviderReference
重投使用相同 Key
```

### 执行结果

```text
PASS（E2）
AfterProviderProcessed：Attempt1=Pending、ProviderOperation=1
崩溃边界：ProviderReference=0、Inbox=0、QueueDelete=0
Redelivery：ReceiveCount=2、Attempts=2、Distinct Provider Key=1
Provider 幂等回放：Attempt2=Succeeded、ProviderReference=1、Operation 仍为 1
最终：Contribution/JobRun=Succeeded、Inbox=1、Queue empty、DeadLetter=0
JobAttempts=Failed,Succeeded
生产代码修改：2处小范围故障注入协议修复；业务状态机和 Migration 修改为 0
测试整理：旧 CrashRecoveryTests 迁入 Exp6，删除与 Exp4 重复的 Inbox 测试
```

聚合实验报告：
[`learning/phase-3/exp6-worker-crash-after-provider-processed.md`](phase-3/exp6-worker-crash-after-provider-processed.md)

---

## Experiment 7 — Callback Security

### 假设

无效 Callback 不会改变任何业务状态。

### 步骤

分别发送：

- [x] 正确 HMAC
- [x] 错误 HMAC
- [x] 缺少 Signature
- [x] 缺少 Timestamp
- [x] 无法解析 Timestamp
- [x] 过期 Timestamp
- [x] 未来 Timestamp
- [x] 非 UTC Timestamp

### PASS 条件

```text
有效请求进入 Handler
无效请求返回 401 / 400
无效请求不创建 Inbox
无效请求不修改 Contribution
无效请求不写 StateTransition
```

### 执行结果

```text
PASS（E2）— 8/8
有效 HMAC：HTTP 200、Contribution=Succeeded、Inbox=1、StateTransition=1
错误 HMAC：HTTP 401、业务零修改
缺少 Signature/Timestamp：HTTP 401、业务零修改
无法解析、过期、未来、非 UTC Timestamp：HTTP 401、业务零修改
每个非法用例：Contribution=Processing、Inbox=0、StateTransition=0、Orphan=0
生产代码修改：0
测试整理：旧 HTTP 安全测试迁入 Exp7；Duplicate Callback 用例移交 Exp8
```

聚合实验报告：
[`learning/phase-3/exp7-callback-security.md`](phase-3/exp7-callback-security.md)

---

## Experiment 8 — Duplicate Callback

### 假设

相同 EventId 重复或并发到达时，只应用一次业务变化。

### 步骤

- [x] 使用同一个 EventId 连续发送两次
- [x] 使用同一个 EventId 并发发送两次
- [x] 检查 HTTP 响应
- [x] 检查 Callback Inbox
- [x] 检查状态变化次数
- [x] 检查 Provider Effect

### PASS 条件

```text
两次都可返回 200
Inbox 只有一条
状态变化只发生一次
ProviderOperationCount 不变
```

### 执行结果

```text
PASS（E2）— 2/2
顺序重复：HTTP=200,200、Inbox=1、Succeeded Transition=1
并发重复：HTTP=200,200、Inbox=1、Succeeded Transition=1
两个场景：Contribution=Succeeded、Version=1
两个场景：ReconciliationRecord=0、Operator Outbox=0
ProviderOperationCount：0 -> 0，保持不变
生产代码修改：0
测试聚合：删除2条低层 Handler 重复用例，替换为2条真实 HTTP 用例
```

聚合实验报告：
[`learning/phase-3/exp8-duplicate-callback.md`](phase-3/exp8-duplicate-callback.md)

---

## Experiment 9 — Callback Before Submit Response

### 假设

Callback 先把 Contribution 更新为 Succeeded 后，Worker 的迟到 Submit Response 不会覆盖正确终态。

### 步骤

- [x] Provider Submit 设置长响应延迟
- [x] Provider 完成 Operation
- [x] Callback 立即到达
- [x] Callback 将 Contribution 改为 Succeeded
- [x] Submit Response 后到
- [x] Worker Reload Contribution
- [x] 检查 Worker 是否再次 Transition
- [x] 重复发送相同 Callback
- [x] 尝试后续 Reconciliation

### PASS 条件

```text
Contribution 最终 Succeeded
Callback Inbox 只有一条
无 Lost Update
无第二次状态变化
后续 Reconciliation 安全跳过
```

### 执行结果

```text
PASS（E2）— 1/1
Provider Operation=1 后，在 AfterProviderProcessed 边界暂停 Submit Response
Callback：Processing -> Succeeded，Callback Inbox=1
迟到响应：ProcessingAttempt=Succeeded、ProviderReference=1
Worker：真实 Reload 看到 Succeeded，新增 Succeeded Transition=0
重复 Callback：200，Callback Inbox 仍为1
后续 Reconciliation：Not in reconciliation state, skipping，Record=0
最终：Queue Send/Receive/Delete=1/1/1、DeadLetter=0
生产代码修改：0
测试聚合：删除1条非并发旧用例，替换为1条真实 WorkerHost 竞态 E2E
```

聚合实验报告：
[`learning/phase-3/exp9-callback-before-submit-response.md`](phase-3/exp9-callback-before-submit-response.md)

---

## Experiment 10 — Concurrent Reconciliation

### 假设

两个 Reconciliation Worker 同时处理同一 Contribution 时，只有一个恢复动作生效。

### 步骤

- [x] 准备 ReconciliationPending Contribution
- [x] 启动两个独立 DbContext / Scope
- [x] 同时执行 Reconcile
- [x] 检查 StateTransition
- [x] 检查 ReconciliationRecord
- [x] 检查 Retry Schedule
- [x] 检查 ProviderReference
- [x] 检查异常

### PASS 条件

```text
只有一个有效状态转换
只有一个 Retry Schedule 或一个终态收敛
无重复 ProviderReference
无非法状态跳转
另一个执行者安全退出
```

### 执行结果

```text
PASS（E2）— 1/1，聚合2个并发场景
SafeRetry：Provider Query=2、Transition=1、Record=1、NextRetryAt=1
Succeeded：Provider Query=2、Transition=1、Record=1、ProviderReference=1
每个场景：winner=业务结果，loser=Concurrent reconciliation already applied
每个场景：Contribution Version=1、未处理异常=0、非法状态跳转=0
生产代码修改：0
测试聚合：删除1条非确定性旧用例，替换为1条双场景确定性屏障测试
```

聚合实验报告：
[`learning/phase-3/exp10-concurrent-reconciliation.md`](phase-3/exp10-concurrent-reconciliation.md)

---

## Experiment 11 — Circuit Open No ACK

### 假设

Circuit Open 时消息不会被错误 ACK，也不会消耗 Retry Budget。

### 步骤

- [x] 触发 Circuit Open
- [x] 创建 Contribution
- [x] 等待 Worker Receive
- [x] 检查 ProviderOperationCount
- [x] 检查 ProcessingAttempt
- [x] 检查 RetryCount
- [x] 检查 Inbox
- [x] 检查 SQS DeleteCount
- [x] 等待 Visibility Timeout
- [x] 检查 ApproximateReceiveCount
- [x] Close Circuit
- [x] 等待最终成功

### PASS 条件

Open 阶段：

```text
ProviderOperationCount == 0
AttemptCount == 0
RetryCount == 0
InboxCount == 0
DeleteCount == 0
ReceiveCount >= 2
```

恢复后：

```text
ProviderOperationCount == 1
Contribution == Succeeded
消息最终 ACK
```

### 执行结果

```text
PASS（E2）— 1/1
Open：Receive=2、SQS ApproximateReceiveCount=2、JobAttempt=2/all Deferred
Open：ProviderOperation=0、ProcessingAttempt=0、RetryCount=0、Inbox=0、Delete=0
Close 后：ProviderOperation=1、ProcessingAttempt=1/Succeeded、ProviderReference=1
最终：Contribution=Succeeded、Inbox=1、Delete=1、Job=Succeeded、Queue empty
生产代码修改：0
测试 helper 修复：Raw adapter 与生产 adapter 一样读取逻辑 MessageId attribute
测试聚合：根目录旧测试升级并移动到 Phase3/Exp11，测试总数净变化0
```

聚合实验报告：
[`learning/phase-3/exp11-circuit-open-no-ack.md`](phase-3/exp11-circuit-open-no-ack.md)

---

## Experiment 12 — Terminal Conflict and ManualRequired

### 假设

本地终态和 Provider 终态冲突时，系统不会静默覆盖，而是要求人工处理。

### 示例

```text
本地 Failed
Provider Callback / Query 返回 Succeeded
```

### 步骤

- [x] 准备本地 Failed Contribution
- [x] 发送 Provider Succeeded Callback
- [x] 检查 Contribution 是否被直接覆盖
- [x] 检查 ReconciliationRecord
- [x] 检查 ManualRequired
- [x] 检查 Operator Alert Outbox
- [x] 检查 AuditEvent

### PASS 条件

```text
不静默覆盖终态
ManualRequired 被记录
冲突原因可审计
存在 Operator Alert 或明确 Known Limitation
```

### 执行结果

```text
PASS（E2）— 1/1，聚合2个对称终态冲突场景
Failed(local) vs Succeeded(provider)：本地保持 Failed/Version=0
Succeeded(local) vs Failed(provider)：本地保持 Succeeded/Version=0
两个场景：StateTransition=0、Reconciliation=1/ManualRequired
两个场景：OperatorAlert=1/Pending、Callback Inbox=1、payload 完整
AuditEvent=0：已检查；冲突审计由 ReconciliationRecord + Alert + Inbox 承载
生产代码修改：0
测试聚合：删除1条单向弱用例，替换为1条双向聚合测试，测试总数净变化0
```

聚合实验报告：
[`learning/phase-3/exp12-terminal-conflict-manual-required.md`](phase-3/exp12-terminal-conflict-manual-required.md)

---

## Experiment 13 — Retry Exhaustion

### 假设

Safe Retry 达到最大次数后，系统停止自动执行，并进入可审计终态。

### 步骤

- [x] Provider 持续返回明确可重试结果
- [x] 记录每次 RetryCount
- [x] 记录 NextRetryAt
- [x] 检查 Backoff / Jitter
- [x] 等待达到最大次数
- [x] 检查 Contribution 状态
- [x] 检查 DeadLetterRecord
- [x] 检查 Operator Alert

### PASS 条件

```text
Retry 有明确上限
无无限 Retry
最终状态明确
DeadLetter 可审计
```

### 执行结果

```text
PASS（E2）— 1/1
Provider Mode=RateLimited（持续 retryable 429）
ProcessingAttempt=5、AttemptNumber=1..5、RetryCount=5
Backoff/Jitter：约 1.8s、2.2s、5.0s、8.6s（1/2/4/8s + 0–1s）
最终：Contribution=Failed、NextRetryAt=null
DeadLetter=1/ContributionRetryExhausted、OperatorAlert=1
稳定性等待3秒：Attempt 5->5、RetryOutbox 4->4、无继续 Retry
生产代码修改：0
测试聚合：Phase2 Exp7 与 Phase3 Exp13 共享单一 scenario 实现，无大段复制
```

聚合实验报告：
[`learning/phase-3/exp13-retry-exhaustion.md`](phase-3/exp13-retry-exhaustion.md)

---

## Experiment 14 — Provider Backlog and Recovery

### 假设

Provider 故障造成积压时，Circuit 和 Backpressure 可以限制压力，并在恢复后安全清空队列。

### 步骤

- [x] 批量创建 50–100 个 Contribution
- [x] Provider 设置 5xx / 高延迟
- [x] 观察 Circuit 何时 Open
- [x] 观察 Queue Depth
- [x] 观察 Oldest Message Age
- [x] 观察 RetryPending Count
- [x] 观察 Provider Call Count
- [x] 恢复 Provider
- [x] 观察 Half-Open Probe
- [x] 测量队列清空时间
- [x] 检查是否出现重复 Provider Effect

### PASS 条件

```text
Provider 故障时调用量受控
Queue 积压可观察
恢复时无瞬时洪峰
队列最终清空
无重复业务效果
```

### 执行结果

```text
PASS（E2）— Exp14 1/1；全量 161/161
批量：50 个 Contribution / 50 条 SQS Message
故障：Error5xxBeforeProcessing + 100ms Provider latency
Open：第 5 次 5xx 后打开；Provider Effect=0
积压：Queue Depth=45；Oldest Message Age≈4.1s；RetryPending=5
恢复：观察到 Half-Open 单 Probe；并发限制=1
恢复速率：最大 1 个 Provider Effect / 100ms；约 13.0s 清空
最终：50 Succeeded、50 ProviderReference、50 Provider Effect
重复业务效果=0；DeadLetter=0；Queue Depth=0
生产代码修改：0
测试基础设施：WorkerHostFixture 增加可选 Circuit 注入，默认行为不变
```

聚合实验报告：
[`learning/phase-3/exp14-provider-backlog-and-recovery.md`](phase-3/exp14-provider-backlog-and-recovery.md)

---

## Experiment 15 — Operational History Retention and Capacity Guardrails

> 归属决定：作为 Phase 3 最后一个 Experiment。它不只是执行 DELETE，而是把
> Retention Policy、容量指标、批量清理和告警一起验证，作为进入正式生产准备
> 前的数据生命周期 Gate。

### 假设

JobRun、JobAttempt、Lease、Inbox、Outbox 和 Reconciliation 等运行历史持续增长
时，系统能够按照明确策略清理或归档已终结数据，同时保留活跃任务、未解决异常、
业务记录和必须保留的审计证据。

### 步骤

- [x] 为每类表定义 Owner、保留周期、归档方式和删除依据
- [x] 明确 Terminal、Active、Pending、Unknown、ManualRequired 的保留规则
- [x] 明确 AuditEvent / StateTransition 是否只归档而不直接删除
- [x] 准备超过 Retention Cutoff 的终结历史数据
- [x] 同时准备未过期和仍在处理中的控制组数据
- [x] 使用小批次、可重入的 Maintenance Cleanup 执行清理
- [x] 验证 JobAttempt、Lease、JobRun 等外键删除顺序
- [x] 并发运行两个 Cleanup Scanner，确认不会重复处理或长时间锁表
- [x] 中途终止 Cleanup，再次启动并确认能够继续
- [x] 检查 Cleanup 扫描数、删除数、跳过数、失败数和耗时指标
- [x] 检查表大小、最老可清理记录年龄和预计清空时间
- [x] 模拟 Cleanup 持续失败或表容量超过阈值
- [x] 确认容量告警和 Cleanup Failure 告警触发
- [x] 检查活跃 Job、未解决记录、业务数据和审计证据没有被误删

### PASS 条件

```text
只有符合策略的终结历史被清理或归档
Active / Pending / Unknown / ManualRequired 数据不被误删
Cleanup 有批次上限、可重入、可中断恢复
并发 Cleanup 不产生重复删除或长事务
容量、清理进度和失败都有指标
超过容量或清理失败会触发告警
清理后业务正确性和审计要求仍然成立
```

### 必须产出的 Policy Matrix

| 数据类型 | Owner | 默认周期与动作 | 必须保护的状态 |
|---|---|---|---|
| Outbox / Inbox | Messaging Platform | 30 天；Sent/Failed、Processed/Failed 且无 Active Job 后直接清理 | Pending、Processing、Active Job、未确认 |
| JobRun / JobAttempt / Lease / Checkpoint | Worker Platform | 30 天；Job 终态且无 Active Lease，按 Child→Parent 成组清理 | Running、Pending、Active Lease |
| ProcessingAttempt | Provider Integration | 90 天；Succeeded/Failed 且业务终态，先写 online archive 再清理 | Unknown、Pending、非终态 Contribution |
| Reconciliation | Provider Reliability / SRE | 90 天；已 Resolved 且业务终态，先归档再清理 | 未 Resolved、WaitNextCycle、ManualRequired、非终态 Contribution |
| AuditEvent / StateTransition | Security / Compliance | 不直接删除；由合规归档策略管理 | 全部事故调查与合规证据 |
| ProviderReference / Contribution | Business Owner | 不属于 operational cleanup | 全部业务事实与幂等映射 |
| DeadLetter / OperatorAlert | SRE / Operations | Pending 永不自动清理；完成后的周期需单独审批 | Pending、未调查、未处置 |
| OperationalHistoryArchive | Data Governance | online archive；外部归档与最终删除需独立批准 | 未完成外部归档或 legal hold |

### 执行结果

```text
PASS（E2）— Exp15 1/1；全量 162/162
初始：Managed Rows=73；Eligible=40；Protected=33；DB≈630784 bytes
最老 Eligible≈120 天；batch=2；预计3轮/180秒（按1分钟调度）
中断：BeforeCommit 强制终止，事务完整回滚，RowsChanged=0
并发：Scanner A 持有 PostgreSQL advisory lock；Scanner B 约15ms跳过
清理：3轮完成；Scanned=40；Deleted=40；Archived=10
保护：Active Job、Pending Outbox、Processing Inbox、Pending/Unknown Attempt、
      ManualRequired、7条业务数据、14条审计证据全部保留
指标：Runs=6；Skipped=1；Failures=1；Alerts=2
告警：CapacityWarning=1；CleanupFailure=1（带60分钟进程内冷却）
Hosted：ScheduledMaintenance Enabled 后自动清理过期 Outbox
全量稳定性修复：5个 Worker publish Docker 实验串行；Exp12 等待 Lease release
```

> 当前补全审计：Exp15 仍为 1/1；仓库扩展全量为 163/163，GitHub Actions
> run 31447327012 PASS。162/162 保留为 Exp15 首次完成时的历史快照。

聚合实验报告：
[`learning/phase-3/exp15-operational-history-retention.md`](phase-3/exp15-operational-history-retention.md)

---

# 6. Evidence 保存规则

Phase 3 使用一实验一报告和一份聚合 Phase 3.1 Evidence：

```text
learning/phase-3/exp1-*.md
...
learning/phase-3/exp15-*.md
docs/evidence/phase-3.1.md
```

- [x] 每个实验恰好一份聚合报告
- [x] 报告包含假设、命令、时间线、关键日志和各系统最终状态
- [x] ProviderOperationCount、状态转换和限制写入同一报告
- [x] 原始 TRX、长日志和生成摘要由 CI Artifact 保存
- [x] Phase 3.1 Gate、CI、E2E 和限制聚合为一份 Evidence
- [x] `scripts/verify-experiments.ps1` 阻止零测试和缺报告
- [x] 不再为一个实验创建多个碎片文件或空目录

---

# 7. Owner 代码审查清单

## Provider Submit

- [ ] Attempt 在 Provider 调用前 Commit
- [ ] Provider 调用不被长数据库事务包围
- [ ] Key 跨 Attempt 稳定
- [ ] 同 Key 不同 Payload 冲突
- [ ] Provider Operation 原子创建
- [ ] Provider 返回后 Reload Contribution
- [ ] Error 分类准确
- [ ] Unknown 不直接 Retry

## ProcessingAttempt

- [ ] AttemptNumber 唯一
- [ ] Status 可表达 Pending / Succeeded / Failed / Unknown
- [ ] ErrorCategory 持久化
- [ ] Provider Key 持久化
- [ ] Worker 重启后可恢复
- [ ] Retry 新 Attempt 仍使用旧 Key

## ProviderReference

- [ ] ProviderName + Reference 唯一
- [ ] Contribution 只能有合理数量的 Reference
- [ ] Reconciliation 可补写 Reference
- [ ] 并发 Upsert 安全
- [ ] Callback 可按 Reference 查找

## Reconciliation

- [ ] Reference 查询
- [ ] Key 查询
- [ ] 完整决策表
- [ ] Pending 不错误 Resolved
- [ ] Unavailable 不等于 NotFound
- [ ] ManualRequired 不等于自动解决
- [ ] 最大次数明确
- [ ] 并发执行安全
- [ ] Retry Schedule 不重复

## Callback

- [ ] HMAC
- [ ] FixedTimeEquals
- [ ] Strict Timestamp
- [ ] TimeProvider
- [ ] EventId 唯一约束
- [ ] Reference Lookup
- [ ] Key Lookup
- [ ] Orphan Persistence
- [ ] Duplicate Callback
- [ ] Concurrent Duplicate
- [ ] Callback Before Response
- [ ] Terminal Conflict
- [ ] 状态和 Inbox 同事务

## Retry Scheduling

- [ ] RetryPending
- [ ] NextRetryAt
- [ ] RetryCount
- [ ] Atomic Claim
- [ ] Scheduler 并发
- [ ] Message Contract
- [ ] Max Attempts
- [ ] DLQ / DeadLetter
- [ ] Operator Alert
- [ ] Circuit Open 不消耗 Budget

## Circuit Breaker

- [ ] Closed / Open / Half-Open
- [ ] Open 不调用 Provider
- [ ] Open 不创建 Attempt
- [ ] Open 不写 Inbox
- [ ] Open 不 ACK
- [ ] Open 不增加 RetryCount
- [ ] Half-Open 单 Probe
- [ ] TimeProvider
- [ ] 错误分类正确
- [ ] 多实例 Known Limitation 明确

## State Machine

- [ ] 每个中间状态都保留
- [ ] 每次 Transition 有独立记录
- [ ] FromState 正确
- [ ] 终态不可重新处理
- [ ] Redelivery 恢复路径明确
- [ ] Callback / Worker 冲突受保护
- [ ] Optimistic Concurrency 生效

## Tests

- [ ] PostgreSQL Testcontainers
- [ ] LocalStack SQS
- [ ] Real WorkerHost
- [ ] WebApplicationFactory
- [ ] Fault Injection
- [ ] Same Message Redelivery
- [ ] New Message Same Contribution
- [ ] Safe Retry E2E
- [ ] Response Lost E2E
- [ ] Circuit Open E2E
- [ ] Callback Security HTTP
- [ ] Concurrent Reconciliation
- [ ] Test Count Gate
- [ ] CI Artifact

---

# 8. Phase 3 口头验收题

不看文档回答。

## Q1

为什么 Timeout 不能直接解释为 Provider 未处理？

应覆盖：

```text
调用方只知道没有收到可信响应
请求可能未到达、处理中、已成功或已失败
Timeout 是观察，不是业务终态
```

---

## Q2

ProviderUnknown 和 ReconciliationPending 为什么是两个状态？

应覆盖：

```text
ProviderUnknown 描述事实
ReconciliationPending 描述恢复计划
状态审计不能丢失中间事实
```

---

## Q3

Timeout Before Processing 和 Processed-but-Response-Lost 有什么区别？

应覆盖：

```text
前者 ProviderOperationCount == 0
后者 ProviderOperationCount == 1
Worker 都可能只看到 Timeout
必须通过 Provider Query 区分
```

---

## Q4

为什么第一次 Timeout 后不能 Retry，而 NotFound 后可以？

应覆盖：

```text
Timeout 没有证明 Provider 未处理
NotFound 是新证据
新证据改变 Retry 安全性
```

---

## Q5

Provider Idempotency Key 为什么不能包含 AttemptNumber？

应覆盖：

```text
Key 表示逻辑业务操作
AttemptNumber 表示网络尝试
每次 Attempt 新 Key 会失去 Provider 幂等保护
```

---

## Q6

为什么 Inbox 不能单独防止重复 Provider Effect？

应覆盖：

```text
不同 MessageId
Provider 成功、本地 Commit 前 Inbox 尚未写入
Provider 是独立系统
需要业务状态和稳定 Provider Key
```

---

## Q7

Provider 已成功但本地未知时，系统如何恢复？

应覆盖：

```text
ProcessingAttempt
Stable Key
ProviderUnknown
ReconciliationPending
Query by Key
ProviderReference
State Convergence
ProviderOperationCount == 1
```

---

## Q8

ProviderUnavailable 为什么不能进入 RetryPending？

应覆盖：

```text
Unavailable 只说明无法查询
不代表 Provider 没有处理
只有明确 NotFound 才提供 Safe Retry 证据
```

---

## Q9

Callback 为什么可能早于 Submit Response？

应覆盖：

```text
Provider 内部 Commit
异步 Callback
Submit Response 网络延迟
不同通道没有顺序保证
```

---

## Q10

Callback 已经把状态改成 Succeeded 后，Worker 为什么可能覆盖它？

应覆盖：

```text
旧 Entity
EF Tracking
Last Writer Wins
Concurrency Token
Reload
Terminal State Guard
```

---

## Q11

为什么 EventId 需要数据库唯一约束？

应覆盖：

```text
两个 Callback 并发先查后写
应用层 SELECT 无法消除竞态
数据库唯一约束是最终保护
```

---

## Q12

Circuit Open 时为什么不能 ACK 消息？

应覆盖：

```text
任务尚未执行
Provider 未被调用
ACK 会让任务永久消失
应保持可重投或明确重新调度
```

---

## Q13

Circuit Open 为什么不能消耗 Retry Budget？

应覆盖：

```text
没有发生真正 Provider Attempt
Retry Budget 应统计业务尝试
Deferred 不等于 Failed
```

---

## Q14

Half-Open 为什么只能允许一个 Probe？

应覆盖：

```text
防止恢复瞬间所有 Worker 穿透
避免 Thundering Herd
一个 Probe 决定 Closed 或重新 Open
```

---

## Q15

相同 SQS MessageId 重投与新的 MessageId 指向同一 Contribution 有什么区别？

应覆盖：

```text
相同 MessageId 由 Inbox 去重
不同 MessageId 由 Business State + Provider Idempotency 防重
```

---

## Q16

两个 Reconciliation Worker 同时执行会有什么风险？

应覆盖：

```text
重复 StateTransition
重复 Retry Schedule
重复 ProviderReference
非法状态转换
需要并发控制和唯一约束
```

---

## Q17

怎样证明系统没有产生第二个 Provider 业务效果？

应覆盖：

```text
ProviderOperationCount == 1
ProviderReferenceCount == 1
Attempt 使用同一 Key
重复消息 / Crash / Retry 后仍为 1
真实 Integration / E2E Evidence
```

---

## Q18

为什么增加 Worker 数量可能让 Provider 事故更严重？

应覆盖：

```text
下游容量未增加
更多并发请求
更多 Timeout
Retry Storm
连接池和线程耗尽
Backpressure
```

---

## Q19

Queue Depth 和 Oldest Message Age 分别说明什么？

应覆盖：

```text
Queue Depth = 积压数量
Oldest Age = 最老任务等待时间
Oldest Age 更接近处理新鲜度和用户影响
```

---

## Q20

Phase 3 的 Exactly-once 目标是什么？

应覆盖：

```text
不声称消息 Exactly-once Delivery
目标是在重复投递和 Crash 条件下
实现 At-most-one Provider Business Effect
以及 Eventually Consistent Local State
```

---

# 9. Phase 3 Gate

只有以下全部成立，才进入 Phase 4。

## Functional Gate

- [x] Provider Submit 正常工作
- [x] ProcessingAttempt 持久化
- [x] Stable Provider Idempotency Key 生效
- [x] ProviderReference 可保存和查询
- [x] Unknown Outcome 可表达
- [x] Reconciliation Decision Table 可执行
- [x] Safe Retry 可调度
- [x] Callback 可验签和处理
- [x] Circuit Breaker 可 Open / Half-Open / Close
- [x] DeadLetter / ManualRequired 可查询

## Reliability Gate

- [x] Provider 调用前 Attempt 已 Commit
- [x] 并发 Submit 最多一个 Provider Operation
- [x] Worker Restart 使用相同 Provider Key
- [x] Timeout Before Processing 不盲目 Retry
- [x] Processed-but-Response-Lost 最终收敛
- [x] ProviderOperationCount 始终为 1
- [x] Unknown 有完整状态转换
- [x] Reconciliation 全决策表通过
- [x] Reconciliation 并发只应用一次
- [x] NotFound 后 Safe Retry 完整闭环
- [x] Retry Scheduler 不重复入队
- [x] Retry 有上限和 DLQ
- [x] Callback HMAC / Timestamp 生效
- [x] Duplicate Callback 只应用一次
- [x] Callback Before Response 不产生 Lost Update
- [x] Orphan Callback 可审计
- [x] Terminal Conflict 进入 ManualRequired
- [x] Circuit Open 不调用 Provider
- [x] Circuit Open 不 ACK
- [x] Circuit Open 不消耗 Retry Budget
- [x] Half-Open 只有一个 Probe
- [x] Crash Before ACK 可重投和去重
- [x] Crash After Provider Processed 不重复外部效果
- [x] Same Message Redelivery 和 New Message Duplicate 均验证

## Evidence Gate

- [x] 每个实验有运行命令
- [x] 每个实验有实际日志
- [x] 每个实验有数据库最终状态
- [x] 每个实验有 Queue 最终状态
- [x] 每个实验有 Provider Operation Count
- [x] 每个实验有 PASS / FAIL
- [x] 每个实验可由第三方复验
- [x] PostgreSQL Evidence 标明 E1
- [x] LocalStack / WorkerHost Evidence 标明 E2
- [x] 不把 Unit Test 冒充端到端证据
- [x] CI Run 和 Commit SHA 对应
- [x] TRX / Test Artifact 可下载
- [x] Test Count Gate 防止 0 测试误通过

## Owner Knowledge Gate

- [ ] 能解释 Provider 外部副作用
- [ ] 能解释 ProcessingAttempt
- [ ] 能解释 Stable Provider Key
- [ ] 能区分 Failure 与 Unknown
- [ ] 能解释 Timeout Before Processing
- [ ] 能解释 Processed-but-Response-Lost
- [ ] 能解释 Safe Retry
- [ ] 能画 Reconciliation Decision Table
- [ ] 能解释 ProviderUnavailable 与 NotFound
- [ ] 能解释 Retry Budget
- [ ] 能解释 Callback HMAC / Timestamp
- [ ] 能解释 Duplicate Callback
- [ ] 能解释 Callback Before Response
- [ ] 能解释 EF Tracking / Lost Update
- [ ] 能解释 Optimistic Concurrency
- [ ] 能解释 StateTransition 审计
- [ ] 能解释 Circuit Open no-ACK
- [ ] 能解释 Half-Open Single Probe
- [ ] 能解释 Retry Storm / Backpressure
- [ ] 能指出 Provider 调用点
- [ ] 能指出 Attempt Commit 点
- [ ] 能指出 Reconciliation 查询点
- [ ] 能指出 Callback Commit 点
- [ ] 能指出 SQS ACK 点
- [ ] 能针对任意 Crash Point说明 DB / SQS / Provider 状态

---

# 10. Phase 4 Readiness Check

进入 Phase 4 前，Phase 3 必须能够回答“应该观测什么”。

## Unknown Outcome Signals

- [ ] `provider_unknown_total`
- [ ] `provider_unknown_rate`
- [ ] `reconciliation_pending_count`
- [ ] `reconciliation_oldest_age`
- [ ] `reconciliation_resolution_total`
- [ ] `reconciliation_manual_required_total`

## Provider Signals

- [ ] Provider request count
- [ ] Provider latency
- [ ] Provider error rate
- [ ] Provider timeout rate
- [ ] Provider result by category
- [ ] Idempotency conflict count
- [ ] Duplicate Provider Operation detection

## Callback Signals

- [ ] Callback received count
- [ ] Invalid signature count
- [ ] Invalid timestamp count
- [ ] Duplicate callback count
- [ ] Orphan callback count
- [ ] Terminal conflict count
- [ ] Callback processing latency

## Retry Signals

- [ ] RetryPending count
- [ ] Retry scheduled count
- [ ] Retry exhausted count
- [ ] Retry age
- [ ] Retry by ErrorCategory
- [ ] DeadLetter count

## Circuit / Queue Signals

- [ ] Circuit state
- [ ] Circuit state transition count
- [ ] Half-open probe count
- [ ] Queue depth
- [ ] Oldest message age
- [ ] Receive count
- [ ] Delete count
- [ ] Redelivery count
- [ ] Worker concurrency
- [ ] Queue drain rate

## Trace Requirements

- [ ] API CorrelationId
- [ ] OutboxMessageId
- [ ] SQS MessageId
- [ ] ContributionId
- [ ] ProcessingAttemptId
- [ ] ProviderIdempotencyKey
- [ ] ProviderReference
- [ ] Callback EventId
- [ ] ReconciliationRecordId
- [ ] Retry Dispatch Id

如果你无法解释一个 Metric 对应哪个故障实验，就不要让 Agent先实现该 Metric。

---

# 11. 推荐推进顺序

## Step 1 — Freeze Phase 4

- [x] 不新增 OTel / Dashboard / SLO
- [x] 保存 Phase 3 当前完成状态
- [x] 确认 CI Green
- [ ] 建立学习分支和目录

## Step 2 — Complete Phase 2 Owner Gate

- [ ] 完成 Phase 2 核心知识
- [ ] 亲自执行核心消息故障实验
- [ ] 完成 Phase 2 口头题
- [ ] 签发 Phase 2 Decision

## Step 3 — Phase 3 Theory

按顺序学习：

```text
Provider Side Effect
→ ProcessingAttempt
→ Stable Provider Key
→ Error Classification
→ Unknown Outcome
→ Timeout Before Processing
→ Processed but Response Lost
→ Reconciliation
→ Safe Retry
→ Callback
→ Concurrency
→ Circuit Breaker
→ Backpressure
```

## Step 4 — Phase 3 Experiments

优先执行：

```text
Happy Path
→ Timeout Before Processing
→ Processed but Response Lost
→ Crash Before ACK
→ Callback Before Response
→ Circuit Open
```

其余实验随后完成。

## Step 5 — Code Review

- [ ] Provider Key 生命周期
- [ ] Attempt Commit 点
- [ ] Unknown 状态路径
- [ ] Reconciliation 决策表
- [ ] Retry Scheduler
- [ ] Callback Security
- [ ] Optimistic Concurrency
- [ ] Circuit ACK 语义

## Step 6 — Oral Review

- [ ] 完成 20 道口头题
- [ ] 画 Provider Unknown 时序图
- [ ] 画 Callback 并发图
- [ ] 画 Crash Matrix
- [ ] 画 Reconciliation Decision Table
- [ ] 讲三分钟 Response Lost 故事

## Step 7 — Gate Decision

选择其一：

```text
ACCEPT
Phase 3 完整通过，可以进入 Phase 4。

VALIDATION
代码和 CI 已完成，但仍缺少 Owner 实验、证据或理解。

BLOCKED
核心不变量、故障恢复或 Owner 验证存在缺陷。
```

---

# 12. Phase 3 Gate Decision Template

```markdown
# Phase 3 Gate Decision

## Decision

ACCEPT / VALIDATION / BLOCKED

## Date

YYYY-MM-DD

## Owner

Name

## Implementation Baseline

- Commit SHA:
- CI Run:
- Total Tests:
- Evidence Pack:

## Scope Completed

- Provider idempotency
- Unknown outcome
- Reconciliation
- Safe retry
- Callback
- Circuit breaker
- Crash recovery
- ...

## Experiments Passed

- ...

## Experiments Failed or Missing

- ...

## Core Invariants

- [ ] Attempt 在 Provider 调用前 Commit
- [ ] Stable Provider Key 跨所有 Retry
- [ ] Unknown 不盲目 Retry
- [ ] Response Lost 不产生第二个 Provider Effect
- [ ] NotFound 后 Safe Retry
- [ ] Callback 重复和乱序安全
- [ ] Reconciliation 并发安全
- [ ] Circuit Open 不 ACK
- [ ] Crash 后最终恢复
- [ ] ProviderOperationCount 始终为 1
- [ ] Retention 不删除活跃或未解决数据
- [ ] Cleanup 容量、进度和失败可观察并可告警

## Owner Knowledge

- [ ] 能解释 Unknown Outcome
- [ ] 能解释 Safe Retry
- [ ] 能解释 Reconciliation
- [ ] 能解释 Callback Ordering
- [ ] 能解释 Optimistic Concurrency
- [ ] 能解释 Circuit / Backpressure
- [ ] 能指出代码和事务边界

## Known Limitations

- ...

## Open Risks

- ...

## Evidence Index

- ...

## Phase 4 Entry Decision

Approved / Not Approved

## Owner Notes

...
```

---

# 13. 最终能力目标

Phase 3 完成后，你不需要已经掌握所有支付系统和分布式系统知识。

你需要真正获得下面这一项能力：

> 能够判断一个使用外部 Provider 的异步系统，在消息重复、Worker 崩溃、Provider 响应丢失、Callback 乱序、并发恢复和下游故障的情况下，是否会重复产生外部业务效果、错误覆盖终态、无限重试，或者永久无法收敛。

你还需要能够脱稿讲出：

```text
Provider 已经处理成功，但响应丢失时，
Reliant 不会把 Timeout 当成普通失败。
系统保存 ProcessingAttempt 和稳定 Provider Key，
将 Contribution 转入 ProviderUnknown 和 ReconciliationPending，
随后按 Key 查询 Provider。
Provider 返回原成功结果后，
本地补写 ProviderReference 并收敛为 Succeeded。
重复消息、Worker Restart 和 Callback 重复都不会产生第二个 Provider Operation。
```

做到这一点，Phase 3 才真正属于你，而不只是属于 Agent 和 CI。
