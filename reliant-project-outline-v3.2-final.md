# Project 2: Reliant — Multi-Cloud SaaS Reliability Engineering System (V3.2 Resource-Aware Final)

## 0. 项目重新定位

### 一句话介绍

Reliant 是一个真实运行的多租户 SaaS 系统，以及围绕该系统建立的一整套可靠性工程实践。

项目通过 .NET、PostgreSQL、消息队列、后台 Worker、外部 Provider、Terraform、OpenTelemetry 和 GitHub Actions，在 Azure 与 AWS 上验证应用设计、交付、运行、故障、恢复和持续改进能力。

### 核心目标

Reliant 不再建设一个通用的 SRE Control Plane，也不把 Service Catalog、Incident Management、SLO Management 或 Risk Register 做成独立产品。

它要证明的是：

> 我能够亲自设计、开发和运行一个存在真实分布式系统复杂度的 SaaS 产品，并对它的可用性、延迟、数据正确性、异步处理、发布安全、故障调查和恢复结果承担端到端责任。

### 核心标签

- Senior Site Reliability Engineering
- Production System Ownership
- Multi-tenant SaaS
- AWS + Azure
- .NET
- PostgreSQL
- Messaging and Background Processing
- Terraform
- OpenTelemetry
- SLI / SLO / Error Budget
- k6 Performance Testing
- Incident Response
- Recovery Engineering
- Toil Reduction


### 已有开发资源

本项目按照以下实际资源设计：

- 本地拥有 **LocalStack Ultimate**，作为 AWS-compatible 高保真开发与集成环境；
- Azure 学生订阅拥有 **100 美元额度**，用于真实云部署和恢复证据；
- Docker Desktop、Terraform、GitHub Actions 和本地 PostgreSQL 可用于快速开发循环。

资源策略：

```text
Fast and frequent verification
→ Local containers + LocalStack Ultimate

Real production-like evidence
→ Azure student subscription

Optional final AWS confidence check
→ Short-lived real AWS smoke validation
```

LocalStack Ultimate 可以显著扩大本地 AWS 测试深度，但它仍属于模拟/兼容环境。项目不得把 LocalStack 的成功直接表述成真实 AWS 的性能、网络、IAM 边缘行为、托管数据库恢复或服务配额已经得到验证。

### 项目不是

Reliant 不是：

- 通用 Service Catalog；
- 通用 Incident Management 产品；
- 通用 Observability 平台；
- 通用 SLO 管理系统；
- 通用 Cloud Governance 平台；
- FinOps Dashboard；
- 自动化运维控制平面；
- 为展示技术栈而拆出的十几个微服务。

它首先是一个需要被可靠运行的 SaaS 产品。

---

# 1. 业务场景

## 1.1 产品概念

Reliant 是一个面向组织的多租户活动、贡献记录与通知处理系统。

每个 Tenant 可以：

- 管理组织成员；
- 创建 Campaign 或 Event；
- 接收 Contribution / Order 请求；
- 调用外部 Provider 完成模拟授权或处理；
- 异步生成 Receipt；
- 发送 Email / Webhook 通知；
- 查询处理状态；
- 导出对账结果；
- 重新处理失败任务；
- 查看审计历史。

业务场景只是可靠性工程的载体，不追求商业创新或复杂 UI。

## 1.2 为什么选择这个场景

这个业务天然具备：

- 多租户隔离；
- Public API；
- 数据库事务；
- 外部依赖；
- 消息队列；
- 后台处理；
- 重复消息；
- 延迟任务；
- Webhook；
- 数据对账；
- 用户可见状态；
- 高风险发布；
- 恢复和重放需求。

因此它能够产生真正值得研究的 SRE 问题，而不是人为拼凑基础设施。

## 1.3 支付与敏感数据边界

Reliant 不成为真实支付处理器：

- 不保存银行卡信息；
- 不处理 PCI 范围内的 Cardholder Data；
- 使用 Sandbox Provider 或受控模拟 Provider；
- 只保存 Provider Reference、状态和非敏感元数据；
- 所有 Payment-like 操作仅用于研究幂等性、超时、重试和对账。

---

# 2. 核心用户旅程

## 2.1 Tenant 管理员

- 创建 Organization；
- 邀请成员；
- 配置 Campaign；
- 配置 Notification 和 Webhook；
- 查看处理状态；
- 查看失败项；
- 发起受控重试。

## 2.2 最终用户

- 提交 Contribution / Order；
- 获得稳定的 Request ID；
- 查询处理结果；
- 收到 Receipt 或通知；
- 重复提交不会产生重复业务结果。

## 2.3 Support / Operator

- 按 Correlation ID 调查请求；
- 查看 API、数据库、队列、Worker 和 Provider 时间线；
- 查询 Dead-letter；
- 执行受控 Replay；
- 验证恢复结果；
- 使用 Runbook 处理已知故障。

## 2.4 SRE

- 维护 SLI/SLO；
- 分析 Error Budget；
- 执行负载和故障测试；
- 调查跨系统事故；
- 改进发布门禁；
- 消除重复告警和人工操作；
- 演练备份、恢复与云环境重建。

---

# 3. 业务不变量

可靠性不仅是 HTTP 200，还包括业务正确性。

Reliant 必须保护以下不变量：

1. 同一个 Idempotency Key 不产生两个 Contribution；
2. 外部 Provider 超时后不能盲目重复创建业务结果；
3. 一个 Contribution 只能进入合法状态；
4. Receipt 不能在业务处理成功前发送；
5. Webhook 重复投递不能造成重复副作用；
6. Tenant A 永远不能读取 Tenant B 的数据；
7. Queue 重复投递不能造成重复处理；
8. Worker Crash 不能让任务永久丢失；
9. 数据库事务提交后，必要异步事件最终能够发布；
10. Reconciliation 能发现本地状态与 Provider 状态不一致；
11. Rollback 不能破坏已经提交的数据；
12. Migration 不得造成静默数据截断。

这些不变量必须通过数据库约束、状态机、幂等性、Outbox/Inbox、测试和 Reconciliation 共同保护。

---

# 4. 系统架构

## 4.1 总体架构风格

Reliant 使用一个有意控制规模的小型分布式系统，而不是通用控制平面。

R1 采用 Monorepo，并只保留两个长期运行的部署单元：

```text
Client
  ↓
Public API Host
  ↓
PostgreSQL
  ├── Domain State
  └── Outbox
          ↓ polled by
Unified Worker Host
  ├── Outbox Publisher → Message Queue
  ├── Processing Handler ← Message Queue → External Provider
  ├── Reconciliation Handler → External Provider
  ├── Notification / Webhook Handler ← Message Queue → Notification Endpoint
  └── Scheduled Maintenance Handler
```

另外存在两个非长期运行组件：

- **Migrator**：作为 Release Job 独立执行数据库迁移；
- **OpenTelemetry Collector**：作为基础设施组件独立运行，不属于 Reliant 业务服务拆分。

## 4.2 长期运行部署单元

### Public API Host

负责：

- HTTP Contract；
- Authentication；
- Tenant Resolution；
- Input Validation；
- Idempotency；
- Rate Limiting；
- Command Acceptance；
- Query；
- Problem Details；
- Correlation；
- 将业务事件写入 Outbox。

### Unified Worker Host

在同一个可执行 Host 中运行多个相互隔离的 Handler：

#### Processing Handler

- 消费业务消息；
- 调用外部 Provider；
- 执行状态转换；
- Retry；
- Checkpoint；
- Dead-letter；
- Unknown Outcome 处理。

#### Notification / Webhook Handler

- Receipt；
- Email Adapter；
- Webhook；
- Signature；
- Delivery Retry；
- Delivery Audit；
- Dead-letter；
- Replay。

#### Reconciliation Handler

- 查询 Provider 最终状态；
- 处理本地与 Provider 的状态差异；
- 生成 Reconciliation Evidence；
- 将高风险差异交给 Operator。

#### Scheduled Maintenance Handler

- Expired Lease Recovery；
- Retry Due Items；
- Retention；
- Operational Maintenance；
- 定期 Reconciliation。

不同 Handler 必须拥有独立：

- Queue / Subscription；
- Concurrency Limit；
- Retry Policy；
- Metrics；
- Health Signal；
- Failure Isolation Boundary。

R1 不把这些 Handler 拆成独立服务。只有出现明确的独立扩缩、故障隔离、安全身份或发布周期需求时，才通过 ADR 拆分。

## 4.3 业务与数据边界

R1 保留两个逻辑业务边界，但不强制形成两个网络服务。

### Transaction Boundary

拥有：

- Organization；
- Campaign；
- Contribution；
- Provider Processing；
- Transaction State；
- Reconciliation；
- Primary Outbox。

Public API 和 Unified Worker 中的 Processing/Reconciliation Handler 共同使用该边界。

### Notification Boundary

拥有：

- Notification；
- Webhook Subscription；
- Delivery Attempt；
- Inbox；
- Delivery Audit；
- Dead-letter State。

Notification Handler 通过消息 Contract 接收事件，不能直接修改 Transaction 内部表。R1 可以使用独立 Schema；只有在真实隔离需求出现后才升级为独立 Database 或独立服务。

## 4.4 为什么只保留两个长期运行部署单元

Reliant 需要足够的分布式复杂度来研究：

- 跨进程 Trace；
- 消息重复；
- Queue Backlog；
- Worker Crash；
- Provider Failure；
- 数据一致性；
- 独立扩缩；
- 故障隔离。

但它不需要模拟十个团队和十几个微服务。

两个部署单元已经能够产生真实的：

```text
Synchronous Request Boundary
→ Database Transaction
→ Asynchronous Message Boundary
→ External Dependency Boundary
```

同时避免为了展示微服务而增加：

- 多套部署；
- 多套身份；
- 多套数据库；
- 服务发现；
- 额外网络故障；
- 大量 Contract Versioning；
- 无真实团队 Ownership 的运维负担。

拆分必须由证据驱动，而不是由模块名称驱动。

# 5. 核心领域模型

```text
Organization
├── Membership
├── Campaign
├── Contribution
│   ├── ProcessingAttempt
│   ├── ProviderReference
│   ├── StateTransition
│   └── ReconciliationRecord
├── Receipt
├── Notification
├── WebhookSubscription
├── WebhookDelivery
└── AuditEvent
```

关键实体包括：

- Organization
- Membership
- Campaign
- Contribution
- ContributionState
- ProcessingAttempt
- ProviderReference
- IdempotencyRecord
- OutboxMessage
- InboxMessage
- JobDefinition
- JobRun
- JobAttempt
- Lease
- Checkpoint
- ReconciliationRecord
- Notification
- DeliveryAttempt
- WebhookSubscription
- WebhookDelivery
- DeadLetterRecord
- DeploymentRecord
- AuditEvent

---

# 6. 多租户与身份

## 6.1 Tenant 模型

- Organization 是业务 Tenant；
- TenantContext 只能来自受信任身份和 Membership；
- 客户端输入不能直接建立 Tenant Authority；
- 所有 Tenant-owned 表必须包含 Tenant Boundary；
- Unique Index、Cache Key、Queue Message 和 Object Path 都必须包含 Tenant Scope。

## 6.2 身份与授权

使用 OIDC：

- Microsoft Entra ID 或开发 Identity Provider；
- JWT Signature、Issuer、Audience 和 Expiry 验证；
- User Membership；
- Service Identity；
- Worker Identity；
- Operator Role。

建议角色：

- Owner；
- Administrator；
- Operator；
- Analyst；
- Auditor。

## 6.3 高风险操作

以下操作需要更高权限和审计：

- Replay Dead-letter；
- Retry Provider Processing；
- Cancel Processing；
- Change Webhook；
- Export；
- Manual Reconciliation；
- Rollback；
- Break-glass Recovery。

授权结果审计和业务执行结果审计必须分开。

---

# 7. 数据与一致性设计

## 7.1 PostgreSQL

PostgreSQL 保存：

- Tenant 和 Membership；
- Campaign；
- Contribution；
- 状态历史；
- Idempotency；
- Processing Attempt；
- Outbox / Inbox；
- Job State；
- Reconciliation；
- Notification；
- Audit。

## 7.2 事务与 Outbox

业务事务中同时提交：

- Domain State；
- Outbox Message；
- Audit Metadata。

Outbox Publisher 异步发送消息。

必须验证：

- DB 提交后 Publisher Crash；
- 重复发布；
- 消息乱序；
- Broker 暂时不可用；
- Publisher 重启；
- Poison Message。

## 7.3 Inbox 与幂等消费

每个 Consumer 必须：

- 使用稳定 Message ID；
- 保存 Inbox；
- 防止重复副作用；
- 支持 Handler Version；
- 记录 Attempt；
- 对永久失败进入 Dead-letter；
- 对暂时失败执行有限 Retry。

不宣称真正的 Exactly-once Delivery，而是通过 At-least-once + Idempotency 实现业务上可接受的 Exactly-once Effect。

## 7.4 状态机

Contribution 示例状态：

```text
Created
→ Accepted
→ Processing
→ Succeeded
→ ReceiptPending
→ Completed
```

失败路径：

```text
Processing
→ RetryPending
→ Processing
→ Failed
```

不确定路径：

```text
Processing
→ ProviderUnknown
→ ReconciliationPending
→ Succeeded / Failed
```

状态转换必须：

- 有数据库约束；
- 有并发控制；
- 有审计；
- 有测试；
- 禁止跳过中间状态。

## 7.5 Optimistic Concurrency

关键资源使用：

- Version；
- ETag；
- Row Version；
- Conditional Update。

必须覆盖：

- 两个 Worker 同时处理；
- Operator 与 Worker 同时修改；
- 重复 Callback；
- Retry 与 Cancel 竞争。

## 7.6 Reconciliation

定期比较：

- 本地 Contribution State；
- Provider State；
- Outbox / Inbox；
- Notification Delivery；
- Queue Dead-letter。

Reconciliation 不能静默修复高风险差异，必须：

- 记录差异；
- 分类；
- 生成 Evidence；
- 自动修复低风险项目；
- 高风险项目要求人工确认。

---

# 8. 异步处理可靠性

## 8.1 Job 模型

至少包含：

- JobDefinition；
- JobRun；
- JobAttempt；
- Trigger；
- Lease；
- Heartbeat；
- Checkpoint；
- Timeout；
- Cancellation；
- ErrorCategory；
- Result；
- RetryPolicy。

## 8.2 Retry

Retry 必须区分：

- Timeout；
- 429；
- Provider 5xx；
- Network Failure；
- Validation Failure；
- Authentication Failure；
- Permanent Business Rejection；
- Unknown Outcome。

策略包括：

- Exponential Backoff；
- Jitter；
- Maximum Attempts；
- Retry Budget；
- Provider-specific Limit；
- Circuit Breaker；
- Dead-letter。

## 8.3 Backpressure

需要处理：

- Queue Backlog；
- Worker Saturation；
- Database Saturation；
- Provider Rate Limit；
- Notification Storm。

手段包括：

- Bounded Concurrency；
- Queue-based Autoscaling；
- Admission Control；
- Rate Limit；
- Batch；
- Pause / Resume；
- Priority；
- Graceful Degradation。

## 8.4 Graceful Shutdown

Worker 收到终止信号时：

- 停止领取新任务；
- 更新 Heartbeat；
- 完成或安全中断当前任务；
- 保存 Checkpoint；
- 释放 Lease；
- 不确认尚未完成的消息；
- 记录 Shutdown Evidence。

---

# 9. 外部 Provider 设计

## 9.1 Provider Adapter

Provider Contract 包括：

- Submit；
- Query Status；
- Cancel（仅在 Provider 支持时）；
- Reconcile；
- Health / Capability；
- Error Classification。

## 9.2 故障语义

必须覆盖：

- 429；
- 5xx；
- Timeout；
- Connection Reset；
- Malformed Response；
- Slow Response；
- Duplicate Callback；
- Callback Before API Response；
- Unknown Transaction；
- Expired Credential；
- Wrong Signature。

## 9.3 Unknown Outcome

最危险的场景：

```text
Request sent
→ Provider processed it
→ Response lost
→ Local system sees timeout
```

系统不能直接再次创建。

必须：

- 使用 Provider Idempotency Key；
- 保存 Attempt；
- 进入 Unknown State；
- Query / Reconcile；
- 只在证据明确后结束状态。

---

# 10. 双云部署与验证模型

## 10.1 设计原则

双云的目标不是实现昂贵的 Active-Active，而是证明：

- 相同应用 Artifact 可以面向 Azure 和 AWS 服务模型运行；
- 云服务差异被明确处理；
- 应用核心不依赖某一云 SDK；
- AWS 适配可以在本地高频验证；
- Azure 可以提供真实 Production-like 运行证据；
- 环境可以重建、演练和清理；
- 每项结论都标记其 Evidence Level。

## 10.2 Evidence Level

Reliant 使用四级证据模型：

| Level | 环境 | 可以证明 | 不能单独证明 |
| --- | --- | --- | --- |
| E1 | Unit / Testcontainers | 代码、数据库和局部组件行为 | 云服务语义 |
| E2 | LocalStack Ultimate | AWS API、Terraform、服务集成、SQS 语义、故障注入和清理流程 | 真实 AWS 性能、配额、网络和托管服务边缘行为 |
| E3 | Real Azure | Production-like 部署、身份、网络、数据库、队列、Backup/Restore 和真实云故障 | 真实 AWS 行为 |
| E4 | Real AWS Smoke | 选定 AWS 路径的真实控制面和数据面信心 | 完整生产规模和长期运行 |

每份 Evidence 必须标明 `E1 / E2 / E3 / E4`。

## 10.3 Azure 路径

Azure 是 R1 的真实 Production-like 主环境：

```text
Azure Public Endpoint
→ Azure Container Apps
→ Azure Database for PostgreSQL
→ Azure Service Bus
→ Application Insights / OpenTelemetry
→ Key Vault
```

R1 成本控制：

- Redis 默认不启用，只有明确性能证据后再增加；
- Front Door 默认不启用；
- 选择当前学生订阅可用的最低合适 SKU；
- 数据库和应用环境按实验创建，不长期空闲运行；
- Telemetry 设置采样、保留期和数据量限制；
- 所有资源带 `Owner`、`Purpose`、`Expiry` 和 `Environment` Tag；
- 每次实验结束执行 Terraform Destroy 和残留检查。

100 美元额度建议分配：

- 不超过 70% 用于计划内 Azure 构建、负载、故障和 Restore；
- 至少保留 30% 处理配额、失败重建和最终 Demo；
- 达到预警阈值后停止新的云实验，先完成本地验证。

实际 SKU 和成本在实施时依据订阅可用性重新确认。

## 10.4 AWS-compatible LocalStack Ultimate 路径

LocalStack Ultimate 是 R1 的 AWS 主开发与验证环境：

```text
Local Application Runtime / ECS-compatible Path
→ LocalStack SQS
→ LocalStack IAM / STS
→ LocalStack Secrets Manager
→ LocalStack S3
→ LocalStack CloudWatch-compatible APIs
→ PostgreSQL or LocalStack-supported RDS Path
```

优先验证：

- Terraform Plan / Apply / Destroy；
- SQS Visibility Timeout；
- Message Redelivery；
- Dead-letter Queue；
- IAM / STS 调用边界；
- Secrets Manager；
- S3 Evidence Storage；
- CloudWatch-compatible Telemetry Contract；
- ECS/RDS 控制面与应用部署脚本；
- 错误注入、API 失败、重试和清理；
- 同一 Artifact 和配置 Contract。

LocalStack 具体服务行为必须由当前安装版本实际验证。某项服务即使名义上可用，也不能在没有运行证据时进入完成状态。

## 10.5 云适配边界

Application 层只依赖：

- Message Bus Contract；
- Object Storage Contract；
- Secret Provider Contract；
- Telemetry Contract；
- Provider Contract。

Azure Service Bus 与 SQS 的差异必须保留：

- Lock / Visibility Timeout；
- Dead-letter；
- Delivery Count；
- Ordering；
- Session / FIFO；
- Delay；
- Receive Model。

不做虚假的“完全一致队列接口”。

Terraform 和应用配置必须支持明确 Profile：

```text
local
localstack-aws
azure-real
aws-real-smoke
```

代码不得通过散落的 `if localstack` 污染业务逻辑。

## 10.6 R1 多云范围

### Azure：E3 完整 Production-like 验证

必须完成：

- 完整应用部署；
- PostgreSQL、Service Bus、Worker 和 Telemetry；
- OIDC / Managed Identity；
- Migration；
- Rollback；
- 至少一个真实云故障与恢复；
- PostgreSQL Backup；
- 独立 Restore Environment；
- Restore 后业务不变量与 Reconciliation；
- Terraform 重建和清理。

### AWS：E2 LocalStack Ultimate 深度验证

必须完成：

- 与 Azure 相同的应用 Artifact；
- AWS Terraform Profile；
- SQS、IAM/STS、Secrets Manager、S3 等关键集成；
- Visibility、Redelivery 和 Dead-letter；
- 至少一个 AWS-compatible 故障与恢复；
- 本地环境重建和清理；
- Azure/AWS 运维差异报告。

### Real AWS：E4 选择性 Smoke Validation

当账户、预算和时间允许时，执行短生命周期真实 AWS 验证：

- 部署最小 API / Worker 路径；
- 验证真实 SQS；
- 验证 IAM Role；
- 运行一个端到端请求；
- 收集 Evidence；
- 立即 Destroy。

E4 不是 R1 Core 的硬门禁，但决定最终简历措辞。

## 10.7 简历和 README 声明规则

只有完成 E4，才能写：

> Deployed and validated across Azure and AWS.

只完成 E2 + E3 时，必须写：

> Built and operated on Azure, with the AWS deployment path validated through LocalStack Ultimate.

不得把 LocalStack 写成真实 AWS Production Evidence。

## 10.8 R2 多云恢复

R2 可以加入：

- 真实 AWS PostgreSQL / RDS Restore Drill；
- 从 Azure Backup / Export 恢复到 AWS；
- 或反向恢复；
- DNS / Client Cutover 演练；
- 数据可移植性评估；
- RTO/RPO 对比。

不宣称零停机跨云 Failover。

# 11. Infrastructure as Code

## 11.1 Terraform

Terraform 管理：

- Networking；
- Runtime；
- PostgreSQL；
- Queue；
- Identity；
- Secret Store；
- Telemetry；
- Object Storage；
- Monitoring；
- Backup；
- Environment Configuration。

## 11.2 执行 Profile

必须提供明确、可审查的执行 Profile：

### `local`

- Docker Compose；
- PostgreSQL；
- 本地 Provider Simulator；
- 本地 OTel/Grafana。

### `localstack-aws`

- `tflocal` 或集中式 AWS Provider Endpoint Configuration；
- LocalStack Ultimate；
- 独立命名和 State；
- 禁止误连真实 AWS；
- 自动 Health Check；
- 自动 Reset / Cleanup；
- 可选 Cloud Pod 或等价 Snapshot 加速复验。

### `azure-real`

- Azure 学生订阅；
- GitHub Actions OIDC；
- 短生命周期环境；
- Budget Guard；
- Expiry Tag；
- Destroy Verification。

### `aws-real-smoke`

- 默认禁用；
- 明确 Account / Region；
- 独立审批；
- 严格成本上限；
- 短生命周期；
- 仅用于 E4 Evidence。

## 11.3 State

- 不同 Profile 独立 State；
- Azure 使用 Remote State、Encryption 和 Locking；
- LocalStack 可以使用隔离的本地或测试 State；
- Real AWS Smoke 使用独立临时 State；
- No Secret Output；
- State Backup；
- Drift Detection；
- Provider Endpoint 和 Account Guard。

## 11.4 模块策略

只在第二个真实使用方出现后提取公共模块。

允许：

- Azure Environment Module；
- AWS Environment Module；
- Shared Naming / Tagging Convention；
- Provider-independent Input Contract；
- LocalStack Test Harness。

禁止：

- 为了“多云统一”构建隐藏所有差异的超大模块；
- 在业务代码中散落 LocalStack 特殊判断；
- 让 LocalStack Profile 能无提示连接真实 AWS；
- 使用同一个 State 管理模拟和真实云。

## 11.5 生命周期

每个环境必须支持：

```text
Plan
→ Apply
→ Health Check
→ Verify
→ Exercise
→ Collect Evidence
→ Destroy / Reset
→ Verify Cleanup
```

Azure 和 Real AWS 必须检查云端残留；LocalStack 必须检查资源 Reset 后的状态。

# 12. 可观测性

## 12.1 OpenTelemetry

覆盖：

- Public API；
- PostgreSQL；
- Outbox Publisher；
- Queue Producer / Consumer；
- Unified Worker Host；
- Processing Handler；
- Notification / Webhook Handler；
- Reconciliation Handler；
- External Provider；
- Reconciliation；
- Deployment Version。

## 12.2 Trace

必须支持：

```text
HTTP Request
→ DB Transaction + Outbox
→ Outbox Publisher
→ Queue
→ Unified Worker / Processing Handler
→ External Provider
→ DB Transaction + Outbox
→ Queue
→ Unified Worker / Notification Handler
→ Webhook
```

Queue 传播：

- Trace Context；
- Correlation ID；
- Causation ID；
- Message ID；
- Tenant ID 的安全表示；
- Deployment Version。

## 12.3 Metrics

### API

- Request Rate；
- Error Rate；
- Latency；
- In-flight Requests；
- Rate-limit Rejection。

### Database

- Connection Pool；
- Query Duration；
- Lock Wait；
- Transaction Failure；
- Migration Duration。

### Queue

- Backlog；
- Oldest Message Age；
- Delivery Count；
- Dead-letter Count；
- Processing Delay。

### Worker

- Run Duration；
- Success / Failure；
- Retry；
- Lease Age；
- Heartbeat；
- Active Concurrency；
- Checkpoint Lag。

### Provider

- Latency；
- 429；
- 5xx；
- Timeout；
- Unknown Outcome；
- Circuit State。

### Business Correctness

- Duplicate Prevented；
- Reconciliation Difference；
- Processing Freshness；
- Notification Delivery；
- Stuck State。

## 12.4 Logs

Structured Logs 至少包含：

- Timestamp；
- Level；
- Trace ID；
- Span ID；
- Correlation ID；
- Causation ID；
- Message ID；
- Tenant-safe Identifier；
- Deployment Version；
- Error Category。

禁止记录：

- Token；
- Secret；
- Card Data；
- Raw Sensitive Payload；
- Full Personal Information。

## 12.5 Dashboard

需要能够回答：

1. 用户现在是否受到影响？
2. 影响哪个 Tenant、接口或流程？
3. 错误从什么时候开始？
4. 最近是否发生 Deployment 或 Configuration Change？
5. 问题在 API、DB、Queue、Worker 还是 Provider？
6. Backlog 是否在增长？
7. Recovery 是否已经生效？
8. SLO 是否仍在燃烧？

---

# 13. SLI、SLO 与 Error Budget

## 13.1 用户体验 SLI

### API Availability

符合条件请求中成功响应的比例。

### API Latency

关键 API 在目标延迟内完成的比例。

### Contribution Processing Success

已接受任务在目标时间内成功完成或进入可解释终态的比例。

### Processing Freshness

任务从接受到完成的时间。

### Receipt / Webhook Delivery

成功业务结果在目标时间内完成通知的比例。

## 13.2 正确性 SLI

- Duplicate Business Result Rate；
- Reconciliation Mismatch Rate；
- Stuck Transaction Rate；
- Lost Message Rate；
- Incorrect Tenant Access Rate。

正确性 SLI 不能被普通 Availability SLO 掩盖。

## 13.3 建议 R1 SLO

示例目标，不在验证前视为最终承诺：

```text
API Availability:
99.9% over 30 days

API Latency:
95% of critical requests under 500 ms

Processing Freshness:
99% of accepted contributions reach a terminal or reconcilable state within 2 minutes

Notification Delivery:
99% of successful contributions produce a receipt or valid delivery state within 5 minutes
```

## 13.4 Error Budget

需要计算：

- Remaining Budget；
- Fast Burn；
- Slow Burn；
- Deployment-related Burn；
- Provider-related Burn；
- Internal vs External Responsibility。

## 13.5 告警

优先使用：

- Multi-window Burn-rate；
- Queue Age；
- Stuck State；
- Reconciliation Difference；
- Dead-letter Growth；
- Database Saturation。

不因为单个瞬时错误立即触发高优先级告警。

---

# 14. 软件交付与变更安全

## 14.1 CI

每个 Pull Request 至少运行：

- Format；
- Build；
- Unit Tests；
- Architecture Tests；
- Integration Tests；
- Contract Tests；
- Migration Tests；
- Terraform Validation；
- Secret Scan；
- Dependency Scan；
- Container Scan；
- SBOM；
- OpenAPI Compatibility；
- k6 Smoke。

## 14.2 Artifact

- Build Once；
- Immutable Artifact；
- Commit SHA；
- SBOM；
- Vulnerability Result；
- Signed or Verifiable Digest；
- Promotion History。

Development、Staging 和 Production-like 环境必须晋级同一 Artifact。

## 14.3 Database Migration

遵循：

- Independent Migrator；
- Expand / Migrate / Contract；
- Backward Compatibility；
- Lock Review；
- Duration Measurement；
- Rollback / Forward-fix Decision；
- Reconciliation；
- No Startup Migration。

## 14.4 Deployment Strategy

R1 使用：

- Rolling 或 Revision-based Deployment；
- Smoke Test；
- Canary Traffic；
- Deployment Marker；
- Automated Health Check；
- Manual or Automated Rollback Gate。

## 14.5 Feature Flag

高风险行为通过 Feature Flag 控制：

- 新 Provider Path；
- 新 Worker Handler；
- 新 Notification Adapter；
- 新 Retry Policy；
- 新 Data Projection。

Feature Flag 必须有：

- Owner；
- Expiry；
- Default；
- Rollback Behavior；
- Audit。

## 14.6 k6 Release Gate

k6 验证：

- Critical API；
- Concurrent Requests；
- Error Rate；
- p95 / p99；
- Database Pool；
- Queue Backlog；
- Worker Throughput；
- Recovery after Load。

Threshold 必须与 SLO 或容量假设有关，不能只是随意数字。

---

# 15. 核心故障场景

R1 固定为 **六个必做场景**。其他场景保留为 Stretch 或 R2，不再同时作为 R1 Gate。

## 15.1 R1 必做场景一：Deployment Regression

### 注入

发布一个引入 5xx 或延迟回归的版本。

### 需要证明

- Deployment Marker；
- Error Rate / Latency 上升；
- Burn-rate Alert；
- Trace 定位；
- Canary、k6 或 Release Gate 捕获；
- Rollback；
- 指标和业务状态恢复；
- Follow-up Regression Test。

## 15.2 R1 必做场景二：Database Saturation and Lock Contention

### 注入

通过并发、慢查询、连接池缩小或长事务制造数据库性能故障。

### 需要证明

- Connection Pool Saturation；
- Query Duration / Lock Wait；
- API Latency 和 Timeout；
- Queue 影响；
- Trace 和 Database Metrics 定位；
- Query、Index、Pool、Timeout 或 Admission Control 改进；
- k6 前后对比；
- 无数据破坏。

该场景把原来的 Connection Pool Saturation 与 Slow Query / Lock 合并为一个完整数据库事故。

## 15.3 R1 必做场景三：Queue Backlog

### 注入

降低 Worker Throughput、暂停 Consumer 或制造下游变慢。

### 需要证明

- Queue Age；
- Processing Freshness SLO；
- Backpressure；
- Bounded Concurrency；
- Worker 恢复或扩容；
- 无消息丢失；
- Backlog 最终清空；
- 恢复时间可量化。

## 15.4 R1 必做场景四：Worker Crash and Duplicate Redelivery

### 注入

Worker 在副作用执行前后或消息确认前终止，使消息重新投递。

### 需要证明

- Message Redelivery；
- Lease / Visibility Recovery；
- Inbox 和 Idempotency；
- 同一个 Message ID 不产生重复业务结果；
- Processing 最终完成；
- Graceful Shutdown 与非优雅 Crash 的差异；
- Duplicate Prevention Metric。

该场景将 Worker Crash 与 Duplicate Message 合并，避免把同一可靠性链路拆成两个较浅场景。

## 15.5 R1 必做场景五：Provider Timeout and Unknown Outcome

### 注入

Provider 已经处理请求，但响应在返回途中丢失或超时。

### 需要证明

- 不盲目重复创建；
- Provider Idempotency Key；
- Unknown State；
- ProcessingAttempt；
- Query / Reconciliation；
- 最终一致状态；
- 用户可解释状态；
- 无重复业务结果。

## 15.6 R1 必做场景六：Migration Failure

### 注入

引入不兼容 Schema、长锁、错误数据转换或新旧版本不兼容。

### 需要证明

- Pre-deployment Migration Gate；
- Migration Fail Closed；
- Expand / Migrate / Contract；
- Old Version Compatibility；
- Forward Fix 或 Rollback Decision；
- 数据无静默丢失；
- Deployment 未在失败 Migration 后继续晋级。

## 15.7 R1 Stretch / R2 场景

以下场景仍然有价值，但不属于 R1 的六个硬门禁：

- Poison Message；
- Provider 429 / 5xx；
- Webhook Failure；
- Telemetry Backend Failure；
- Cache Failure；
- AWS Task Termination；
- Credential Expiry；
- Database Failover；
- Queue Service Failure；
- Network Dependency Failure。

其中 Provider 429、Poison Message 和 Telemetry Backend Failure 仍应在对应模块中具有自动化负向测试，但不要求全部形成完整 Incident Evidence Pack。

## 15.8 独立恢复演练

Azure PostgreSQL Backup / Restore 是 R1 必做恢复演练，但它不占用六个故障场景名额。

AWS Backup / Restore 属于缓冲或 R2。

# 16. 恢复工程

## 16.1 RTO / RPO

为不同能力定义：

- API；
- Transaction Data；
- Queue Processing；
- Notification；
- Audit；
- Telemetry。

RTO/RPO 必须经过演练，而不是只写目标。

## 16.2 PostgreSQL Backup / Restore

R1 范围：

- Azure 必须完成完整 Backup / Restore；
- AWS 必须配置合理的数据保护参数，但完整 Restore Drill 可进入缓冲或 R2。

需要验证：

- Automated Backup；
- Point-in-time Recovery；
- Independent Restore Environment；
- Schema Version；
- Data Count；
- Business Invariant；
- Reconciliation；
- Restore Duration。

## 16.3 Queue Recovery

- Dead-letter Replay；
- Message Redelivery；
- Duplicate Protection；
- Checkpoint；
- Replay Authorization；
- Audit；
- Maximum Replay Count。

## 16.4 Deployment Rollback

验证：

- Previous Artifact 可用；
- Database Compatibility；
- Configuration Compatibility；
- Feature Flag；
- Metrics Recovery；
- No Data Corruption。

## 16.5 Environment Rebuild

通过 Terraform：

- 创建新环境；
- 部署 Artifact；
- 执行 Migration；
- 恢复数据；
- Smoke Test；
- 切换；
- 清理。

## 16.6 Cross-cloud Recovery

R2 目标：

- 从 Azure Backup / Export 恢复到 AWS；
- 或反向恢复；
- 记录数据转换和服务差异；
- 实测 Recovery Time；
- 明确无法自动化的部分。

---

# 17. 针对性 Toil Reduction

Reliant 不构建通用自动化平台，只为真实重复工作提供工具。

建议实现 `reliantctl`：

```text
reliantctl diagnostics collect
reliantctl jobs inspect
reliantctl jobs retry
reliantctl deadletter list
reliantctl deadletter replay
reliantctl reconciliation run
reliantctl migration verify
reliantctl deployment verify
reliantctl recovery validate
reliantctl environment cleanup
```

每个操作必须具备：

- RBAC；
- Dry-run（适用时）；
- Idempotency；
- Timeout；
- Audit；
- Result；
- Before / After Evidence。

---

# 18. 安全边界

## 18.1 Application Security

- OIDC；
- Tenant Isolation；
- RBAC；
- Stable Error Contract；
- Rate Limiting；
- Input Validation；
- Output Encoding；
- Secure Headers；
- Sensitive Data Redaction；
- Audit。

## 18.2 Cloud Identity

- GitHub Actions OIDC；
- Azure Managed Identity；
- AWS IAM Role；
- No Long-lived Cloud Secrets；
- Least Privilege；
- Separate Deployment and Runtime Identity。

## 18.3 Secret

- Key Vault / Secrets Manager；
- Rotation；
- No Secret in Terraform Output；
- No Secret in Logs；
- Startup Validation；
- Expiry Alert。

## 18.4 Webhook Security

- Signature；
- Timestamp；
- Replay Protection；
- Secret Rotation；
- TLS；
- Delivery Audit。

## 18.5 Supply Chain

- Dependency Scan；
- Container Scan；
- SBOM；
- Immutable Artifact；
- Provenance；
- Protected Branch；
- Required Checks。

---

# 19. 测试策略

## 19.1 Unit Tests

- State Machine；
- Idempotency；
- Retry Classification；
- SLO Calculation；
- Reconciliation；
- Authorization；
- Webhook Signature。

## 19.2 Architecture Tests

- Domain 不依赖 Infrastructure；
- Application 不依赖云 SDK；
- Provider SDK 只存在于 Infrastructure；
- Notification Boundary 不直接访问 Transaction 内部表；
- API/Worker 不执行 Startup Migration。

## 19.3 Integration Tests

采用分层集成策略：

### E1：Testcontainers

- PostgreSQL；
- Outbox；
- Inbox；
- Worker；
- Migration；
- Provider Simulator；
- OTel Export。

### E2：LocalStack Ultimate

- SQS；
- Dead-letter；
- Visibility Timeout；
- IAM / STS；
- Secrets Manager；
- S3；
- CloudWatch-compatible Contract；
- AWS Terraform Apply / Destroy；
- AWS Adapter Failure Injection。

### E3：Real Azure

- Service Bus；
- Managed Identity；
- Key Vault；
- PostgreSQL；
- Container Apps；
- Backup / Restore；
- Production-like Networking。

### E4：Real AWS Smoke（可选）

- SQS；
- IAM Role；
- Minimal Runtime；
- End-to-End Smoke；
- Cleanup。

## 19.4 Contract Tests

- Provider；
- Queue；
- Webhook；
- OpenAPI；
- Deployment Event；
- Azure / AWS Adapter。

## 19.5 End-to-End Tests

```text
Create Tenant
→ Create Campaign
→ Submit Contribution
→ Process
→ Send Receipt
→ Deliver Webhook
→ Query Final State
```

覆盖成功、失败、超时、重复和恢复。

## 19.6 Load Tests

- Baseline；
- Step Load；
- Spike；
- Soak；
- Queue Backlog；
- Recovery；
- Provider Rate Limit。

## 19.7 Failure and Recovery Tests

每个核心故障场景必须可重复执行并输出 Evidence。

---

# 20. 运维与 On-call 证据

虽然个人项目没有真实 On-call Team，但可以真实建设可操作性。

## 20.1 Runbook

至少包括：

- High API Error Rate；
- High Latency；
- DB Pool Saturation；
- Queue Backlog；
- Worker Crash Loop；
- Dead-letter Growth；
- Provider Outage；
- Stuck Transaction；
- Migration Failure；
- Backup Restore；
- Rollback；
- Secret Expiry。

## 20.2 Incident Report

每个核心场景记录：

- Impact；
- Detection；
- Timeline；
- Diagnosis；
- Mitigation；
- Recovery；
- Root Cause；
- Contributing Factors；
- What Went Well；
- What Went Poorly；
- Follow-up；
- Preventive Control。

事实、推断和未知项必须分开。

## 20.3 Operational Review

每次 Release 回答：

- 用户风险是什么？
- Error Budget 是否允许发布？
- Migration 是否安全？
- Rollback 是否可用？
- Provider 是否稳定？
- 当前容量是否足够？
- 新告警是否可行动？
- Runbook 是否更新？

---

# 21. 工程文档体系

建议目录：

```text
docs/
├── vision.md
├── current-state.md
├── roadmap.md
├── architecture/
│   ├── system-context.md
│   ├── containers.md
│   ├── azure-deployment.md
│   ├── aws-deployment.md
│   ├── data-flow.md
│   ├── trust-boundaries.md
│   └── telemetry-flow.md
├── adr/
├── slo/
│   ├── definitions.md
│   ├── error-budget-policy.md
│   └── alert-policy.md
├── runbooks/
├── incidents/
├── risks/
├── recovery/
├── performance/
├── releases/
├── evidence/
│   ├── deployments/
│   ├── failures/
│   ├── recovery/
│   └── load-tests/
└── ai/
    ├── pair-programming-protocol.md
    ├── task-template.md
    └── review-template.md
```

Reliant 不需要把 Service Catalog、Incident 或 Risk Register 做成 Web 产品。

它们分别通过：

- `service.yaml` / README；
- Incident Markdown；
- Risk Register Markdown；
- Prometheus / Grafana SLO；
- GitHub Actions Deployment Marker；
- CLI 和 Runbook；

完成工程职责。

---

# 22. AI 结对编程与防劣化

## 22.1 AI Task 输入

每个任务必须包含：

```text
Task
Reliability value
Current facts
Allowed scope
Non-goals
Business invariants
Failure modes
Acceptance criteria
Required tests
Required evidence
Cost and cleanup
```

## 22.2 AI Task 输出

AI 必须报告：

```text
What changed
Why
Files changed
Tests added
Commands executed
Observed results
Failure paths verified
Evidence created
Known limitations
Risks introduced
Documentation updated
```

## 22.3 AI 禁止事项

AI 不得：

- 伪造运行结果；
- 用 Mock 代替全部真实集成；
- 把 At-least-once 宣称成 Exactly-once；
- 自动忽略失败测试；
- 为展示技术栈强拆服务；
- 自动批准高风险操作；
- 用重试掩盖未知业务结果；
- 把日志当作永久状态；
- 将敏感信息写入 Evidence；
- 把 LocalStack Evidence 表述成真实 AWS Evidence；
- 跳过 Azure Budget 和 Cleanup Gate；
- 自签 Release ACCEPT。

## 22.4 Fresh-context Review

每个 Milestone 至少一次新的 AI 上下文复核：

- 只提供 Vision；
- Current State；
- ADR；
- Code；
- Tests；
- Evidence；
- Risks。

开发 AI 的解释不作为独立验收。

---

# 23. 项目成功标准

Reliant R1 Core 成功必须证明：

1. 多租户 SaaS 业务链路可以真实运行；
2. R1 只有 Public API Host 和 Unified Worker Host 两个长期运行部署单元；
3. API、DB、Queue、Worker Handler 和 Provider 形成完整 Trace；
4. 重复请求和重复消息不会产生重复业务结果；
5. Worker Crash 后任务可以恢复；
6. Provider Unknown Outcome 可以通过 Reconciliation 解决；
7. 至少四个用户体验或正确性 SLI 可计算；
8. Error Budget Burn 能关联到 Deployment 或 Failure；
9. k6 能阻断明确的性能回归；
10. Database Migration 有独立 Gate；
11. Queue Backlog 和 Dead-letter 可诊断、恢复；
12. Azure PostgreSQL Backup / Restore 有 E3 实际证据；
13. Deployment Rollback 后指标和业务状态恢复；
14. 同一应用 Artifact 可用于 Azure 与 AWS-compatible Profile；
15. Azure 完成完整 E3 Production-like 故障、恢复和环境重建验证；
16. LocalStack Ultimate 完成 E2 AWS Terraform、SQS、IAM/STS、Secrets Manager、S3 和至少一个故障恢复验证；
17. LocalStack 与 Azure Evidence 被明确区分；
18. 六个 R1 必做 Failure Scenario 全部可重复；
19. 至少三个重复故障被转化为自动 Gate；
20. 所有高风险运维动作都有授权和审计；
21. Azure 100 美元额度受到 Budget、Expiry 和 Cleanup 控制；
22. README 和 Runbook 可由第三方复验；
23. 所有简历主张都有对应 Evidence Level；
24. 未完成的真实 AWS、跨云 Restore 等能力明确标记为 E4、R2 或 Known Limitation。

R1+ AWS Verified 额外要求：

- 完成一个短生命周期真实 AWS E4 Smoke；
- 验证真实 SQS、IAM Role 和最小应用业务链路；
- 收集 Evidence 后完整 Destroy；
- 才能使用“validated across Azure and AWS”的简历措辞。

# 24. 明确不做的内容

Reliant 不做：

- 通用 SRE Control Plane；
- 通用 Service Catalog；
- 通用 Incident Management 产品；
- 通用 Alert Manager；
- 通用 Risk Management 产品；
- 完整 FinOps；
- 完整 CMDB；
- 真实银行卡支付处理；
- 全自动 AI Root Cause Analysis；
- 无人审批的破坏性修复；
- Kubernetes 节点镜像生命周期；
- Kubernetes Cluster Platform；
- 大规模 Chaos Engineering 产品；
- 多区域 Active-Active；
- 零停机跨云 Failover；
- 企业级工单系统；
- 商业级前端；
- 为证明微服务而拆分无独立价值的服务。

Kubernetes 节点、镜像供应链和 AKS/EKS Node Pool Reliability 由 NodeFoundry 负责。

---

# 25. 最终展示材料

## 25.1 README

三分钟内说明：

- Reliant 是什么业务系统；
- 为什么它存在可靠性挑战；
- Azure、LocalStack AWS Profile 和可选 Real AWS Smoke 如何运行；
- 核心业务链路；
- 如何查看 Trace 和 SLO；
- 如何触发故障；
- 如何恢复；
- 如何清理环境。

## 25.2 Architecture Pack

- System Context；
- Deployable Components；
- Transaction Flow；
- Async Flow；
- Azure E3 Deployment；
- LocalStack Ultimate E2 AWS Path；
- Optional Real AWS E4 Smoke；
- Trust Boundaries；
- Telemetry Flow；
- Recovery Flow。

## 25.3 Reliability Evidence Pack

- SLO Dashboard；
- Error Budget；
- k6 Report；
- Deployment Regression；
- Database Saturation / Lock Contention；
- Queue Backlog；
- Worker Crash / Duplicate Redelivery；
- Provider Unknown Outcome；
- Migration Failure；
- Dead-letter Replay；
- Azure Backup / Restore；
- LocalStack AWS Integration Evidence；
- Optional Real AWS Smoke Evidence；
- Rollback；
- Incident Reports。

## 25.4 Demo Video

建议 10–15 分钟：

1. 创建 Tenant 和 Campaign；
2. 提交 Contribution；
3. 展示 API → DB → Queue → Worker → Provider Trace；
4. 查看正常 SLO；
5. 发布故障版本或注入 Provider Failure；
6. 观察 Burn Rate、Backlog 或错误；
7. 使用 Trace 和 Dashboard 定位；
8. 执行 Rollback、Replay 或 Reconciliation；
9. 验证业务和指标恢复；
10. 展示 Incident Report 和 Preventive Gate。

---

# 26. 与 Pushpay Senior SRE 的对应关系

| Pushpay 关注点 | Reliant 项目证据 |
| --- | --- |
| End-to-end Ownership | 从业务代码、数据库、队列、云部署到恢复 |
| Code and Infrastructure | .NET、Terraform、GitHub Actions、Azure、AWS |
| Cross-system Investigation | API、DB、Queue、Worker、Provider 和 Cloud Runtime |
| Ambiguous Problem Solving | Unknown Outcome、重复消息、锁竞争、Partial Failure |
| Reduce Operational Toil | Diagnostics、Replay、Reconciliation、Migration Verification |
| Improve Observability | OpenTelemetry、RED/USE、业务正确性指标 |
| Improve Resilience | Idempotency、Outbox/Inbox、Retry、Circuit Breaker、Backpressure |
| Safer Delivery | Immutable Artifact、Canary、k6、Migration Gate、Rollback |
| Remove Recurring Failures | Failure Experiment 转化为自动回归和发布门禁 |
| Security Awareness | Tenant Isolation、OIDC、Short-lived Identity、Audit |
| Communicate Trade-offs | ADR、SLO Policy、Runbook、Postmortem、Known Limitations |
| On-call Mindset | Actionable Alert、Incident Timeline、Recovery Evidence |

---

# 27. 最终项目定位

## GitHub 仓库

```text
reliant
```

## 简历项目标题

```text
Reliant | Multi-Cloud SaaS Reliability Engineering System
```

## R1 Core 简历描述

完成 Azure E3 + LocalStack Ultimate E2，但尚未完成真实 AWS E4 时：

> Designed, built and operated a multi-tenant SaaS system on Azure, with its AWS deployment and service-integration path validated through LocalStack Ultimate. Engineered end-to-end observability, SLOs, idempotent asynchronous processing, safe delivery and evidence-backed recovery across APIs, PostgreSQL, queues, workers and external providers.

## R1+ AWS Verified 简历描述

完成真实 AWS E4 Smoke 后：

> Designed, built and operated a multi-tenant SaaS system across Azure and AWS, engineering end-to-end observability, SLOs, idempotent asynchronous processing, safe delivery, failure recovery and evidence-backed reliability improvements across APIs, PostgreSQL, queues, workers and external providers.

## 更强调 SRE Ownership 的版本

> Owned the reliability of a distributed SaaS workload from application code and database behavior through queues, cloud infrastructure, deployments and recovery, converting repeat incidents into automated tests, release gates and operational tooling.

## 与 NodeFoundry 的组合

```text
Reliant
→ Production SaaS Reliability Ownership

NodeFoundry
→ Kubernetes Node and Infrastructure Supply Chain Reliability
```

两个项目共同展示：

> 能从业务请求、数据库、队列、Worker、Telemetry 和 SLO，一直深入到 Kubernetes 节点、操作系统、Container Runtime 和云基础设施供应链的 Senior SRE / Platform Engineer。

