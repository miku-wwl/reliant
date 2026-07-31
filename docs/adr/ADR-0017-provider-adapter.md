# ADR-0017: Provider Adapter

## Status

Proposed

## Context

Reliant 需要调用外部 Provider 完成模拟支付处理。ADR-0001 第5节规定 Provider Contract 定义在 Application 层，实现在 Infrastructure 层。outline 第9.1节定义了 Provider Contract。

需要回答：和 Provider 通信的接口长什么样？怎么切换 Provider 而不改业务代码？

## Decision

### 1. Provider Contract 接口

定义在 Application 层，业务代码只调这个接口：

```csharp
public interface IProvider
{
    Task<ProviderResult> SubmitAsync(ProviderRequest request, CancellationToken ct);
    Task<ProviderStatusResult> QueryStatusAsync(string providerReference, CancellationToken ct);
    Task<ProviderResult> CancelAsync(string providerReference, CancellationToken ct);
    Task<ProviderHealthResult> CheckHealthAsync(CancellationToken ct);
}
```

### 2. ProviderRequest

```
ProviderRequest
├── IdempotencyKey (string) - 全局唯一，防重复提交
├── Amount (decimal)
├── Currency (string)
├── Reference (string) - 业务参考号
└── Metadata (Dictionary)
```

### 3. ProviderResult

```
ProviderResult
├── Status (ProviderStatus) - Succeeded / Failed / Pending / Unknown
├── ProviderReference (string, nullable) - Provider 返回的参考号
├── ErrorCategory (ErrorCategory, nullable)
├── ErrorMessage (string, nullable)
└── RawResponse (string, nullable) - 原始响应，用于调试
```

### 4. SandboxProvider 实现

Infrastructure 层实现一个模拟 Provider：

- 正常请求返回 Succeeded + 随机 Reference
- 可配置故障模式：Timeout / 429 / 5xx / 成功
- 通过配置文件切换故障模式，用于测试
- 不依赖真实网络，纯内存模拟

### 5. 切换 Provider

- Application 层只依赖 `IProvider` 接口
- DI 注册时注入 `SandboxProvider`
- 将来换真实 Provider 只改 DI 注册，不改业务代码
- Provider SDK 只在 Infrastructure 层

## Consequences

- Sandbox Provider 足够模拟所有故障场景，不需要真实支付服务
- ProviderRequest 的 IdempotencyKey 是防重复提交的关键
- ProviderResult 的 Unknown 状态触发 Unknown Outcome 处理流程
