# Reliant - Environment Baseline

> Phase 0 Stage A - Discovery
> 验证时间：2026-07-31

## LocalStack Ultimate

端点：`http://localhost:4566`

| 服务 | 验证操作 | 状态 | 备注 |
| --- | --- | --- | --- |
| SQS | create-queue / send / receive / visibility-timeout / DLQ redrive | 完全可用 | Visibility Timeout 生效；maxReceiveCount=2 后消息进入 DLQ |
| IAM | create-role / put-role-policy / get-role-policy / delete-role | 完全可用 | Role 创建、Inline Policy 读写正常 |
| STS | get-caller-identity | 完全可用 | Account: 000000000000 |
| Secrets Manager | create-secret / get-secret-value / delete-secret | 完全可用 | 读写正常，支持 JSON secret string |
| S3 | mb / cp / ls / cat / rm / rb | 完全可用 | 读写、列表、删除全部正常 |
| ECS Fargate | create-cluster / register-task-definition / run-task (FARGATE) / describe-tasks / HTTP 访问 / stop-task / delete-cluster | 完全可用 | Fargate 任务达到 RUNNING，nginx 容器 HTTP 200 可访问 |
| CloudWatch Logs | create-log-group / create-log-stream / put-log-events / filter-log-events | 完全可用 | 写入和 filter 查询正常 |
| RDS PostgreSQL | create-db-subnet-group / create-db-instance / describe / psql 连接 / delete | 完全可用 | DB Instance 从 creating 到 available 约需 30 秒；psql 连接成功，PostgreSQL 18.4 |
| EC2 | describe-vpcs / describe-subnets / describe-security-groups | 完全可用 | 默认 VPC + 2 subnet (us-west-1a/1b) + 1 security group |

## Azure Student Subscription

| 字段 | 值 |
| --- | --- |
| 订阅名 | Azure for Students |
| Subscription ID | 7c73b89d-485e-43a9-8d66-b12b766d567f |
| Tenant ID | 96e2f052-4512-4d4c-b2c0-cd0d36ad6437 |
| 状态 | Enabled |
| 额度 | 100 USD |

已验证可用服务（从 usage 数据确认）：Container Apps、PostgreSQL、Service Bus、KeyVault、Storage、Container Registry、Log Analytics / Application Insights、Compute、Network。

现有资源：NetworkWatcherRG（australiaeast）。

## 预算策略

| 类别 | 额度分配 |
| --- | --- |
| 计划内实验（Phase 6-8） | 最多 70 USD |
| 失败重建 + Restore + 最终 Demo | 至少 30 USD |
| 预警阈值 | 达到 70 USD 时停止新云实验 |

成本控制措施：
- 所有资源带 Owner / Purpose / Expiry / Environment Tag
- 数据库和应用环境不长期空闲运行
- 每次实验有预估成本、开始时间和 Destroy 截止时间
- 每次实验结束执行 Terraform Destroy + 残留检查

## 待确认项

| 项目 | 何时确认 |
| --- | --- |
| Container Apps 实际可用 SKU 和配额 | Phase 6 |
| PostgreSQL 实际可用 SKU 和定价 | Phase 6 |
| Service Bus 实际可用 Tier | Phase 6 |
| 实际每次实验预估成本 | Phase 6 |
