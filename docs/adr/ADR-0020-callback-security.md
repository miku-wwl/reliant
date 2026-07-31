# ADR-0020: Callback Security

## Status

Proposed

## Context

Provider 处理完请求后可能通过 Callback（类似支付平台 Webhook）通知 Reliant。Callback 来自外部网络，必须验证真实性。outline 第9.2节要求覆盖 Duplicate Callback 和 Wrong Signature。

## Decision

### 1. Callback 流程

```
Provider 处理完成
  -> 向 Reliant 发送 HTTP POST Callback
  -> 包含：ProviderReference、Status、Signature
  ↓
Reliant 收到 Callback
  -> 验证签名（HMAC-SHA256）
  -> 验证 Timestamp（防重放，5 分钟内有效）
  -> 验证 ProviderReference 是否存在
  -> 处理状态更新
  -> 返回 200
```

### 2. 签名验证

- Provider 和 Reliant 共享一个 Secret
- 签名 = HMAC-SHA256(Secret, Timestamp + Payload)
- Callback Header 包含 `X-Provider-Signature` 和 `X-Provider-Timestamp`
- 验证失败返回 401

### 3. Replay Protection

- Timestamp 超过 5 分钟的 Callback 拒绝
- 每个 Callback 有唯一 `EventId`
- 已处理的 EventId 记录在 InboxMessage 中
- 重复的 EventId 返回 200（但不重复处理），和主流支付平台行为一致

### 4. Callback Before Response 场景

```
Worker 调 Provider Submit
  -> Provider 正在处理
  -> Provider 处理完后先发 Callback
  -> Callback 比 Submit 响应先到达
  -> Worker 还在等 Submit 响应
```

处理方式：
- Callback 先到，验证签名后处理，更新 Contribution 状态
- Submit 响应后到，Worker 发现 Contribution 已经不是 Processing，忽略响应
- 通过乐观并发（Version）保证不会冲突

### 5. 和典型支付回调事故的关系

| 典型事故 | Phase 3 的解法 |
| --- | --- |
| Webhook 到了直接处理，没验签 | HMAC-SHA256 签名验证 |
| 重复 Webhook 重复处理 | EventId + Inbox 去重 |
| 业务失败返回 200 | 业务失败返回 5xx，Provider 会重试 |

### 6. Sandbox Provider 的 Callback

- Sandbox Provider 可配置是否发 Callback
- 可配置 Callback 延迟（模拟 Callback Before Response）
- 可配置 Callback 重复发送
- 可配置错误签名

## Consequences

- Callback 和 Submit 响应可能乱序，通过乐观并发处理
- Secret 管理通过 Secrets Manager（Phase 0 已验证 LocalStack 可用）
- Callback 失败不丢消息：Provider 会重试
- Callback 的 EventId 和 Outbox 的 MessageId 是不同的 ID，不混用
