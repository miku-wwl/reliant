# Phase 3 / Experiment 7 — Callback Security

## 一页结论

**PASS（E2：真实 HTTP API + PostgreSQL）**

通过真实 `/api/callbacks/provider` 入口验证 8 种请求：正确 HMAC 请求进入 Handler 并
产生一次业务状态变化；错误 HMAC、缺少签名、缺少时间戳、无法解析时间戳、过期、
未来和非 UTC 时间戳全部返回 401。

每一个非法场景都使用一笔独立的 Processing Contribution，并逐项证明：

```text
Contribution 保持 Processing
Callback Inbox = 0
StateTransition = 0
OrphanProviderCallback = 0
```

## 实验信息

- 日期：2026-08-11
- 测试目录：`tests/Reliant.Tests/Integration/Phase3/Exp7/`
- 测试类：`CallbackSecurityHttpTests`
- HTTP 入口：`POST /api/callbacks/provider`
- 签名：HMAC-SHA256(`timestamp + raw payload`)
- 允许时间偏差：正负 5 分钟
- 时间格式：ISO-8601，必须显式 `Z` 和 UTC offset 0
- 数据库：PostgreSQL 17 Testcontainer
- Exp7：8/8 passed

## 假设

```text
Callback 身份验证必须发生在 JSON 解析和业务 Handler 之前
任何验证失败都不能留下 Inbox、状态转换或 Orphan 审计副作用
```

## 实验矩阵

| 场景 | 签名 | 时间戳 | HTTP | 业务结果 |
| --- | --- | --- | --- | --- |
| 正确 HMAC | 正确 | 当前 UTC Z | 200 | Succeeded，Inbox=1，Transition=1 |
| 错误 HMAC | 错误 | 当前 UTC Z | 401 | 零修改 |
| 缺少 Signature | 缺少 | 当前 UTC Z | 401 | 零修改 |
| 缺少 Timestamp | 有 | 缺少 | 401 | 零修改 |
| 无法解析 Timestamp | 按原字符串正确签名 | `not-a-timestamp` | 401 | 零修改 |
| 过期 Timestamp | 正确 | UTC -10 分钟 | 401 | 零修改 |
| 未来 Timestamp | 正确 | UTC +10 分钟 | 401 | 零修改 |
| 非 UTC Timestamp | 正确 | `+08:00` | 401 | 零修改 |

## 学生视角：中间过程

### 第一次 Review：HTTP 401 还不够

旧 `CallbackSecurityHttpTests` 已覆盖这 8 类输入，但除错误签名外，多数非法用例只检查
HTTP 401。如果 Controller 在返回 401 前意外写了 Inbox 或业务状态，旧断言发现不了。

所以我把每个非法测试都改成：

1. 创建自己的 Processing Contribution 和 ProviderReference；
2. Payload 指向这笔真实业务；
3. 发送对应非法请求；
4. 用独立 DbContext 检查四类持久化副作用为零。

### 有效请求确实进入 Handler

有效请求不是只检查 HTTP 200。最终数据库证据：

```text
Contribution.State = Succeeded
Inbox MessageId = callback-http-evt-1
InboxCount = 1
Processing -> Succeeded StateTransitionCount = 1
```

因此可以区分“网关随便返回 200”和“签名通过后实际进入 Callback Handler”。

### 非法请求在 Handler 前被拒绝

7 个非法用例全部输出相同的零副作用模板：

```text
REJECTED | Status=401 |
Contribution=Processing |
Inbox=0 | StateTransition=0 | Orphan=0
```

`Orphan=0` 也很重要：无效签名不能被误当成“找不到本地业务的合法 Callback”写入
Orphan 表，否则攻击者可以污染审计和容量。

### Timestamp 防重放规则

Verifier 的顺序是：

```text
header presence
-> parse timestamp
-> require explicit UTC Z
-> require absolute skew <= 5 minutes
-> fixed-time HMAC compare
```

过期与未来都被拒绝，避免只防旧请求却允许远未来签名。非 UTC offset 即使表示同一
绝对时刻也被拒绝，从而保持签名字符串和审计格式唯一。

## PASS 条件逐项判定

| PASS 条件 | 实际证据 | 判定 |
| --- | --- | --- |
| 有效请求进入 Handler | 200 + Contribution Succeeded + Inbox/Transition | PASS |
| 无效请求返回 401 / 400 | 7 类均为 401 | PASS |
| 无效请求不创建 Inbox | 每类目标 EventId Inbox=0 | PASS |
| 无效请求不修改 Contribution | 每类保持 Processing | PASS |
| 无效请求不写 StateTransition | 每类目标 Contribution Transition=0 | PASS |

## 代码与文件整理 Review

```text
生产代码修改：0
数据库 Migration：0
测试文件：旧 CallbackSecurityHttpTests 迁入 Phase3/Exp7
删除跨实验测试：1条 Duplicate Callback 测试，交由 Exp8 专门验证
新增重复测试：0
```

现有生产代码已满足安全顺序：Controller 先读取原始 Payload 和 Headers，调用
`IProviderCallbackVerifier`；只有验证成功才反序列化并发送
`HandleProviderCallbackCommand`。HMAC 使用 `CryptographicOperations.FixedTimeEquals`
比较，避免普通字符串比较的时序泄漏。

本实验仅增强数据库零副作用断言并整理目录，不需要修改 Controller 或 Verifier。

## 当前限制

1. TestServer 验证应用 HTTP Pipeline，但不包含真实 WAF、Ingress、TLS termination 或
   Provider IP allow-list。
2. 当前签名协议没有持久化 nonce；重放窗口内的相同合法 EventId 依靠 Callback Inbox
   幂等，属于 Exp8。
3. Secret 来自测试配置；生产 Secret rotation、Key Vault 和双 Key 过渡尚未验证。
4. Payload 大小限制、速率限制和 DDoS 防护不在本实验范围内。

## 验证命令

```powershell
dotnet build Reliant.slnx -c Release --no-restore

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build `
  --filter "FullyQualifiedName~Integration.Phase3.Exp7" `
  --logger "console;verbosity=detailed"

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build
```

## 我的学习结论

Callback 安全测试不能停在“返回 401”。真正的安全不变量是：

```text
authentication failure
-> no handler invocation
-> no business mutation
-> no audit/inbox pollution
```

只有同时验证 HTTP 结果和数据库零副作用，才能证明非法 Callback 被挡在副作用边界
之外。
