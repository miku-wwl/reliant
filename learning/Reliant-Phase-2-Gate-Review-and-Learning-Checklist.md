# Reliant Phase 2 Gate Review & Learning Checklist

> 用途：Phase 2（Reliable Asynchronous Processing）代码实现完成后，由 Owner 主导学习、验证和签发 Gate。  
> 原则：Agent 可以实现代码，但不能替你判断系统是否真的可靠。  
> 进入 Phase 3 前，必须完成本文件中的核心学习、实验和验收。

---

## 1. 当前目标

Phase 2 的目标不是“把消息队列跑起来”，而是证明：

```text
数据库提交后消息不会静默丢失
消息重复投递不会产生重复业务结果
Worker 非正常崩溃后任务可以恢复
Retry 不会无限循环或制造重试风暴
Poison Message 不会阻塞正常处理
Dead-letter 可以审计和受控 Replay
```

Phase 2 的核心链路：

```text
API
→ PostgreSQL Transaction
→ Business Data + Outbox
→ Outbox Publisher
→ Queue
→ Unified Worker Host
→ Inbox / Idempotency
→ Handler
→ Database Commit
→ ACK / Complete
```

---

## 2. 立即停止新增功能

在进入 Phase 3 前：

- [ ] 停止增加 Phase 3 代码
- [ ] 停止继续扩展 Phase 2 Scope
- [ ] 确认当前改动不直接堆在 `main`
- [ ] 创建或切换到 Phase 2 分支
- [ ] 将大改动拆成可审查的小 Commit
- [ ] 让 Agent 输出当前完成情况和缺口
- [ ] 由 Owner 决定 ACCEPT / VALIDATION / BLOCKED

建议分支：

```bash
git switch -c phase/2-reliable-async-processing
```

---

## 3. 让 Agent 生成 Phase 2 Review Packet

将下面的提示词直接交给 Agent：

```text
停止增加 Phase 3 或新的 Phase 2 功能。

请为当前 Phase 2 生成 Owner Review Packet，但不要自行签发 PASS。

需要输出：

1. 当前已经完成的 Phase 2 Scope；
2. 尚未完成或只完成骨架的内容；
3. 关键代码文件和职责；
4. Outbox、Inbox、Queue、Worker、Retry、Lease、Heartbeat、
   Checkpoint、DLQ 的调用链；
5. 数据库事务边界；
6. Queue ACK/Complete 的准确位置；
7. 所有状态机和状态转换；
8. 每个 Phase 2 Failure Scenario 的：
   - 故障注入方法
   - 运行命令
   - 预期结果
   - 实际结果
   - Evidence 路径
9. 当前测试列表和测试类型；
10. 与 Phase 2 Gate 的逐项映射；
11. Known Limitations；
12. Open Risks。

禁止：
- 自行声明 Phase 2 PASS；
- 用单元测试代替真实集成或崩溃恢复验证；
- 声称 Exactly-once Delivery；
- 跳过失败测试；
- 开始 Phase 3。
```

---

# 4. Phase 2 必须掌握的知识

## 4.1 At-least-once、ACK 与 Redelivery

### 必须理解

Queue 可能重复投递消息。

典型时间线：

```text
Worker 收到消息
→ 执行业务
→ 数据库提交成功
→ Worker 在 ACK 前崩溃
→ Visibility Timeout 到期
→ Queue 再次投递消息
```

因此：

```text
At-least-once Delivery
= 尽量保证消息不丢
+ 允许消息重复
```

### 必须在代码中找到

- [ ] 消息在哪里 Receive
- [ ] Visibility Timeout 在哪里配置
- [ ] ACK / Complete 在哪里执行
- [ ] Abandon / NACK 在哪里执行
- [ ] Worker 崩溃后为什么会重新投递
- [ ] ACK 是否发生在业务事务完成之后

### 核心问题

1. 为什么 ACK 不能发生在业务处理之前？
2. 数据库提交后、ACK 前崩溃会发生什么？
3. Queue 如何判断消息应该再次可见？
4. Redelivery 是否一定意味着系统有 Bug？

---

## 4.2 Transactional Outbox

### 必须理解

业务数据与 Outbox 消息必须在同一个 PostgreSQL 事务中提交。

正确做法：

```text
BEGIN TRANSACTION

INSERT / UPDATE Business Data
INSERT OutboxMessage

COMMIT
```

错误做法：

```text
COMMIT Business Data

然后尝试发送 Queue Message
```

错误做法存在以下窗口：

```text
业务提交成功
→ 进程崩溃
→ 消息未发送
→ 任务永久丢失
```

### 必须在代码中找到

- [ ] `CreateContributionCommand` 或等价入口
- [ ] Business Entity 写入位置
- [ ] `OutboxMessage` 写入位置
- [ ] 是否使用同一个 DbContext / Transaction
- [ ] 是否只有一个原子提交点
- [ ] Outbox Publisher 是否在事务提交之后异步发布
- [ ] Outbox 状态如何变化
- [ ] Publisher Crash 后如何重新扫描

### 核心不变量

```text
只要业务状态提交成功，对应的 Outbox 消息必须存在。
```

---

## 4.3 Inbox、幂等与 Exactly-once Business Effect

### 必须区分

```text
Exactly-once Delivery
```

和：

```text
Exactly-once Business Effect
```

Phase 2 不应声称底层消息只会投递一次。

合理目标是：

```text
At-least-once Delivery
+ Idempotent Consumer
= Exactly-once Business Effect
```

### 必须在代码中找到

- [ ] `InboxMessage`
- [ ] MessageId 的生成和传递
- [ ] Inbox 唯一约束
- [ ] Inbox 与业务副作用是否同事务
- [ ] 两个 Worker 并发处理相同 MessageId 时的行为
- [ ] 重复消息被抑制后是否留下审计记录
- [ ] Inbox 历史数据的清理策略或已知限制

### 注意

仅做下面的逻辑是不够的：

```text
SELECT Inbox WHERE MessageId = X
如果不存在，再 INSERT
```

两个 Worker 可能同时 SELECT，都看到不存在，然后同时处理。

最终保护层应包括：

```text
数据库唯一约束
+ 正确事务边界
```

---

## 4.4 Lease、Heartbeat、Checkpoint 与 Crash Recovery

### 概念区分

#### Lease

```text
当前 Worker 在一段时间内拥有任务处理权。
```

#### Heartbeat

```text
Worker 周期性证明自己仍在执行任务。
```

#### Checkpoint

```text
记录长任务执行到了哪个阶段。
```

#### Graceful Shutdown

```text
收到正常停止信号后停止领取新任务，并尽量安全完成当前任务。
```

#### Crash Recovery

```text
Worker 被 kill、OOM、进程崩溃后，系统依然能够重新处理任务。
```

### 必须在代码中找到

- [ ] Job 如何从 Pending 进入 Processing
- [ ] WorkerId / LockedBy
- [ ] LockedUntil / LeaseExpiry
- [ ] 原子领取任务的实现
- [ ] 两个 Worker 是否可能同时领取同一任务
- [ ] Heartbeat 更新机制
- [ ] Lease 续租机制
- [ ] Lease 过期任务如何重新发现
- [ ] Checkpoint 是否持久化
- [ ] Worker 重启后如何恢复 Attempt
- [ ] Graceful Shutdown 与强制 Crash 是否分别验证

### 重点

下面两种测试不能互相替代：

```text
Ctrl+C / SIGTERM
```

与：

```text
docker kill
kill -9
OOM Kill
```

---

## 4.5 Retry、Backoff、Jitter 与 Error Classification

### 错误分类

#### Transient Failure

通常可能恢复：

```text
429
502
503
短暂连接失败
临时 DNS 故障
Broker 暂时不可用
```

#### Permanent Failure

重试通常没有意义：

```text
非法参数
权限拒绝
不支持的消息版本
业务状态非法
```

#### Unknown Outcome

请求结果无法确认。

这是 Phase 3 的重点，Phase 2 只需保留可扩展的错误表达能力。

### 必须在代码中找到

- [ ] `RetryPolicy`
- [ ] `ErrorCategory`
- [ ] 最大重试次数
- [ ] Backoff 计算
- [ ] Jitter 计算
- [ ] Retry Attempt 是否持久化
- [ ] Worker 重启后是否保留 Attempt
- [ ] Permanent Failure 是否跳过无意义重试
- [ ] Retry Exhaustion 后进入什么状态
- [ ] 是否存在无限重试路径
- [ ] 是否存在大量 Worker 同步重试的风险

### 示例

```text
Attempt 1 → 1 秒
Attempt 2 → 2 秒
Attempt 3 → 4 秒
Attempt 4 → 8 秒
```

Jitter 用于让不同 Worker 的实际重试时间产生随机偏移，避免同时冲击下游。

---

## 4.6 Poison Message、DLQ 与 Replay

### Poison Message 示例

```text
Payload 无法反序列化
缺少必需字段
消息版本不支持
违反稳定业务不变量
同一代码缺陷导致每次都失败
```

### 必须在代码中找到

- [ ] Retry Exhaustion 后是否进入 DLQ
- [ ] 原始 Payload 是否保存
- [ ] MessageId 是否保存
- [ ] CorrelationId / CausationId 是否保存
- [ ] 最后一次错误是否保存
- [ ] Attempt Count 是否保存
- [ ] Dead-letter 是否可查询
- [ ] Dead-letter 是否可审计
- [ ] Replay 是否要求明确操作
- [ ] Replay 是否可能绕过 Inbox
- [ ] Replay 是否生成新 MessageId
- [ ] 修复前重复 Replay 是否会造成噪声或事故

### CLI 目标

```text
jobs inspect
jobs retry
deadletter list
deadletter replay
```

---

## 4.7 Backpressure

### 必须理解

如果消息进入速度持续高于处理速度，队列会增长。

不能只通过无限增加并发解决，因为可能压垮：

```text
PostgreSQL
Provider
Queue
CPU
Memory
Network
```

### 必须检查

- [ ] 每个 Handler 是否有独立并发限制
- [ ] Batch Size 是否有限制
- [ ] Queue Polling 是否可控
- [ ] 下游变慢时是否继续无上限领取任务
- [ ] Backlog 增长是否可观察
- [ ] Oldest Message Age 是否可获取
- [ ] Broker 恢复后是否出现重试风暴

---

# 5. Phase 2 必做故障实验

> 至少第一次由 Owner 本人执行命令并观察结果。  
> Agent 可以生成脚本，但不能只让 Agent 告诉你“测试通过”。

---

## Experiment 1 — DB Commit 后 Publisher Crash

### 假设

业务事务提交后，即使 Publisher 在发送消息前崩溃，Outbox 消息也不会丢失。

> 2026-08-03 已执行：**PASS（E2）**。
>
> Evidence：`docs/evidence/phase-2/exp1-publisher-crash.md`
>
> 使用真实 PostgreSQL 17 + LocalStack 3，在真实 SQS Send 前暂停并停止
> Publisher Host；重启后消息恢复发布，最终业务结果只有一份。

### 步骤

- [x] 创建一笔业务数据
- [x] 确认业务数据与 Outbox 同事务提交
- [x] 在 Publisher Publish 前强制终止
- [x] 确认 Outbox 仍为 Pending
- [x] 重启 Publisher
- [x] 确认消息最终发布
- [x] 确认业务结果没有重复

### PASS 条件

```text
Business Data 存在
OutboxMessage 存在
重启后消息成功发布
无静默丢失
```

---

## Experiment 2 — Duplicate Publish

### 假设

同一个 Outbox 消息被重复发布，不会产生重复业务结果。

### 步骤

- [ ] 强制同一 Outbox Message 发布两次
- [ ] 检查 Queue 是否收到重复消息
- [ ] 检查 Consumer 是否触发两次
- [ ] 检查最终业务数据
- [ ] 检查 Inbox / Dedup 记录

### PASS 条件

```text
允许消息重复
不允许业务副作用重复
```

---

## Experiment 3 — Duplicate Delivery

### 假设

同一 MessageId 被重复投递时，Inbox 或业务唯一约束会阻止重复结果。

### 步骤

- [ ] 同一 MessageId 投递两次
- [ ] 尝试并发投递
- [ ] 检查两个 Worker 是否都进入处理路径
- [ ] 检查数据库最终状态
- [ ] 检查第二次处理的结果和日志

### PASS 条件

```text
最终只有一个业务结果
重复处理有可解释记录
无竞态导致的双写
```

---

## Experiment 4 — Worker Crash

### 假设

Worker 收到消息后非正常崩溃，消息会重新出现并由其他 Worker 处理。

### 步骤

- [ ] Worker Receive 消息
- [ ] 在 ACK 前 `docker kill`
- [ ] 等待 Visibility Timeout
- [ ] 启动另一个 Worker
- [ ] 确认消息 Redelivery
- [ ] 确认任务最终完成
- [ ] 确认业务副作用没有重复

### PASS 条件

```text
消息重新投递
任务最终恢复
业务结果不重复
```

---

## Experiment 5 — Lease Expiry

### 假设

Worker 崩溃后 Lease 过期，其他 Worker 可以接管 Job。

### 步骤

- [ ] Worker A 获取 Job
- [ ] Job 进入 Processing
- [ ] 强制终止 Worker A
- [ ] 等待 Lease 到期
- [ ] Worker B 扫描过期 Job
- [ ] Worker B 接管并完成
- [ ] 检查 Attempt 和状态转换

### PASS 条件

```text
Job 不永久卡在 Processing
只有一个有效 Owner
任务最终完成
```

---

## Experiment 6 — Poison Message

### 假设

坏消息不会无限重试，也不会阻塞正常消息。

### 步骤

- [ ] 投递一个无法反序列化或违反版本约束的消息
- [ ] 同时投递正常消息
- [ ] 检查 Retry 次数
- [ ] 检查坏消息进入 DLQ
- [ ] 检查正常消息正常完成
- [ ] 检查 Dead-letter 信息是否完整

### PASS 条件

```text
Poison Message 进入 DLQ
正常消息不受阻塞
错误可审计
```

---

## Experiment 7 — Retry Exhaustion

### 假设

Transient Failure 达到最大次数后停止自动重试。

### 步骤

- [ ] 让 Handler 持续返回可重试错误
- [ ] 记录每次 Attempt
- [ ] 记录 Backoff / Jitter
- [ ] 等待达到最大次数
- [ ] 检查最终状态或 DLQ
- [ ] 确认没有继续重试

### PASS 条件

```text
Retry 有上限
Attempt 可审计
最终状态明确
```

---

## Experiment 8 — Broker Temporarily Unavailable

### 假设

Broker 暂时不可用时，业务事务和 Outbox 状态不会丢失；Broker 恢复后继续发送。

### 步骤

- [ ] 停止 LocalStack / Queue
- [ ] 创建业务请求
- [ ] 检查 Business Data 与 Outbox
- [ ] 检查 Publisher 错误分类
- [ ] 恢复 Broker
- [ ] 确认 Outbox 继续发布
- [ ] 检查是否出现重试风暴

### PASS 条件

```text
业务状态保留
Outbox 消息保留
Broker 恢复后继续发布
无静默丢失
无无限重试
```

---

## Experiment 9 — Graceful Shutdown

### 假设

正常停止时，Worker 停止领取新任务，并安全完成或释放当前任务。

### 步骤

- [ ] 启动长任务
- [ ] 发送 SIGTERM / Ctrl+C
- [ ] 检查是否停止 Receive 新消息
- [ ] 检查当前任务如何结束
- [ ] 检查 Lease、ACK、Checkpoint
- [ ] 重启后检查任务状态

### PASS 条件

```text
无新任务继续进入
当前任务行为明确
无任务静默丢失
```

---

## Experiment 10 — Backlog Growth and Recovery

### 假设

当生产速度超过处理速度时，系统可以观察 backlog，并在负载下降后恢复。

### 步骤

- [ ] 使用脚本快速发布大量消息
- [ ] 限制 Worker 并发
- [ ] 观察 Queue Depth
- [ ] 观察 Oldest Message Age
- [ ] 观察数据库和 Worker 负载
- [ ] 恢复正常容量
- [ ] 测量 Backlog 清空时间

### PASS 条件

```text
Backlog 可观察
系统未失控
恢复后队列最终清空
无大量失败或重复结果
```

---

# 6. Evidence 目录建议

```text
evidence/
└── phase-2/
    ├── gate-summary.md
    ├── test-matrix.md
    ├── outbox-publisher-crash/
    │   ├── commands.md
    │   ├── expected.md
    │   ├── logs.txt
    │   ├── database-before.txt
    │   ├── database-after.txt
    │   └── result.json
    ├── duplicate-publish/
    ├── duplicate-delivery/
    ├── worker-crash-redelivery/
    ├── lease-expiry/
    ├── poison-message-dlq/
    ├── retry-exhaustion/
    ├── broker-unavailable/
    ├── graceful-shutdown/
    └── backlog-recovery/
```

每个实验至少保存：

- [ ] 运行命令
- [ ] 开始时间
- [ ] 初始状态
- [ ] 故障注入动作
- [ ] 关键日志
- [ ] 数据库最终状态
- [ ] Queue / DLQ 最终状态
- [ ] 实际结果
- [ ] PASS / FAIL
- [ ] Known Limitation

---

# 7. Owner 代码审查清单

## Outbox

- [ ] Business Data 与 Outbox 同事务
- [ ] Outbox 有稳定 MessageId
- [ ] Publisher 可重复扫描
- [ ] Publisher Crash 不丢消息
- [ ] 重复 Publish 可被下游安全处理
- [ ] Outbox 状态转换明确
- [ ] 失败原因可审计

## Queue Adapter

- [ ] 不向 Application 泄漏具体 SQS SDK
- [ ] Receive / Complete / Abandon / DeadLetter 语义明确
- [ ] Visibility Timeout 可配置
- [ ] Message metadata 完整
- [ ] Queue 异常有稳定分类
- [ ] Phase 3 Provider 逻辑没有写入 Queue Adapter

## Worker Host

- [ ] Handler 之间有独立并发控制
- [ ] Handler 之间失败不会静默串扰
- [ ] CancellationToken 正确传递
- [ ] Graceful Shutdown 明确
- [ ] 非正常 Crash 有恢复路径
- [ ] WorkerId 可追踪
- [ ] Job 状态可持久化

## Inbox / Idempotency

- [ ] MessageId 唯一
- [ ] Inbox 有数据库唯一约束
- [ ] Inbox 与业务副作用同事务
- [ ] 并发重复消息已验证
- [ ] 重复消息不会重复外部副作用

## Retry

- [ ] Transient / Permanent 分类
- [ ] 最大次数明确
- [ ] Backoff 明确
- [ ] Jitter 存在
- [ ] Retry Attempt 持久化
- [ ] Retry Exhaustion 有终态
- [ ] 无无限重试

## DLQ

- [ ] 原始 Payload 可审计
- [ ] MessageId / CorrelationId 保存
- [ ] 错误原因保存
- [ ] Replay 需要明确操作
- [ ] Replay 不绕过幂等保护
- [ ] Poison Message 不阻塞正常消息

## Lease / Heartbeat / Checkpoint

- [ ] Lease 原子获取
- [ ] Lease 有过期时间
- [ ] Heartbeat 可更新
- [ ] Heartbeat 失败行为明确
- [ ] Lease 过期可接管
- [ ] Checkpoint 持久化
- [ ] Worker Crash 后恢复已验证

---

# 8. Phase 2 口头验收题

不看文档回答。

## Q1

为什么数据库和 Queue 不能靠两次普通调用保证一致性？

应覆盖：

```text
两个独立系统
没有共享事务
数据库成功而消息发送失败
Transactional Outbox
```

---

## Q2

为什么 At-least-once Delivery 一定要求幂等？

应覆盖：

```text
ACK 前崩溃
Visibility Timeout
Redelivery
消息可重复
业务结果不能重复
```

---

## Q3

为什么 Inbox 不能只做一次普通 SELECT？

应覆盖：

```text
并发竞态
两个 Worker 同时看到不存在
数据库唯一约束
事务边界
```

---

## Q4

Worker 在哪些时间点崩溃，结果有什么区别？

至少解释：

```text
领取前
领取后、处理前
数据库提交前
数据库提交后、ACK 前
ACK 后
```

---

## Q5

Lease 和 Visibility Timeout 有什么区别？

基本答案：

```text
Visibility Timeout：
Queue 控制消息多久以后重新可见。

Lease：
应用内部控制某个 Job 当前属于哪个 Worker，以及何时允许接管。
```

---

## Q6

哪些错误应该 Retry，哪些错误不应该？

应覆盖：

```text
Transient
Permanent
Poison Message
Retry Exhaustion
```

---

## Q7

为什么不能声称 Exactly-once Delivery？

应覆盖：

```text
底层仍可能重复投递
真正实现的是 Exactly-once Business Effect
```

---

## Q8

如何证明 Worker Crash 后真的恢复？

应覆盖：

```text
消息重新可见
Lease 过期
新 Worker 接管
最终状态正确
业务结果不重复
日志和数据库证据
恢复时间
```

---

## Q9

为什么 Retry 必须有 Jitter？

应覆盖：

```text
防止大量 Worker 在相同时间同时重试
避免 Thundering Herd / Retry Storm
```

---

## Q10

为什么 Poison Message 必须进入 DLQ？

应覆盖：

```text
避免无限重试
避免阻塞正常消息
保留审计和修复后 Replay 能力
```

---

# 9. Phase 2 Gate

只有以下全部成立，才进入 Phase 3。

## Functional Gate

- [ ] API → DB → Outbox → Queue → Worker 链路运行
- [ ] Queue Adapter 可以在 LocalStack SQS 工作
- [ ] Inbox / Idempotency 生效
- [ ] Job / Attempt / Lease / Heartbeat 数据可查询
- [ ] Retry 与 DLQ 工作
- [ ] CLI 可以查询 Job 和 Dead-letter

## Reliability Gate

- [ ] DB 状态和 Outbox 原子提交
- [ ] Duplicate Publish 不产生重复业务结果
- [ ] Duplicate Delivery 不产生重复业务结果
- [ ] Worker Crash 后任务可恢复
- [ ] Lease Expiry 后其他 Worker 可接管
- [ ] Poison Message 不阻塞正常队列
- [ ] Retry 有分类、上限、Backoff 和 Jitter
- [ ] Retry Exhaustion 有明确终态
- [ ] Dead-letter 可审计和受控 Replay
- [ ] Broker 暂时不可用时消息不会静默丢失
- [ ] Graceful Shutdown 有独立 Evidence
- [ ] Backlog Growth 和 Recovery 有测试

## Evidence Gate

- [ ] 每个实验有运行命令
- [ ] 每个实验有实际日志
- [ ] 每个实验有数据库最终状态
- [ ] 每个实验有 Queue / DLQ 最终状态
- [ ] 每个实验有 PASS / FAIL
- [ ] 每个实验可由第三方复验
- [ ] LocalStack Evidence 明确标记为 E2
- [ ] 不把 Mock 或单元测试冒充集成证据

## Owner Knowledge Gate

- [ ] 能解释 At-least-once
- [ ] 能解释 ACK 与 Visibility Timeout
- [ ] 能解释 Outbox
- [ ] 能解释 Inbox
- [ ] 能解释 Exactly-once Business Effect
- [ ] 能解释 Lease / Heartbeat / Checkpoint
- [ ] 能解释 Retry 分类
- [ ] 能解释 Backoff / Jitter
- [ ] 能解释 Poison Message / DLQ
- [ ] 能解释 Worker Crash 的故障窗口
- [ ] 能指出代码中的事务提交点
- [ ] 能指出代码中的 ACK 点

---

# 10. Phase 3 Readiness Check

进入 Phase 3 前，不需要实现以下功能，但 Phase 2 不能把架构写死。

- [ ] Message 有稳定 MessageId
- [ ] 有 CorrelationId
- [ ] 有 CausationId 或明确的因果关系表达
- [ ] JobAttempt 可持久化
- [ ] Handler 可以表达不同 Error Category
- [ ] Queue Adapter 不耦合具体 Provider
- [ ] Reconciliation Handler 可以继续扩展
- [ ] Processing 状态机允许新增 Unknown 路径
- [ ] Retry 逻辑可以区分 Unknown Outcome
- [ ] Checkpoint 可以记录 Provider 阶段
- [ ] Phase 3 可以增加 ProviderReference
- [ ] Phase 3 可以增加 ReconciliationRecord

---

# 11. 当前无需深入学习的 Phase 3 内容

以下内容可以在 Phase 3 再正式学习：

```text
Provider Timeout
Provider Idempotency
Unknown Outcome
ProviderReference
Callback
Callback Signature
Replay Protection
Reconciliation
Circuit Breaker
Retry Budget
Provider Processed but Response Lost
```

Phase 3 的核心主线：

```text
Timeout 不等于 Failed
→ 结果可能 Unknown
→ 不盲目 Retry
→ 查询 Provider 实际状态
→ Reconciliation 让状态最终收敛
```

---

# 12. 推荐推进顺序

## Step 1 — Freeze

- [ ] 停止新增功能
- [ ] 创建 Phase 2 分支
- [ ] 保存当前工作
- [ ] 整理 Commit

## Step 2 — Review Packet

- [ ] Agent 输出完成范围
- [ ] Agent 输出代码地图
- [ ] Agent 输出测试地图
- [ ] Agent 输出 Failure Scenario
- [ ] Agent 输出 Known Risks

## Step 3 — Learn

按顺序学习：

```text
At-least-once / ACK / Redelivery
→ Outbox
→ Inbox / Idempotency
→ Worker Crash / Lease
→ Retry / Backoff / Jitter
→ Poison Message / DLQ
→ Backpressure
```

## Step 4 — Verify

- [ ] 亲自执行核心故障实验
- [ ] 检查事务点
- [ ] 检查 ACK 点
- [ ] 检查状态机
- [ ] 检查数据库约束
- [ ] 检查实际 Evidence

## Step 5 — Oral Review

- [ ] 完成十道口头题
- [ ] 能画完整消息链路
- [ ] 能画五个 Worker Crash 窗口
- [ ] 能说明每个机制防什么、不防什么

## Step 6 — Gate Decision

选择其一：

```text
ACCEPT
Phase 2 完整通过，可以进入 Phase 3。

VALIDATION
代码基本完成，但仍缺少实验、证据或 Owner 理解。

BLOCKED
核心不变量、恢复机制或测试存在缺陷。
```

---

# 13. Phase 2 Gate Decision Template

```markdown
# Phase 2 Gate Decision

## Decision

ACCEPT / VALIDATION / BLOCKED

## Date

YYYY-MM-DD

## Owner

Name

## Scope Completed

- ...

## Experiments Passed

- ...

## Experiments Failed or Missing

- ...

## Core Invariants

- [ ] Business Data 与 Outbox 同事务
- [ ] Duplicate Message 不产生 Duplicate Business Effect
- [ ] Worker Crash 后任务恢复
- [ ] Retry 有分类和上限
- [ ] Poison Message 进入 DLQ
- [ ] Dead-letter 可审计和 Replay

## Known Limitations

- ...

## Open Risks

- ...

## Evidence Index

- ...

## Phase 3 Entry Decision

Approved / Not Approved

## Owner Notes

...
```

---

# 14. 最终能力目标

Phase 2 完成后，你不需要已经成为 SRE。

你需要真正获得下面这一项能力：

> 能够判断一个 At-least-once 异步 Worker 系统，在数据库提交、消息重复、Broker 暂时不可用和 Worker 崩溃的情况下，是否会丢任务、重复产生业务结果，或者永久卡住。

做到这一点，Phase 2 才属于你，而不是只属于 Agent。
