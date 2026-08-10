# Phase 3 / Experiment 12 — Terminal Conflict and ManualRequired

## 一页结论

**PASS（E2：Callback Handler + PostgreSQL）**

实验覆盖两个对称终态冲突：

```text
local Failed    vs provider Succeeded
local Succeeded vs provider Failed
```

两个场景都返回幂等 200，但绝不覆盖本地终态；Contribution Version 保持 0，新增
StateTransition 为 0。每个冲突原子写入一条 ManualRequired ReconciliationRecord、一条包含
完整冲突原因的 OperatorAlert Outbox 和一条 Callback Inbox。

```text
Local overwrite = 0
ManualRequired = 1 per scenario
OperatorAlert = 1 per scenario
Callback Inbox = 1 per scenario
```

`AuditEvent` 表当前没有额外复制同一事实（count=0）。冲突通过专用 ReconciliationRecord、
OperatorAlert payload 和 Callback Inbox 已完整可审计，因此没有为本实验加入第四份重复记录。

## 实验信息

- 日期：2026-08-11
- 测试目录：`tests/Reliant.Tests/Integration/Phase3/Exp12/`
- 测试类：`TerminalConflictManualRequiredTests`
- 数据库：PostgreSQL 17 Testcontainer
- 入口：`HandleProviderCallbackCommand`
- 定位方式：ProviderReference
- 场景：Failed↔Succeeded 双向终态冲突
- Exp12：1/1 passed（一个测试聚合两个场景）

## 假设

```text
本地终态是已提交业务事实
相反的 Provider 终态不能静默覆盖它
系统必须保留冲突证据并通知 Operator
只有人工裁决后才能决定哪个事实正确
```

## 实验设计

每个场景都创建独立数据：

```text
Contribution = Failed or Succeeded / Version=0
ProviderReference = one stable reference
StateTransition = 0 baseline
AuditEvent = 0 baseline
```

然后发送相反的 Provider Callback：

```text
ProviderCallbackPayload
  EventType = contribution.submit
  ProviderReference = seeded reference
  Status = opposite terminal state
```

Callback Handler 使用 ProviderReference 定位 Contribution，在同一个 SaveChanges 中写入：

```text
ReconciliationRecord(ManualRequired)
OperatorAlert Outbox(conflict payload)
Callback Inbox(processed receipt)
```

Contribution 不被修改，也不创建 StateTransition。

## 学生视角：中间过程

### 第一次 Review：旧测试只覆盖相反方向

仓库原有 `TerminalStateConflict_ShouldCreateManualRequiredReconciliation` 使用：

```text
local Succeeded
provider callback Failed
```

它只断言本地仍 Succeeded、存在 ManualRequired 和 OperatorAlert。Phase 3 Checklist 的明确例子
是 `local Failed + provider Succeeded`，而且还要求检查 AuditEvent。

我没有简单再加一条测试，而是删除旧单向用例，换成一个 Exp12 聚合用例，在同一测试中复现
双向冲突。测试总数不增加，同时补齐 Version、Transition、Inbox、alert payload、AuditEvent、
ProviderReference 和记录唯一性断言。

### 场景一：本地 Failed，Provider 报 Succeeded

执行结果：

```text
response = 200 / ManualRequired
Contribution = Failed / Version=0
StateTransition = 0
ReconciliationRecord:
  LocalState = Failed
  ProviderState = Succeeded
  Difference = StateMismatch
  Resolution = ManualRequired
  ResolvedAt = null
OperatorAlert = 1 / Pending
Callback Inbox = 1 / Processed
```

这正面复现 Checklist 指定场景。Provider 的成功回调没有把本地失败静默改写为成功。

### 场景二：本地 Succeeded，Provider 报 Failed

对称场景得到：

```text
response = 200 / ManualRequired
Contribution = Succeeded / Version=0
StateTransition = 0
ReconciliationRecord.LocalState = Succeeded
ReconciliationRecord.ProviderState = Failed
OperatorAlert = 1 / Pending
Callback Inbox = 1 / Processed
```

这保留并增强了旧测试覆盖，证明保护逻辑不是只对一个方向成立。

### Operator Alert 是否包含足够上下文

测试解析 JSON payload，而不是只检查“有一条 Outbox”：

```json
{
  "alert": "Callback conflicts with local terminal state",
  "contributionId": "<exact contribution id>",
  "localState": "Failed or Succeeded",
  "providerState": "Succeeded or Failed"
}
```

Outbox 为 Pending，表示冲突事实和业务事务已提交，后续 Publisher 可以可靠发送 Operator
通知。Callback Inbox 同事务提交，证明 Provider 可以安全重试同一事件。

## AuditEvent Review

Checklist 要求“检查 AuditEvent”，实验确实检查并得到：

```text
AuditEvent count = 0
```

这不是“不可审计”：

| 记录 | 用途 |
| --- | --- |
| ReconciliationRecord | 专用冲突事实、两边状态、ManualRequired 生命周期 |
| OperatorAlert Outbox | 可可靠发送的运维告警及完整 payload |
| Callback Inbox | Provider EventId 的接收与幂等证据 |

额外写一条通用 AuditEvent 会复制同一事实，并引入四份数据的一致性和 retention 成本。当前
专用审计链已经满足 PASS 条件，因此本实验不修改业务代码。

如果未来组织级合规规范明确要求“所有人工处置事件必须进入统一 AuditEvent stream”，可以把
它作为统一审计投影需求实施，而不是只在这个 Handler 临时补一条特殊记录。

## PASS 条件逐项判定

| PASS 条件 | Failed vs Succeeded | Succeeded vs Failed | 判定 |
| --- | --- | --- | --- |
| 不静默覆盖终态 | Failed 保持 | Succeeded 保持 | PASS |
| ManualRequired 被记录 | 1 | 1 | PASS |
| 冲突原因可审计 | record + payload + inbox | record + payload + inbox | PASS |
| Operator Alert | 1 / Pending | 1 / Pending | PASS |

## 最终数据（每个场景）

| 数据 | 最终值 |
| --- | --- |
| Contribution State | 保持原终态 |
| Contribution Version | 0，不变 |
| StateTransition | 0 |
| ReconciliationRecord | 1 / StateMismatch / ManualRequired |
| OperatorAlert Outbox | 1 / Pending / 完整 payload |
| Callback Inbox | 1 / Processed |
| ProviderReference | 1，保持 |
| Orphan Callback | 0 |
| AuditEvent | 0（已知设计边界） |

## 业务代码与测试聚合 Review

```text
生产代码修改：0
数据库 Migration：0
删除：CallbackTests 中1条单向弱用例
新增：Exp12 中1条双向聚合用例
测试总数净变化：0
```

现有 `HandleProviderCallbackHandler` 在任何终态修改前先检查冲突，并将 ReconciliationRecord、
OperatorAlert 和 Inbox 放在同一个 `TrySaveChangesAsync` 原子边界。生产语义已经满足实验，
无需新增状态、锁、表或重复 AuditEvent。

## 当前限制

1. 本实验直接调用 Callback command；Exp7/Exp8 已独立验证 HTTP HMAC、timestamp 和重复 HTTP，
   Exp12 聚焦终态冲突语义，避免重复同一入口证据。
2. OperatorAlert 的 CorrelationId 当前由 Handler 新生成，没有复用 Provider EventId；正式告警
   关联可考虑把 callback event ID 放入 payload/metadata。
3. ManualRequired 目前只表示“需要人工”，尚未实现 operator claim、审批、resolution action
   和关闭时间线；这是正式运维工作流能力，不应在冲突 Handler 中假装完成。
4. 通用 AuditEvent=0；若未来合规策略要求统一审计流，应设计通用投影和 retention，而不是
   复制单个场景。

## 验证命令

```powershell
dotnet build tests/Reliant.Tests/Reliant.Tests.csproj -c Release --no-restore

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build `
  --filter "FullyQualifiedName~Integration.Phase3.Exp12" `
  --logger "console;verbosity=detailed"

dotnet test tests/Reliant.Tests/Reliant.Tests.csproj `
  -c Release --no-build
```

## 我的学习结论

终态冲突不能用“最后写入者获胜”解决。Provider 和本地都可能持有真实但不一致的证据，系统
最安全的自动行为是冻结已提交终态、记录双方事实并升级给 Operator。

```text
opposite terminal callback
  -> no state mutation
  -> ManualRequired evidence
  -> durable operator alert
```

可审计不等于每张审计相关表都必须写一份；关键是有一个明确的 source of truth、完整上下文、
幂等接收证据和可靠告警链路。
