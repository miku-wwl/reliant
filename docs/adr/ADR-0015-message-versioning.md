# ADR-0015: Message Versioning

## Status

Proposed

## Context

消息格式会随时间变化。比如 ContributionCreated 消息最初只有 Amount 和 Currency，后来加了 ProviderReference。如果旧版本的消息被新版本的 Handler 消费，可能出问题。

## Decision

### 1. 消息包含版本号

每条消息的 Payload 包含 `version` 字段：

```json
{
  "version": 1,
  "contributionId": "abc-123",
  "organizationId": "org-456",
  "amount": 100.00,
  "currency": "USD"
}
```

### 2. Handler 版本兼容

- Handler 记录自己支持的版本范围
- 收到消息时检查版本：
  - 版本在范围内 -> 正常处理
  - 版本低于范围 -> 处理（向后兼容，新字段用默认值）
  - 版本高于范围 -> 进 Dead-letter（Handler 不认识新格式）

### 3. InboxMessage 记录 Handler 版本

```
InboxMessage.HandlerVersion = "1.0"
```

如果 Handler 升级后需要重新处理旧消息，可以按 HandlerVersion 过滤。

### 4. 版本变更规则

- 只能加字段，不能删字段（向后兼容）
- 字段类型变更 = 新版本，不能改旧版本
- 版本号是整数，递增

## Consequences

- 消息格式变更需要规划，不能随意改
- 旧消息在新 Handler 中能处理，但新字段为默认值
- 版本不匹配的消息进 Dead-letter，需要人工介入
