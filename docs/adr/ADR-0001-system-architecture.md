# ADR-0001: System Architecture

## Status

Proposed

## Context

Reliant 是一个多租户 SaaS 系统（活动、贡献记录、通知处理），需要足够的分布式复杂度来研究可靠性工程，但不需要模拟大型团队的微服务架构。

技术栈：.NET 10 / ASP.NET Core / EF Core / PostgreSQL / Terraform / OpenTelemetry / Azure + AWS。

### 核心领域（来自 outline 第5节）

- **Transaction Boundary**：Organization、Campaign、Contribution、ProcessingAttempt、ProviderReference、ReconciliationRecord、OutboxMessage
- **Notification Boundary**：Notification、WebhookSubscription、WebhookDelivery、InboxMessage、DeliveryAttempt、DeadLetterRecord

### 关键约束

1. 业务事务和 Outbox 消息必须原子提交
2. Provider SDK 不能泄漏到 Domain 层
3. 需要支持多租户隔离（共享数据库 + TenantId）
4. Worker Crash 后任务可恢复
5. 消息重复投递不能产生重复业务结果
6. Incident timeline 需要由系统事件自动构成

## Decision

### 1. Modular Monolith，两个长期运行部署单元

不使用 Microservices。理由：
- 单人开发，Microservices 运维成本不可接受
- 两个部署单元已能产生完整的分布式复杂度（同步请求 -> DB 事务 -> 异步消息 -> 外部依赖）
- 拆分必须由证据驱动，不是由模块名称驱动

部署单元：
- **Public API Host**：HTTP 契约、认证、幂等、限流、写 Outbox
- **Unified Worker Host**：一个进程内跑 4 个隔离 Handler

只有出现独立扩缩、故障隔离、安全身份或发布周期需求时，才通过 ADR 拆分。

### 2. Clean Architecture 分层

```
Domain          -> 实体、值对象、领域事件，零外部依赖
Application     -> Use Case、DTO、接口定义，依赖 Domain
Infrastructure  -> EF Core、Provider SDK、消息队列、外部服务
Presentation    -> ASP.NET Core API、Background Worker
```

依赖方向：Presentation -> Application -> Domain，Infrastructure 实现 Application 定义的接口。

### 3. Solution 结构

```
Reliant.sln
├── src/
│   ├── Reliant.Domain/
│   ├── Reliant.Application/
│   ├── Reliant.Infrastructure/
│   ├── Reliant.Api/
│   ├── Reliant.Worker/
│   ├── Reliant.Migrator/
│   └── Reliant.Cli/
└── tests/
    └── Reliant.Tests/
```

比 phase-plan 建议 Structure 简化：不分模块子项目，用命名空间区分 Transaction Boundary 和 Notification Boundary。原因：单人项目，模块子项目会增加编译和依赖管理负担，但边界通过 Architecture Test 约束。

### 4. Unified Worker Host 内部 Handler

```
Unified Worker Host
├── Processing Handler         -> 消费业务消息，调外部 Provider
├── Notification Handler       -> Receipt、Email、Webhook
├── Reconciliation Handler     -> 查询 Provider 最终状态
└── Scheduled Maintenance Handler -> Expired Lease、Retry Due、Retention
```

每个 Handler 拥有独立 Queue、Concurrency Limit、Retry Policy、Metrics、Health Signal、Failure Isolation Boundary。

Handler 之间通过消息通信，不直接方法调用。

### 5. Provider 抽象边界

- Provider Contract（Submit / Query Status / Cancel / Reconcile）定义在 Application 层
- Sandbox/Simulation Provider 实现在 Infrastructure 层
- Azure/AWS 云 SDK 只在 Infrastructure 层
- Provider-specific 差异通过 Error Classification 保留，不强行统一

### 6. 多租户

- 共享数据库，通过 `TenantId` 列隔离
- 仓储层强制注入 `TenantContext`
- EF Core Global Query Filter 自动过滤
- Unique Index、Cache Key、Queue Message 都包含 Tenant Scope

## Architecture Test 规则

以下规则将被 NetArchTest 自动验证：

1. Domain 不依赖 Infrastructure、ASP.NET Core、EF Core、云 SDK
2. Application 不依赖 Infrastructure、ASP.NET Core
3. API/Worker 不执行 Startup Migration
4. Notification Boundary 不直接访问 Transaction 内部表
5. Tenant Repository 必须显式使用 TenantContext

## Consequences

- 两个部署单元足够产生真实分布式复杂度，同时避免微服务运维负担
- 共享 Solution 简化编译，但需要 Architecture Test 保证边界
- Unified Worker 内 Handler 共享进程，需要纪律保证故障隔离
- 未来可按 Handler 拆分为独立服务，但 R1 不做
