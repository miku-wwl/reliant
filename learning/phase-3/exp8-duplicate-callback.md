# Phase 3 / Experiment 8 — Duplicate Callback

## 一页结论

**PASS（E2：真实 HTTP API + PostgreSQL）**

使用同一个 EventId 分别做顺序重复和并发重复。两个场景中的两次 HTTP 请求都通过
HMAC 验证并返回 200，但数据库最终只有一条 Callback Inbox、一次
`Processing -> Succeeded` 状态变化，Contribution Version 只增加一次。

Callback 处理不会调用 Provider，因此 ProviderOperationCount 在请求前后都保持 0。

```text
SEQUENTIAL | HTTP=200,200 | Inbox=1 | Transition=1 | ProviderOp=0
CONCURRENT | HTTP=200,200 | Inbox=1 | Transition=1 | ProviderOp=0
```

## 实验信息

- 日期：2026-08-11
- 测试目录：`tests/Reliant.Tests/Integration/Phase3/Exp8/`
- 测试类：`DuplicateCallbackHttpTests`
- 场景：顺序重复、并发重复
- HTTP 入口：`POST /api/callbacks/provider`
- 签名：每次请求均使用有效 HMAC-SHA256
- 数据库：PostgreSQL 17 Testcontainer
- Exp8：2/2 passed

## 假设

```text
Provider Callback 是 at-least-once 事件
相同 EventId 可以重复甚至并发到达
HTTP 可重复返回成功，但业务状态只能应用一次
```

## 实验设计

每个场景都创建独立的：

```text
Contribution.State = Processing
Contribution.Version = 0
ProviderReference = unique reference
ProviderOperationCount = 0
```

Callback Payload 使用相同 EventId 和 ProviderReference，Status 为 `succeeded`。

### 顺序重复

```text
POST Event A -> 200
POST Event A -> 200
```

### 并发重复

```text
Task.WhenAll(
  POST Event B,
  POST Event B)
-> 200, 200
```

两个场景最后都读取全新 DbContext 检查最终数据库状态。

## 学生视角：中间过程

### 第一次 Review：Handler 测试不足以证明 HTTP 行为

仓库原有 `CallbackTests` 已有顺序和并发重复 Handler 测试，但它们直接发送 MediatR
Command，没有覆盖：

- HMAC 和 Timestamp 验证；
- Controller 的 HTTP status 映射；
- 每个 HTTP 请求独立 Scope / DbContext；
- 并发请求通过完整 ASP.NET Pipeline 的行为。

所以我删除这两条低层重复用例，换成两条真实 HTTP 测试。测试数量不变，证据层级从
Handler + PostgreSQL 提升到 HTTP + Handler + PostgreSQL。

### 顺序重复的行为

第一次 Callback：

```text
Processing -> Succeeded
write callback-{EventId} Inbox
commit
return 200 Processed
```

第二次 Callback 看到 Contribution 已 Succeeded，尝试记录同 EventId Inbox；唯一约束
使重复记录不成立，Handler 将其作为已处理返回 200。

最终：

```text
Contribution = Succeeded / Version=1
Inbox = 1
Succeeded Transition = 1
ReconciliationRecord = 0
Operator Outbox = 0
ProviderOperation = 0
```

### 并发重复的行为

两个请求可能都从 Processing 状态开始计算。数据库通过两层约束决定唯一 winner：

```text
Contribution optimistic Version
Callback Inbox unique MessageId = callback-{EventId}
```

winner 原子提交 Contribution、StateTransition 和 Inbox；loser 的事务失败并回滚，
`TrySaveChangesAsync` 将这个已知并发结果映射为 `Already processed`，Controller 返回
200。

测试日志中会看到 EF/Npgsql 对唯一约束 `IX_inbox_messages_MessageId` 的错误级输出；
这是并发 loser 的底层证据，不是测试失败。最终数据库仍只有一份业务结果。

### Provider Effect 为什么保持 0

Callback Handler 只根据已存 ProviderReference 定位 Contribution，不调用 Provider
Submit。实验在请求前记录 OperationCount，并断言请求后完全不变：

```text
operationsBefore = 0
operationsAfter = 0
```

PASS 条件写的是“不变”，不要求一定从 1 开始。

## PASS 条件逐项判定

| PASS 条件 | 顺序重复 | 并发重复 | 判定 |
| --- | --- | --- | --- |
| 两次都可返回 200 | 200, 200 | 200, 200 | PASS |
| Inbox 只有一条 | 1 | 1 | PASS |
| 状态变化只发生一次 | Succeeded Transition=1 | Succeeded Transition=1 | PASS |
| ProviderOperationCount 不变 | 0 -> 0 | 0 -> 0 | PASS |

## 最终数据（每个场景）

| 数据 | 最终值 |
| --- | --- |
| HTTP responses | 200, 200 |
| Contribution | Succeeded / Version=1 |
| Callback Inbox | 1 / Processed |
| Processing -> Succeeded | 1 |
| ReconciliationRecord | 0 |
| Operator Outbox | 0 |
| ProviderOperation | 0，保持不变 |

## 业务代码与测试聚合 Review

```text
生产代码修改：0
数据库 Migration：0
删除：CallbackTests 中2条重复 Handler 用例
新增：Exp8 中2条真实 HTTP 用例
测试总数净变化：0
```

现有业务代码已经有正确的原子边界：Contribution 更新、StateTransition 和 Callback
Inbox 在同一 SaveChanges 中提交；Inbox MessageId 唯一约束与 Contribution Version
共同处理并发；`TrySaveChangesAsync` 将已知冲突视为已处理。

没有为了消除测试日志而加入“先查再写”逻辑，因为先查在并发下仍有 TOCTOU 竞态，
不能替代数据库约束。

## 当前限制

1. EF Core 会把预期唯一约束 loser 记录为 Error 级日志，可能造成告警噪声；正式 SRE
   观测应按约束名分类为 duplicate outcome，而不是业务错误。
2. 本实验验证相同 EventId；Provider 错误地使用不同 EventId 重复表达同一业务事件时，
   需要业务状态终态保护。
3. 测试不包含多实例网络延迟，但不同 HTTP Scope 和独立 DbContext 已形成真实 DB 竞态。

## 验证命令

```powershell
dotnet build Reliant.slnx -c Release --no-restore

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build `
  --filter "FullyQualifiedName~Integration.Phase3.Exp8" `
  --logger "console;verbosity=detailed"

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build
```

## 我的学习结论

Callback 幂等的正确语义不是“第二次返回错误”，而是：

```text
same EventId may receive 200 repeatedly
business mutation commits at most once
```

这样 Provider 可以安全重试，同时 Reliant 不会产生重复状态推进。数据库约束是最后的
并发裁判，HTTP 200 是对已处理事实的幂等确认。
