# ADR-0005: Tenant and Membership Model

## Status

Proposed

## Context

Reliant 是多租户 SaaS 系统。Organization 是业务 Tenant。outline 第6.1节定义了 Tenant 边界规则。

需要回答：Organization、Membership、User 之间的关系是什么？Tenant 身份怎么传递？开发环境怎么认证？

## Decision

### 1. 数据模型

```
Organization (Tenant)
├── Id (Guid)
├── Name
├── CreatedAt
└── Status (Active / Suspended)

User
├── Id (Guid)
├── ExternalId (OIDC Subject)
├── Email
└── CreatedAt

Membership
├── Id (Guid)
├── OrganizationId (Tenant Boundary)
├── UserId
├── Role (Owner / Administrator / Operator / Analyst / Auditor)
├── Status (Active / Invited / Revoked)
└── CreatedAt
```

### 2. Tenant 身份传递

- 用户登录后，JWT 包含 `sub`（用户 ID）和 `org_id`（当前组织）
- `org_id` 不是客户端自己填的，是 Membership 查出来的
- API 收到请求后，从 JWT 提取 `org_id`，注入 `TenantContext`
- `TenantContext` 是 Scoped 服务，整个请求生命周期内不变
- 客户端不能通过请求参数覆盖 `TenantContext`

### 3. 认证方案

R1 开发阶段使用本地开发 IdentityProvider（不依赖真实 Entra ID）：
- 本地用 JWT 签发工具或测试用 Identity Server
- JWT 包含 `sub`、`email`、`org_id`、`role`
- API 验证 JWT 签名、Issuer、Audience、Expiry
- Phase 6 Azure 部署时切换到真实 Entra ID

### 4. 数据库约束

- `Membership` 表唯一索引：`(OrganizationId, UserId)` - 一个人在同一组织只有一条 Membership
- `Organization` 表的 `Id` 是所有租户表的外键来源
- 所有租户表包含 `OrganizationId` 列，带索引

### 5. 租户隔离

- EF Core `Global Query Filter`：所有租户 Repository 自动加 `WHERE OrganizationId = @currentTenant`
- Repository 层强制注入 `TenantContext`，不注入的查询编译报错
- Architecture Test 检查所有继承 `ITenantScoped` 接口的 Repository 必须引用 `TenantContext`

## Consequences

- 开发阶段需要本地的 JWT 签发工具，不依赖外部 IdP
- TenantContext 是全局 Scoped，每个请求创建一次
- Phase 6 需要切换到真实 Entra ID，但 API 层逻辑不变（只换认证中间件）
- Membership 表是多租户隔离的基础，必须先建
