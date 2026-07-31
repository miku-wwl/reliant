# ADR-0018: Provider Error Classification

## Status

Accepted

## Context

ADR-0013 定义了通用错误分类。但 Provider 返回的错误需要更细的分类逻辑：同一个 HTTP 429 可能是"等一会重试"也可能是"配额用完了"。需要回答：Provider 的各种错误怎么映射到 ErrorCategory。

## Decision

### 1. 错误映射表

| Provider 返回 | ErrorCategory | 该重试吗 | 原因 |
| --- | --- | --- | --- |
| 200 OK | 无错误 | - | 成功 |
| 400 Bad Request | ValidationFailure | 否 | 请求格式有问题，重试也没用 |
| 401 Unauthorized | AuthenticationFailure | 否 | 凭证过期，需要人工更新 |
| 403 Forbidden | AuthenticationFailure | 否 | 没有权限 |
| 409 Conflict | PermanentBusinessRejection | 否 | 业务冲突（如重复提交但参数不同） |
| 429 Too Many Requests | RateLimited | 是 | 等一会再试 |
| 500 Internal Server Error | ServerError | 是 | Provider 内部错误，可能恢复 |
| 502 Bad Gateway | ServerError | 是 | 上游错误 |
| 503 Service Unavailable | ServerError | 是 | Provider 暂时不可用 |
| Timeout（无响应） | Timeout | 特殊处理 | 可能已处理，走 Unknown Outcome |
| Connection Reset | NetworkFailure | 是 | 网络问题 |
| Malformed Response | ServerError | 是 | Provider 返回了无法解析的内容 |
| Slow Response（>30s） | Timeout | 特殊处理 | 同 Timeout |

### 2. Timeout 的特殊处理

Timeout 是最危险的错误，因为有两种可能：

```
情况 A：请求没到达 Provider -> 没处理 -> 可以安全重试
情况 B：请求到达了 Provider，Provider 处理了，但响应丢了 -> 已处理 -> 重试会重复
```

系统无法区分是 A 还是 B。所以：
- 不直接重试
- 进入 Unknown Outcome 流程（ADR-0019）
- 通过 Reconciliation 查 Provider 最终状态

### 3. 分类实现

- Sandbox Provider 根据配置返回指定错误
- ProcessingHandler 收到 ProviderResult 后，根据 ErrorCategory 决定下一步
- 分类逻辑在 Application 层，不在 Infrastructure 层

### 4. 错误不吞

- 所有 Provider 错误必须记录到 ProcessingAttempt
- 错误分类必须有测试覆盖
- 不允许 catch 后只记日志不分类

## Consequences

- Timeout 和 NetworkFailure 的处理方式不同：NetworkFailure 可以重试，Timeout 不能
- 429 和 5xx 的重试策略相同但含义不同（限流 vs 故障）
- 错误分类是 Circuit Breaker 的输入（连续 5xx 触发熔断，429 不触发）
