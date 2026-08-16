# Phase 1：多租户业务核心与业务不变量

> 目的：从头恢复 Reliant 的业务模型，理解“业务正确性”如何由领域规则、数据库约束和 API 契约共同保护。

## 1. Phase 1 要解决什么问题

Phase 1 不是简单地创建几个 CRUD 表。它要建立一个后续队列、Worker 和外部 Provider 都能依赖的业务核心：

- 不同 Tenant 的数据不能串读、串写；
- 同一个业务请求重试时不能创建两个业务结果；
- Contribution 不能跳过合法状态；
- 两个并发写入者不能静默覆盖对方；
- 关键状态变化必须可追溯；
- API 的错误、幂等和并发契约必须稳定。

Phase 1 负责先把“业务事实”固定下来。Phase 2/3 再把这些事实延伸到消息和外部系统。

## 2. 必读材料

按下面顺序阅读：

1. [主 Phase 计划：Phase 1](../../reliant-phase-plan-v3.2-final.md)
2. [ADR-0002：Business Invariants](../../docs/adr/ADR-0002-business-invariants.md)
3. [ADR-0005：Tenant and Membership](../../docs/adr/ADR-0005-tenant-and-membership.md)
4. [ADR-0006：Contribution State Machine](../../docs/adr/ADR-0006-contribution-state-machine.md)
5. [ADR-0007：Idempotency](../../docs/adr/ADR-0007-idempotency.md)
6. [ADR-0008：Optimistic Concurrency](../../docs/adr/ADR-0008-optimistic-concurrency.md)
7. [ADR-0009：Audit Model](../../docs/adr/ADR-0009-audit-model.md)
8. [ADR-0010：API Contract](../../docs/adr/ADR-0010-api-contract.md)
9. [Phase 1 的 Domain、Application、API 和测试代码](#5-代码和测试地图)

Phase 1 在 12 条总业务不变量中主要完成第 1、3、6 条：

- 第 1 条：同一个 Idempotency Key 不产生两个 Contribution；
- 第 3 条：一个 Contribution 只能进入合法状态；
- 第 6 条：Tenant A 永远不能读取 Tenant B 的数据。

其他不变量会在 Phase 2/3/后续 Phase 中继续完成。不要把 Phase 1 的局部完成误认为整个系统已经解决所有可靠性问题。

## 3. 核心知识

### 3.1 Tenant、User、Membership、Organization 的关系

在这个项目里，`Organization` 是 Tenant 边界。用户不是天然属于某个请求；用户通过 `Membership` 以某个角色加入 Organization。

```text
User
  └── Membership ──> Organization (Tenant)
                           ├── Campaign
                           └── Contribution
```

重要区别：

- **User**：谁在发起请求；
- **Organization / Tenant**：这次请求在哪个业务边界内执行；
- **Membership**：这个 User 在该 Tenant 中拥有什么角色和状态；
- **Role**：这个 User 能做什么；
- **TenantContext**：当前请求经过认证和授权后确定的租户上下文。

### 3.2 Tenant Context 必须来自可信身份

正确的链路是：

```text
JWT org_id
→ TenantMiddleware
→ TenantContext
→ Application / Repository
→ OrganizationId 过滤
```

客户端提交的 `organizationId`、URL 参数或请求体字段不能覆盖服务端已经确定的 `TenantContext`。

否则攻击者只需要把请求中的 Tenant ID 改成另一个值，就能绕过租户边界。这类问题通常称为 IDOR 或 Client Tenant Forgery。

“所有查询都手写 `WHERE OrganizationId = ...`”也不是足够强的防线，因为后续某个查询可能忘记写过滤条件。Reliant 使用 Repository/TenantContext 和架构测试把这个边界变成持续检查。

### 3.3 业务不变量不是注释

不变量是无论请求怎么重试、顺序怎么变化、客户端是否恶意，系统都必须保持为真的规则。

举例：

```text
同一个 Tenant + Idempotency-Key
→ 最多一个业务 Contribution
```

这条规则不能只写在 Controller 的 `if` 中，因为两个并发请求可能同时通过 `if`。需要多层保护：

1. Application 层定义处理流程；
2. 数据库唯一索引作为最终并发防线；
3. 事务保证 IdempotencyRecord 和 Contribution 的写入关系；
4. 测试验证重复请求、并发请求和不同请求体的行为。

### 3.4 Contribution 状态机

Phase 1 的核心业务状态是：

```text
Created
  → Accepted
  → Processing
  → Succeeded
  → ReceiptPending
  → Completed
```

后续可靠性路径还会出现：

```text
Processing
  → RetryPending
  → Processing

Processing
  → ProviderUnknown
  → ReconciliationPending

Processing
  → Failed
```

状态机的重点不是记住状态名字，而是理解三件事：

- 谁允许某个转换；
- 哪些转换不允许；
- 每次合法转换如何被持久化和审计。

状态转换应该由 Domain 层的规则控制，不能让 Controller、Worker 或数据库调用者随意给实体赋一个新状态。非法转换应明确失败，而不是静默修正。

### 3.5 StateTransition 与 AuditEvent 的区别

- `StateTransition`：Contribution 的业务状态从哪里变到哪里，属于领域历史；
- `AuditEvent`：谁在什么时候以什么理由执行了什么高风险动作，属于审计历史。

一个状态变化可以同时产生两类记录，但两者回答的问题不同：

```text
StateTransition：业务发生了什么状态变化？
AuditEvent：谁通过哪个入口做了什么操作？
```

关键写入需要和业务操作放在同一个数据库事务中，否则可能出现“业务已经成功，但审计没有记录”的不一致。

### 3.6 Idempotency：重复请求不重复产生业务结果

Idempotency 解决的是：客户端不知道上一次请求是否到达，于是重新发送同一业务请求。

典型流程：

```text
读取 Idempotency-Key
→ 按 Tenant + Key 查找记录
→ 已完成：返回之前的结果
→ 处理中：按契约返回处理中或冲突
→ 不存在：创建记录和业务对象
→ 同一事务提交
```

必须区分两种情况：

1. 相同 Tenant、相同 Key、相同请求体：应当复用或返回原结果；
2. 相同 Tenant、相同 Key、不同请求体：应当拒绝，通常返回冲突。

Idempotency Key 必须有 Tenant Scope。否则两个不同 Tenant 使用同一个 Key，会产生错误的全局冲突。

### 3.7 Idempotency 不等于并发控制

这两个概念经常混淆：

| 问题 | 主要解决方案 |
| --- | --- |
| 客户端重复提交同一个业务请求 | Idempotency Key + 唯一约束 |
| 两个写入者修改同一行 | Optimistic Concurrency + Version/ETag |
| 状态能否从 A 进入 B | Domain State Machine |
| 谁做了什么、何时做的 | AuditEvent |

一个请求可能既需要幂等，又需要并发控制；它们不是互相替代的。

### 3.8 Optimistic Concurrency 与 ETag

乐观并发的假设是：冲突不一定频繁，因此读取时不锁住整行，而是在更新时检查版本是否仍然是预期值。

```text
读取 Version = 3
→ 客户端或 Worker 修改
→ UPDATE ... WHERE Id = X AND Version = 3
→ 成功：Version 变成 4
→ 影响行数为 0：说明别人已经先修改
```

API 可以把同一个版本表达成 `ETag`：

- GET 返回 `ETag`；
- 客户端带条件更新；
- ETag 不匹配时返回 `412 Precondition Failed`；
- 不要把并发冲突静默覆盖，也不要无条件盲目重试。

### 3.9 API Contract 是可靠性边界

Phase 1 固定的不只是 URL，还包括：

- HTTP 方法和状态码；
- Problem Details 错误格式；
- `Idempotency-Key` Header；
- `ETag` 和并发失败语义；
- 分页和版本策略；
- 后续 Phase 不应随意破坏的 API 契约。

API 契约稳定，Phase 2 才能把请求可靠地转成消息，Phase 3 才能把外部 Provider 结果映射回业务状态。

### 3.10 数据库约束是业务规则的最后一道防线

Application 验证适合提供清晰错误；数据库约束适合在并发、漏调用和其他代码路径下仍然阻止非法数据。

需要理解的保护包括：

- Tenant 相关唯一索引；
- 外键和必填字段；
- 状态和版本字段；
- Migration 可重复执行和升级安全；
- 关键关系不能靠调用者“记得保持正确”。

## 4. 一次请求的完整心智模型

以创建 Contribution 为例，先按下面顺序讲一遍，再去看代码：

```text
HTTP Request
  → 认证和 TenantContext
  → 验证 Membership / Role
  → 读取 Idempotency-Key
  → 检查请求体是否与历史 Key 一致
  → 开启数据库事务
  → 创建 IdempotencyRecord
  → 创建 Contribution = Created / Accepted
  → 写 StateTransition / AuditEvent
  → 提交事务
  → 返回稳定 API 响应和 Version / ETag
```

注意：后续 Phase 会把 Outbox、Queue 和 Provider 接到这条链路后面，但不能改变 Phase 1 已经确立的业务不变量。

## 5. 代码和测试地图

| 位置 | 学习重点 |
| --- | --- |
| `src/Reliant.Domain/Entities/Organization.cs` | Tenant 业务实体 |
| `src/Reliant.Domain/Entities/Membership.cs` | 用户、租户和角色关系 |
| `src/Reliant.Domain/Entities/Contribution.cs` | 核心业务对象和 Version |
| `src/Reliant.Domain/Entities/ContributionStateMachine.cs` | 合法状态转换 |
| `src/Reliant.Domain/Entities/StateTransition.cs` | 状态变化记录 |
| `src/Reliant.Domain/Entities/AuditEvent.cs` | 审计记录 |
| `src/Reliant.Domain/Entities/IdempotencyRecord.cs` | 幂等请求记录 |
| `src/Reliant.Application/Tenancy/TenantContext.cs` | 当前租户上下文 |
| `src/Reliant.Api/Middleware/TenantMiddleware.cs` | 租户身份进入请求的入口 |
| `src/Reliant.Application/Contributions/Commands/CreateContributionCommand.cs` | 创建 Contribution 的应用流程 |
| `src/Reliant.Application/Contributions/Queries/GetContributionQuery.cs` | 带租户边界的读取流程 |
| `src/Reliant.Infrastructure/Persistence/Repositories/Repositories.cs` | Repository 和租户过滤 |
| `src/Reliant.Infrastructure/Persistence/ReliantDbContext.cs` | EF Core 模型、约束和过滤器 |
| `tests/Reliant.Tests/Architecture/ArchitectureTests.cs` | 架构与租户边界检查 |
| `tests/Reliant.Tests/Unit/ContributionStateMachineTests.cs` | 状态机行为 |
| `tests/Reliant.Tests/Integration/DatabaseConstraintTests.cs` | 数据库约束 |

## 6. 从头学习任务

### 任务 A：画领域关系图

画出 `User`、`Membership`、`Organization`、`Campaign`、`Contribution` 的关系，并在每条关系旁写明：

- 谁拥有谁；
- 哪个字段是 Tenant Boundary；
- 哪个角色负责授权；
- 哪些对象会被后续 Worker 使用。

### 任务 B：手推四个请求

对每个场景写出预期结果、状态、数据库写入和 HTTP 响应：

1. Tenant A 第一次用 Key-1 创建 Contribution；
2. Tenant A 用相同 Key-1、相同请求体再次提交；
3. Tenant A 用相同 Key-1、不同请求体再次提交；
4. Tenant B 尝试读取 Tenant A 的 Contribution。

### 任务 C：手推两个并发写入

两个请求都读取 `Version = 3`，然后同时更新同一个 Contribution。说明：

- 为什么不能让两次更新都成功；
- `WHERE Version = 3` 如何阻止丢失更新；
- 第二个请求应该观察到什么错误；
- 这和 Idempotency Key 重复有什么不同。

### 任务 D：状态机审查

从 `ContributionStateMachine.cs` 找出：

- 一个合法转换；
- 一个非法转换；
- 非法转换如何失败；
- StateTransition 在哪里被记录；
- Version 在哪里参与并发保护。

## 7. Owner 自测题

1. Tenant 和 User 为什么不是同一个概念？
2. 为什么 Tenant ID 不能信任客户端请求体？
3. `TenantContext` 在请求生命周期中扮演什么角色？
4. 为什么租户过滤不能只靠 Controller 自觉添加？
5. 为什么相同 Idempotency Key 但不同请求体必须拒绝？
6. 为什么唯一索引仍然需要 Application 层的幂等流程？
7. Idempotency 和 Optimistic Concurrency 分别解决哪两个问题？
8. `StateTransition` 和 `AuditEvent` 分别回答什么问题？
9. 为什么非法状态转换不能静默修正？
10. ETag 不匹配为什么是 `412 Precondition Failed` 的语义？
11. 数据库约束和 Application 校验为什么需要同时存在？
12. Phase 1 的哪些不变量会由 Phase 2/3 继续承接？
13. 如果 API 返回成功但审计没有写入，为什么这是可靠性问题？
14. 如果两个 Worker 同时改变状态，哪些层分别负责保证正确性？
15. 为什么 API Contract 必须在后续 Phase 中保持稳定？

## 8. Phase 1 学习完成标准

- [ ] 能画出 User、Membership、Organization、Campaign、Contribution 的关系；
- [ ] 能从 JWT 到 Repository 解释 TenantContext 的传递链路；
- [ ] 能说出 Phase 1 负责的三条核心不变量；
- [ ] 能不看代码画出 Contribution 状态机；
- [ ] 能解释 Idempotency、State Machine、Optimistic Concurrency、Audit 各自解决的问题；
- [ ] 能手推四种重复/跨租户请求的结果；
- [ ] 能解释 Version、ETag、唯一索引和事务的关系；
- [ ] 能指出创建 Contribution、租户过滤、状态机和架构测试的代码位置；
- [ ] 能回答全部 Owner 自测题；
- [ ] 能说明哪些可靠性问题明确留给 Phase 2/3，而不是声称 Phase 1 已全部解决。

完成 Phase 1 后，再进入 Phase 2。Phase 2 的 Outbox、Inbox、Worker 和消息重复投递，都是在这里的业务不变量之上继续展开的。
