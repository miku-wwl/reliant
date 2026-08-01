# Reliant 学习笔记：Transactional Outbox、SQS、Worker 与支付幂等

> 本笔记基于最初学习文档和本次多轮讨论整理。  
> 为了更容易理解，正文多数地方暂时使用 `Payment`；回到 Reliant 项目代码时，请将其映射回 `Contribution`。

---

## 1. 术语映射

| 学习时使用 | Reliant 项目中使用 | 含义 |
|---|---|---|
| `Payment` | `Contribution` | 一笔需要异步处理的业务记录 |
| `PaymentId` | `ContributionId` | 业务记录 ID |
| `Publisher` | Outbox Publisher | 从 Outbox 读取消息并发送到 SQS |
| `Worker` | Consumer | 从 SQS 获取消息并处理业务 |
| `Provider` | External Provider | 真正执行支付、缴费或外部业务动作的系统 |

当前可以把 `Contribution` 暂时理解成一笔 `Payment`。

---

# 第一部分：整体链路

## 2. 完整主链路

```text
用户请求
→ API
→ PostgreSQL：Payment + Outbox
→ Outbox Publisher
→ SQS
→ Worker
→ Payment Provider
→ PostgreSQL：更新 Payment + 写 Inbox
→ SQS Delete / ACK
```

各组件职责：

```text
API        ：登记业务
Publisher  ：传递任务
SQS        ：保存和投递任务
Worker     ：执行任务
Provider   ：真正处理支付
PostgreSQL ：保存业务事实、消息状态和处理历史
```

一句话记忆：

> API 记账，Publisher 传话，Worker 办事，Provider 真正处理支付。

---

# 第二部分：数据库事务与 Dual Write

## 3. 普通数据库事务能管理什么

Spring 中：

```java
@Transactional
public void createPayment() {
    paymentRepository.save(payment);
    accountRepository.updateBalance(accountId);
    auditRepository.save(auditRecord);
}
```

只要这些操作由同一个数据库事务管理器控制，它们可以：

```text
一起 COMMIT
或
一起 ROLLBACK
```

普通数据库事务通常管理：

- 同一数据库或同一数据源中的多次 CRUD；
- 多张表；
- 多个 Spring Data Repository 操作；
- `INSERT`、`UPDATE`、`DELETE` 等数据库修改。

它不能自动管理方法中的所有代码。

例如：

```java
@Transactional
public void createPayment() {
    paymentRepository.save(payment);
    sqsClient.sendMessage(message);
}
```

其中：

```text
paymentRepository.save() → PostgreSQL
sqsClient.sendMessage()   → AWS SQS
```

`@Transactional` 只能回滚 PostgreSQL，不能撤回已经进入 SQS 的消息。

---

## 4. 什么是 Dual Write

一个业务操作需要同时写两个独立系统：

```text
PostgreSQL
+
SQS
```

系统希望：

```text
要么都成功
要么都失败
```

但实际上可能出现：

### 场景一：数据库成功，SQS 失败

```text
Payment 保存成功
SQS 发送失败
Worker 永远不知道这笔任务
```

结果：

```text
Payment 一直卡在 Created / Pending
```

### 场景二：SQS 成功，数据库失败

```text
消息进入 SQS
Payment 保存失败
Worker 收到消息
但数据库里找不到 Payment
```

交换执行顺序不能解决 Dual Write。

---

# 第三部分：Transactional Outbox

## 5. Outbox 是什么

Outbox 可以理解成 PostgreSQL 中的一张：

> 可靠的待发送消息清单。

创建 Payment 时，不直接向 SQS 发送，而是在同一个数据库事务中写：

```text
Payment
+
OutboxMessage
```

例如：

```java
@Transactional
public void createPayment() {
    paymentRepository.save(payment);
    outboxRepository.save(outboxMessage);
}
```

结果只能是：

```text
Payment 有，Outbox 也有
```

或者：

```text
Payment 没有，Outbox 也没有
```

不会出现：

```text
Payment 已保存
但系统完全忘记要发送消息
```

---

## 6. Outbox 保存什么

Outbox 不只记录状态，还保存要发送的消息本身。

典型字段：

```text
Id
MessageType
Payload
OccurredAt
Status
PublishedAt
AttemptCount
NextAttemptAt
ClaimedBy
ClaimedUntil
LastError
```

示例：

```text
MessageId     = msg_001
MessageType   = PaymentProcessingRequested
Payload       = {"paymentId":"pay_123"}
Status        = Pending
PublishedAt   = null
AttemptCount  = 0
```

Outbox 同时保存：

```text
消息内容：要发送什么
消息状态：是否发送成功、失败几次、何时重试
```

---

## 7. API 与 Publisher 的分工

API：

```text
接收请求
→ 保存 Payment
→ 保存 Outbox
→ 返回
```

Publisher：

```text
扫描未发布 Outbox
→ Claim
→ 发送到 SQS
→ 标记 Published
```

严格来说：

> 真正向 SQS 发送消息的 Producer，通常是 Outbox Publisher，而不是 API 本身。

API 和 Publisher 都不直接调用 Payment Provider。

---

## 8. Outbox 能保证什么

Outbox 保证：

> 只要 Payment 已经成功提交，数据库中就一定存在一个持久化的“需要发送消息”的意图。

即使：

- API 进程崩溃；
- Publisher 崩溃；
- SQS 暂时不可访问；
- 网络中断；
- Worker 没启动；

这条发送意图仍然保存在 PostgreSQL。

Publisher 恢复后可以继续发送。

一句话：

> SQS 出问题时，业务先记账；SQS 恢复后，再补发。

---

## 9. Outbox 不保证 Exactly Once

危险窗口：

```text
1. Publisher 发送消息到 SQS
2. SQS 已经接收成功
3. Publisher 还没更新 PublishedAt
4. Publisher 崩溃
```

数据库仍然显示：

```text
PublishedAt = null
```

Publisher 重启后会再次发送。

因此：

```text
同一个逻辑事件可能进入 SQS 多次
```

Outbox 通常保证的是：

```text
At-least-once publication
```

不是：

```text
Exactly-once publication
```

一句话：

> Outbox 宁可重复发送，也不能悄悄丢消息。

---

# 第四部分：Publisher 并发、租约与重试

## 10. 多个 Publisher 为什么会重复发送

如果 Publisher A 和 Publisher B 同时执行：

```sql
SELECT *
FROM outbox
WHERE published_at IS NULL;
```

两者可能同时读到同一条记录，然后都发送到 SQS。

因此 Publisher 需要抢占 Outbox。

---

## 11. 原子条件更新

不要：

```text
先 SELECT
→ 业务代码判断
→ 再 UPDATE
```

因为两条 SQL 中间存在并发窗口。

应该将判断和修改放进同一条 SQL：

```sql
UPDATE outbox
SET status = 'Claimed',
    claimed_by = :publisherId,
    claimed_until = NOW() + INTERVAL '1 minute'
WHERE id = :messageId
  AND (
      status = 'Pending'
      OR claimed_until < NOW()
  );
```

返回：

```text
更新 1 行 → 抢占成功
更新 0 行 → 没抢到
```

英文：

```text
atomic conditional update
```

也常见：

```text
conditional update
compare-and-set update
optimistic concurrency control
```

---

## 12. Spring Data JPA 示例

```java
public interface OutboxRepository
        extends JpaRepository<OutboxMessage, UUID> {

    @Modifying
    @Query("""
        UPDATE OutboxMessage o
        SET o.status = 'CLAIMED',
            o.claimedBy = :publisherId,
            o.claimedUntil = :claimedUntil
        WHERE o.id = :messageId
          AND (
              o.status = 'PENDING'
              OR o.claimedUntil < CURRENT_TIMESTAMP
          )
    """)
    int claim(
        @Param("messageId") UUID messageId,
        @Param("publisherId") String publisherId,
        @Param("claimedUntil") Instant claimedUntil
    );
}
```

调用：

```java
int affectedRows =
    outboxRepository.claim(messageId, publisherId, claimedUntil);

if (affectedRows == 0) {
    return; // 没抢到
}

// affectedRows == 1，当前 Publisher 获得发送权
```

普通 `save()` 返回实体对象，不直接返回影响行数：

```java
Payment saved = paymentRepository.save(payment);
```

自定义 `@Modifying UPDATE` 可以返回影响行数：

```java
int updatedRows = repository.claim(...);
```

---

## 13. 为什么需要 ClaimedUntil

如果只有：

```text
Status = Claimed
```

Publisher 抢到后崩溃，这条消息可能永久卡死。

因此需要租约：

```text
ClaimedBy
ClaimedUntil
```

例如：

```text
ClaimedBy    = publisher-A
ClaimedUntil = 12:05
```

12:05 之前，其他 Publisher 不处理。

12:05 之后，如果仍未发布，其他 Publisher 可以重新 Claim。

一句话：

> Claim 不能永久占有，必须有过期时间。

---

## 14. Outbox 发送失败后的 Retry

Publisher 发送 SQS 失败时，不能标记为 Published。

应该记录：

```text
AttemptCount = 1
LastError    = "SQS timeout"
NextAttemptAt = 12:10
Status       = Pending / Failed
```

等待到 `NextAttemptAt` 后重试。

常见 Backoff：

```text
第一次失败 → 10 秒后
第二次失败 → 30 秒后
第三次失败 → 2 分钟后
```

`Backoff`：退避。

目的：

> 避免 SQS、网络或依赖故障时疯狂重试。

---

## 15. Poison Outbox Message

如果某条 Outbox 永远发送失败，例如：

```text
Payload 格式错误
缺少必要字段
消息太大
代码 Bug
权限配置永久错误
```

不能无限重试，也不应该直接删除。

通常：

```text
AttemptCount >= 最大次数
→ Status = Failed / Dead
→ 停止自动重试
→ 保留 Payload 和 LastError
→ 触发告警
→ 人工检查或修复后重新发布
```

它类似 Publisher 侧的 DLQ，但通常保存在 PostgreSQL 中：

```text
Publisher 侧：Failed Outbox / Poison Outbox
Consumer 侧：SQS DLQ
```

一句话：

> 自动处理不了，可以隔离，但不能悄悄丢掉真实业务。

---

# 第五部分：Consumer、Worker 与 SQS

## 16. Worker 的基本流程

```text
Worker 收到 SQS 消息
→ 检查 Inbox
→ 检查 Payment 状态
→ 抢占 Payment
→ 调用 Provider
→ 更新 Payment + 写 Inbox
→ 数据库 COMMIT
→ 最后 Delete / ACK SQS 消息
```

---

## 17. 为什么消息可能被重复消费

重复来源包括：

- Publisher 重复发送；
- SQS 的 at-least-once delivery；
- Visibility Timeout 到期；
- Worker 处理成功但 DeleteMessage 前崩溃；
- 同一 Payment 被错误生成两条不同消息。

因此 Worker 必须按“消息可能重复”来设计。

---

## 18. Visibility Timeout

Worker 收到 SQS 消息后，消息不会立刻删除，而是暂时不可见。

流程：

```text
Worker A 收到消息
→ 消息进入不可见状态
→ Worker A 处理
```

如果 Worker A 在 Delete / ACK 前崩溃：

```text
Visibility Timeout 到期
→ 消息重新可见
→ Worker B 可以再次获取
```

一句话：

> 没有 ACK，超时后消息会重新排队。

---

## 19. Visibility Timeout 太短

假设：

```text
Worker 处理需要 40 秒
Visibility Timeout 只有 10 秒
```

到第 10 秒：

```text
Worker A 仍在处理
消息已经重新可见
Worker B 又拿到同一条消息
```

可能造成两个 Worker 同时处理同一笔 Payment。

因此：

> Visibility Timeout 应大于正常处理时间。

固定、可预测任务：

```text
直接配置一个足够长的 Timeout
```

超长、不可预测任务：

```text
再考虑由 Worker 定期延长 Visibility Timeout
```

延长 Timeout 只是续租，不是 ACK。

---

## 20. ACK / DeleteMessage 必须放在数据库 COMMIT 之后

正确顺序：

```text
调用 Provider
→ 更新 Payment + 写 Inbox
→ 数据库 COMMIT 成功
→ Delete / ACK SQS 消息
```

不能：

```text
先 Delete SQS
→ 再更新数据库
```

否则 Worker 在数据库更新前崩溃时：

```text
SQS 消息已经没了
Payment 又没有更新
任务永久丢失
```

一句话：

> 先安全落库，再 ACK。

---

## 21. Worker 失败时如何重试

处理成功：

```text
COMMIT
→ DeleteMessage
```

处理失败：

```text
不 DeleteMessage
→ Visibility Timeout 到期
→ SQS 再次投递
```

SQS 侧通常配置：

```text
Visibility Timeout
maxReceiveCount
DLQ
```

Worker 侧负责：

```text
成功 → 删除消息
失败 → 不删除
```

超过 `maxReceiveCount` 后：

```text
SQS 将消息移动到 DLQ
```

---

# 第六部分：Inbox 与业务幂等

## 22. Inbox 是什么

Inbox 是 Consumer 侧的“已处理消息清单”。

Worker 收到：

```text
MessageId = msg_001
```

先检查 Inbox：

```text
msg_001 是否已经处理过？
```

如果已经存在：

```text
不再调用 Provider
→ 直接 Delete / ACK 消息
```

如果不存在：

```text
继续处理
```

一句话：

> Outbox 防消息丢失，Inbox 防同一条消息重复消费。

---

## 23. Payment 与 Inbox 应同事务提交

业务成功后：

```text
UPDATE Payment
INSERT InboxMessage
COMMIT
```

必须放在同一个数据库事务里。

结果只能是：

```text
Payment 已更新 + Inbox 已记录
```

或者：

```text
两者都没提交
```

不能出现：

```text
Payment 已完成
Inbox 没记录
```

否则相同消息重来时，Worker 可能再次处理。

---

## 24. 为什么有 Inbox 还要检查 Payment 状态

Inbox 只能防止：

```text
同一个 MessageId 重复
```

但可能存在：

```text
msg_001 → PaymentId = pay_123
msg_002 → PaymentId = pay_123
```

两个 MessageId 不同，Inbox 会认为都是新消息。

因此还要检查 Payment 状态：

```text
Completed  → 不再执行
Pending    → 可以尝试抢占
Processing → 可能有人正在处理
Failed     → 根据错误类型决定是否重试
```

两层防线：

```text
Inbox          → 防同一消息重复
Payment Status → 防同一业务重复
```

一句话：

> 消息可以不同，但 Payment 只能成功一次。

---

# 第七部分：Worker 并发抢占

## 25. 为什么不能先 SELECT 再 UPDATE

不安全写法：

```java
Payment payment = repository.findById(paymentId);

if (payment.getStatus() == PENDING) {
    payment.setStatus(PROCESSING);
    repository.save(payment);
}
```

底层类似：

```sql
SELECT status
FROM payments
WHERE id = 'pay_123';

UPDATE payments
SET status = 'Processing'
WHERE id = 'pay_123';
```

两个 Worker 可能同时读到 `Pending`，然后都继续。

---

## 26. 原子条件更新 Payment

安全写法：

```sql
UPDATE payments
SET status = 'Processing'
WHERE id = 'pay_123'
  AND status = 'Pending';
```

PostgreSQL、MySQL 和 Oracle 都能使用这种方式。

并发时：

```text
Worker A：UPDATE 成功，影响 1 行
Worker B：条件已不满足，影响 0 行
```

数据库自己通过行锁和事务并发控制处理竞争。

---

## 27. Spring Data JPA 示例

```java
public interface PaymentRepository
        extends JpaRepository<Payment, UUID> {

    @Modifying
    @Query("""
        UPDATE Payment p
        SET p.status = 'PROCESSING'
        WHERE p.id = :paymentId
          AND p.status = 'PENDING'
    """)
    int claimPayment(@Param("paymentId") UUID paymentId);
}
```

Service：

```java
@Transactional
public boolean tryClaimPayment(UUID paymentId) {
    int affectedRows =
        paymentRepository.claimPayment(paymentId);

    return affectedRows == 1;
}
```

Worker：

```java
if (!paymentService.tryClaimPayment(paymentId)) {
    return; // 其他 Worker 已经抢到，或 Payment 不再是 Pending
}

processPayment(paymentId);
```

一句话：

> 返回 1 就获得处理权，返回 0 就退出。

---

## 28. Worker 是否需要租约

SQS Visibility Timeout 只能防止消息永久被某个 Worker 占用。

但如果 Worker 已经将：

```text
Payment: Pending → Processing
```

然后崩溃，下一位 Worker 看到 `Processing`，可能一直不敢接手。

更完整的设计可以加入：

```text
ProcessingBy
ProcessingUntil
```

例如：

```text
Status          = Processing
ProcessingBy    = worker-A
ProcessingUntil = 12:05
```

租约过期后，其他 Worker 可以重新接管。

注意：

> 这是我们讨论中的通用工程补充，最初文档主要强调业务状态、并发控制和 Provider 幂等。

---

# 第八部分：Provider 与 Idempotency

## 29. Provider 是什么

Provider 是真正执行外部业务动作的系统，例如：

- 支付网关；
- 银行接口；
- 信用卡处理平台；
- 第三方清算系统；
- 捐款或缴费平台。

在当前简化模型中：

```text
Worker
→ 调用 Provider
→ Provider 扣款
→ Worker 更新 Payment
```

API 和 Publisher 不直接调用 Provider。

---

## 30. Provider 调用为什么不受数据库事务保护

即使这样写：

```java
@Transactional
public void process() {
    provider.charge();
    paymentRepository.save(payment);
}
```

Provider 是外部 HTTP 系统。

数据库事务只能控制 PostgreSQL，不能回滚 Provider 已经执行的扣款。

可能出现：

```text
Provider 已扣款
Worker 写数据库前崩溃
Payment 仍显示未完成
```

消息重试后，如果再次调用 Provider，可能重复扣款。

---

## 31. Idempotency Key

`idempotent`：幂等的。

发音近似：

```text
eye-dem-POH-tent
```

常见词形：

```text
idempotent    形容词：幂等的
idempotency   名词：幂等性
idempotence   名词：幂等性，数学中常见
idempotently  副词，较少使用
```

记忆：

```text
idem   = same
potent = effect / power
```

口诀：

> Idempotent：多做不多算。

---

## 32. Provider Idempotency Key 如何工作

第一次调用：

```text
PaymentId      = pay_123
IdempotencyKey = pay_123
```

Provider 已扣款，但 Worker 在更新数据库前崩溃。

消息重试后，Worker 仍使用：

```text
IdempotencyKey = pay_123
```

Provider 发现这个 Key 已经处理过：

```text
不再重复扣款
→ 返回上一次结果
```

关键：

> 同一笔 Payment 的所有重试，必须使用同一个稳定 Key。

不能每次重试都生成新的 Key。

---

# 第九部分：支付 Token 补充设计

> 本节是我们讨论中补充的通用支付设计，不是最初 Reliant 文档明确规定的内容。

## 33. 为什么后端不保存完整卡号和 CVV

常见流程：

```text
用户在浏览器输入卡号、有效期、CVV
→ Provider 托管的前端组件 / iframe 接收
→ 浏览器直接把卡信息发给 Provider
→ Provider 返回 Token
→ 前端只把 Token 发给我们的 API
```

后端看不到：

```text
完整卡号
CVV
```

后端只看到：

```text
PaymentMethodToken / PaymentMethodId
```

一句话：

> 浏览器收卡，Provider 存卡，后端只拿引用 ID。

---

## 34. 短期 Token 与持久 PaymentMethodId

短期 Token：

```text
一次性
有效期较短
用于安全登记银行卡
```

持久 PaymentMethodId：

```text
由 Provider 保存
可以在之后的 Worker 异步扣款中使用
```

简化流程：

```text
前端获得短期 Token
→ 后端向 Provider 换成持久 PaymentMethodId
→ 写 Payment + Outbox
→ Worker 使用 PaymentMethodId 调 Provider
```

PostgreSQL 通常保存：

```text
PaymentMethodId = pm_abc123
```

而不是卡号和 CVV。

---

## 35. Payment 表可以长什么样

示例：

```text
Payment
- Id
- Amount
- Currency
- Status
- PaymentMethodId
- ProviderPaymentId
- IdempotencyKey
- ProcessingBy
- ProcessingUntil
- CreatedAt
- UpdatedAt
```

说明：

```text
PaymentMethodId   → Provider 中保存的支付方式引用
ProviderPaymentId → Provider 返回的交易 ID
IdempotencyKey    → 防止重复扣款
Status            → Pending / Processing / Completed / Failed
```

---

## 36. 数据库泄露时为什么 Token 仍有价值

`PaymentMethodId` 类似：

```text
储物柜号码
```

Provider API Secret 类似：

```text
储物柜钥匙
```

只拿到 PaymentMethodId，通常还不能调用 Provider 扣款。

攻击者还需要：

- Provider API Key；
- 正确的商户账户权限；
- Worker 或后端调用能力。

因此：

```text
数据库保存 PaymentMethodId
Vault / Secret Manager 保存 Provider API Key
```

这样把风险分离。

---

## 37. Vault / Secret Manager

Worker 通常需要 Provider API Key。

常见方式：

```text
Worker 启动
→ 用自身身份访问 Vault / Secret Manager
→ 获取 Provider API Key
→ 缓存在内存中
→ 调用 Provider
```

或者：

```text
Vault / AWS Secrets Manager / Azure Key Vault
→ 注入环境变量或挂载文件
→ Worker 读取
```

不一定每处理一笔 Payment 都重新访问 Vault。

---

# 第十部分：Crash Matrix

## 38. Crash Matrix 是什么

Crash Matrix：崩溃点矩阵。

用于系统化检查：

```text
在哪一步崩溃？
Payment 有没有？
Outbox 有没有？
SQS 有没有？
系统能否自动恢复？
```

---

## 39. 典型 Crash Matrix

| 崩溃位置 | Payment | Outbox | SQS | 结果 |
|---|---|---|---|---|
| 写数据库前崩溃 | 无 | 无 | 无 | 客户可重试 |
| 写 Payment 后、Commit 前 | 回滚 | 回滚 | 无 | 安全 |
| DB Commit 后、Publisher 前 | 有 | Pending | 无 | Publisher 恢复后补发 |
| SQS Send 前崩溃 | 有 | Pending | 无 | Publisher 重试 |
| SQS Send 后、标记 Published 前 | 有 | Pending | 有 | 可能重复发送 |
| 标记 Published 后 | 有 | Published | 有 | 正常 |
| Worker 收到消息后、调用 Provider 前 | Pending/Processing | Published | 有 | Timeout 后重投 |
| Provider 成功后、数据库 Commit 前 | 未完成/Processing | Published | 有 | 使用 Idempotency Key 重试 |
| DB Commit 后、Delete SQS 前 | Completed | Published | 有 | 重投后 Inbox/状态识别重复 |
| Delete SQS 后 | Completed | Published | 无 | 正常 |

最重要的两个窗口：

```text
SQS 已收到
Outbox 仍 Pending
→ 可能重复发布
```

```text
Provider 已成功
数据库还未 Commit
→ 可能重复调用 Provider
```

对应防线：

```text
Consumer 幂等
Provider Idempotency Key
```

---

# 第十一部分：每层机制解决什么问题

## 40. 防线分层

| 机制 | 主要解决的问题 |
|---|---|
| Database Transaction | 同一数据库内多个修改的一致性 |
| Transactional Outbox | 数据库成功但消息永久丢失 |
| Publisher Claim / Lease | 多 Publisher 同时发送同一 Outbox |
| Retry / Backoff | 临时发送失败 |
| Failed / Poison Outbox | 永久无法发送的 Outbox |
| SQS Visibility Timeout | Worker 崩溃后消息重新投递 |
| SQS DLQ | Consumer 多次处理失败 |
| Inbox | 同一个 MessageId 重复消费 |
| Payment State Machine | 同一业务被重复推进 |
| Atomic Conditional Update | 多 Worker 同时抢同一业务 |
| Processing Lease | Worker 崩溃导致业务状态卡死 |
| Provider Idempotency Key | 外部支付效果重复 |
| ACK after Commit | 防止消息先删除、业务结果丢失 |

---

# 第十二部分：关键区别

## 41. Publisher 与 Worker

```text
Publisher 抢 Outbox
Worker 抢 Payment
```

Publisher 关心：

```text
消息有没有被其他 Publisher 占用？
Outbox 租约有没有过期？
消息是否已发送？
```

Worker 关心：

```text
Payment 是什么状态？
是否已由其他 Worker 处理？
是否可以调用 Provider？
```

一句话：

> Publisher 抢的是消息发送权，Worker 抢的是业务处理权。

---

## 42. Outbox、Inbox 与 DLQ

```text
Outbox
→ 发送侧
→ 保证消息最终进入 MQ
```

```text
Inbox
→ 消费侧数据库
→ 记录 MessageId 是否已处理
```

```text
SQS DLQ
→ 消费侧队列
→ 保存多次处理失败的消息
```

```text
Failed / Poison Outbox
→ Publisher 侧隔离区
→ 保存多次发送失败的消息
```

---

## 43. MessageId 与 PaymentId

```text
MessageId
→ 标识一条消息
→ Inbox 用它去重
```

```text
PaymentId
→ 标识一笔业务
→ Payment 状态与 Provider Idempotency 用它保护
```

可能存在：

```text
msg_001 → pay_123
msg_002 → pay_123
```

因此不能只依赖 Inbox。

---

# 第十三部分：可靠处理顺序

## 44. Producer 侧 Happy Path

```text
1. API 接收请求
2. 创建 Payment
3. 创建 Outbox
4. 同一数据库事务 COMMIT
5. Publisher 扫描 Outbox
6. Publisher Claim
7. 发送 SQS
8. 更新 Outbox 为 Published
```

---

## 45. Consumer 侧 Happy Path

```text
1. Worker 从 SQS 获取消息
2. 检查 Inbox
3. 检查 Payment 状态
4. 原子 Claim Payment
5. 使用稳定 Idempotency Key 调 Provider
6. Provider 返回成功
7. 同一数据库事务更新 Payment + 写 Inbox
8. COMMIT
9. Delete / ACK SQS 消息
```

---

# 第十四部分：Reliant 代码 Review 清单

> 这部分适合之后与 Agent 一起对照实际代码检查。

## 46. Producer / API

- [ ] Payment 与 Outbox 是否在同一个数据库事务中？
- [ ] 是否在同一个 `SaveChangesAsync()` 中提交？
- [ ] API 是否错误地直接发送 SQS？
- [ ] Outbox Payload 是否包含稳定的 MessageId 和 PaymentId？
- [ ] API 重试是否可能创建两笔相同 Payment？

---

## 47. Outbox Publisher

- [ ] 是否只查询未发布或到期可重试记录？
- [ ] 是否使用原子 Claim、Lease 或 `FOR UPDATE SKIP LOCKED`？
- [ ] 是否有 `ClaimedBy`？
- [ ] 是否有 `ClaimedUntil`？
- [ ] 发送成功后是否更新 `PublishedAt`？
- [ ] 发送失败后是否增加 `AttemptCount`？
- [ ] 是否记录 `LastError`？
- [ ] 是否有 `NextAttemptAt` 和 Backoff？
- [ ] 是否有最大重试次数？
- [ ] Poison Outbox 是否隔离并告警？
- [ ] 是否接受“发送成功但标记 Published 前崩溃”导致重复发布？

---

## 48. SQS

- [ ] Visibility Timeout 是否大于正常处理时长？
- [ ] Worker 处理失败时是否不 DeleteMessage？
- [ ] 是否配置 `maxReceiveCount`？
- [ ] 是否配置 DLQ？
- [ ] 是否监控 ApproximateAgeOfOldestMessage？
- [ ] 是否监控积压消息数量？

---

## 49. Worker

- [ ] 是否先检查 Inbox？
- [ ] 是否检查 Payment 状态？
- [ ] 是否使用原子条件更新抢占 Payment？
- [ ] 是否避免先 SELECT 再普通 UPDATE？
- [ ] 是否使用稳定 Provider Idempotency Key？
- [ ] Provider 成功后，Payment 与 Inbox 是否同事务提交？
- [ ] 是否在数据库 COMMIT 后才 Delete / ACK SQS？
- [ ] Processing 状态是否可能永久卡死？
- [ ] 是否需要 `ProcessingBy / ProcessingUntil`？

---

## 50. Provider

- [ ] 是否支持 Idempotency Key？
- [ ] 同一 Payment 的重试是否使用相同 Key？
- [ ] Provider API Key 是否保存在 Vault / Secret Manager？
- [ ] Worker 是否使用最小权限？
- [ ] 是否记录 ProviderPaymentId？
- [ ] 网络超时时是否能查询上一次请求结果？

---

# 第十五部分：面试表达

## 51. 中文表达

> PostgreSQL 与 SQS 之间不存在一个普通的原子事务。为了避免 Payment 已经提交但消息永久丢失，我将 Payment 与 OutboxMessage 放在同一个数据库事务中提交，再由独立 Publisher 执行至少一次发布。由于 Publisher 可能在 SQS 已接收消息但更新 PublishedAt 前崩溃，下游 Worker 仍需要通过 Inbox、业务状态机、原子条件更新以及稳定的 Provider Idempotency Key 抑制重复副作用。Worker 只有在数据库业务结果提交成功后，才会删除 SQS 消息。

---

## 52. 英文表达

> PostgreSQL and SQS do not share a normal atomic transaction. To avoid losing the processing intent after the payment record has been committed, I persist the Payment and the OutboxMessage in the same database transaction. A separate publisher performs at-least-once publication to SQS. Since the publisher may crash after SQS accepts the message but before `PublishedAt` is updated, the consumer remains idempotent through an Inbox, business-state validation, atomic conditional updates, and a stable provider idempotency key. The worker deletes the SQS message only after the business transaction has committed successfully.

---

# 第十六部分：核心口诀

## 53. 最短记忆版

```text
Payment + Outbox 同事务
Publisher 负责发 MQ
Outbox 保证不丢，但可能重复
SQS 没 ACK，超时后重投
Inbox 防同一消息重复
Payment 状态防同一业务重复
原子 UPDATE 防多个 Worker 同时抢
Provider Idempotency Key 防重复扣款
Payment + Inbox 同事务
数据库 COMMIT 后再 ACK
```

---

## 54. 一句话总览

> Transactional Outbox 负责不丢消息，SQS 负责至少一次投递，Inbox 和 Payment 状态负责识别重复，原子条件更新负责并发抢占，Provider Idempotency Key 负责避免外部重复扣款，最后通过 Commit 后 ACK 实现可接受的 effectively-once business effect。

---

# 第十七部分：自测题

1. 为什么 `@Transactional` 无法同时管理 PostgreSQL 和 SQS？
2. Payment 与 Outbox 为什么必须同事务提交？
3. Outbox 为什么仍然可能重复发送？
4. Publisher 抢到 Outbox 后崩溃，如何防止记录永久卡死？
5. 为什么 Visibility Timeout 不能设置得太短？
6. 为什么 Worker 失败时不应该 DeleteMessage？
7. Inbox 防的是什么重复？
8. 为什么有 Inbox 仍然需要检查 Payment 状态？
9. 为什么要用一条 `UPDATE ... WHERE status = 'Pending'`？
10. Provider 已扣款但数据库 Commit 前崩溃，靠什么防止重复扣款？
11. 为什么 ACK 必须放在数据库 COMMIT 之后？
12. Poison Outbox 与 SQS DLQ 有什么区别？

---

## 55. 最终架构图

```text
                        ┌──────────────────────┐
                        │      PostgreSQL      │
                        │                      │
User → API ───────────→ │ Payment              │
                        │ OutboxMessage         │
                        └──────────┬───────────┘
                                   │
                                   │ scan + claim lease
                                   ▼
                         ┌─────────────────────┐
                         │  Outbox Publisher   │
                         └──────────┬──────────┘
                                    │ at-least-once publish
                                    ▼
                              ┌──────────┐
                              │   SQS    │
                              └────┬─────┘
                                   │ visibility timeout
                                   ▼
                             ┌───────────┐
                             │  Worker   │
                             └─────┬─────┘
                                   │ stable idempotency key
                                   ▼
                         ┌─────────────────────┐
                         │ Payment Provider    │
                         └──────────┬──────────┘
                                    │ result
                                    ▼
                        ┌──────────────────────┐
                        │ PostgreSQL           │
                        │ Payment updated      │
                        │ Inbox inserted       │
                        └──────────┬───────────┘
                                   │ commit succeeds
                                   ▼
                              SQS Delete / ACK
```
