# ADR-0013: Retry and Dead-letter

## Status

Proposed

## Context

outline 第8.2节定义了 Retry 分类和策略。ADR-0002 不变量 #7 要求"Queue 重复投递不能造成重复处理"。需要回答：什么错误该重试、重试几次、什么时候进 Dead-letter。

## Decision

### 1. 错误分类

**说人话**：不是所有错误都该重试。网络超时重试有意义（可能下次就好了），但"金额为负数"这种永久错误重试 100 次也还是错。

| 错误类型 | 例子 | 该重试吗 | 原因 |
| --- | --- | --- | --- |
| Timeout | Provider 5 秒没响应 | 是 | 可能是暂时的 |
| 429 Too Many Requests | Provider 限流 | 是 | 等一会再试 |
| 5xx Server Error | Provider 内部错误 | 是 | 可能恢复 |
| Network Failure | 连接被重置 | 是 | 可能恢复 |
| Validation Failure | 金额为负数 | 否 | 永久错误，重试没用 |
| Authentication Failure | Token 过期 | 否 | 需要人工干预 |
| Permanent Business Rejection | 重复提交 | 否 | 业务逻辑拒绝 |
| Unknown Outcome | 请求发出去了但不知道结果 | 特殊处理 | 不能盲目重试，Phase 3 用 Reconciliation 解决 |

### 2. 重试策略

```
重试间隔 = 基础延迟 × 2^次数 + 随机抖动

示例（基础延迟 1 秒）：
第 1 次重试：1 秒 + 随机 0-1 秒
第 2 次重试：2 秒 + 随机 0-1 秒
第 3 次重试：4 秒 + 随机 0-1 秒
第 4 次重试：8 秒 + 随机 0-1 秒
第 5 次重试：16 秒 + 随机 0-1 秒（最多 5 次）
```

- 最大重试次数：5 次
- 最大延迟上限：30 秒（防止指数增长太夸张）
- 随机抖动（Jitter）：0-1 秒，防止多个 Worker 同时重试造成雪崩

### 3. Retry Budget

- 每种错误类型有独立的 Retry Budget
- 如果最近 1 分钟内某错误类型的重试次数超过 100，暂停重试，直接进 Dead-letter
- 防止 Provider 大面积故障时 Worker 疯狂重试把系统拖垮

### 4. Dead-letter 流程

```
消息重试 5 次都失败
  ↓
消息从主队列转移到 Dead-letter 队列（SQS DLQ，Phase 0 已验证可用）
  ↓
写 DeadLetterRecord 到数据库（记录原因、时间、原始消息）
  ↓
Operator 通过 CLI 查看 Dead-letter 列表
  ↓
Operator 判断：
  - 暂时性问题 -> replay（重新放回主队列）
  - 永久问题 -> 标记为忽略
  - 需要修复代码 -> 修复后 replay
```

### 5. DeadLetterRecord 数据模型

```
DeadLetterRecord
├── Id (Guid)
├── OrganizationId (Guid)
├── OriginalMessageId (string)
├── MessageType (string)
├── Payload (JSON) - 原始消息内容
├── ErrorCategory (string) - 错误分类
├── ErrorMessage (string) - 错误详情
├── AttemptCount (int) - 重试次数
├── DeadLetteredAt (DateTime) - 进入 DLQ 时间
├── Status (DeadLetterStatus) - Pending / Replayed / Ignored
└── ReplayedAt (DateTime, nullable)
```

### 6. Replay 规则

- Replay 将消息重新放回主队列
- Replay 有最大次数限制（3 次），防止反复 replay 死循环
- 每次 Replay 记录审计
- Replay 是高风险操作，需要 Operator 权限

## Consequences

- 重试不是无限重试，5 次后进 Dead-letter
- Retry Budget 防止雪崩，但也意味着大面积故障时部分消息会直接进 DLQ
- Dead-letter 需要人工介入，不能自动 replay
- SQS DLQ 在 Phase 0 已验证可用（maxReceiveCount 行为正确）
