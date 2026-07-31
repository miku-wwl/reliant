# ADR-0003: Deployment Boundaries

## Status

Proposed

## Context

Reliant 需要在 4 种环境（Profile）下运行同一套应用代码，每种环境的目的和可信度不同。outline 第11.2节定义了 4 个 Profile，第11.3节定义了 State 隔离要求。

核心问题：如何确保同一份代码在不同环境下运行，同时防止模拟环境误连真实云。

## Decision

### 1. 四个 Profile 的定义和边界

| Profile | 目的 | 证据级别 | 可信度 |
| --- | --- | --- | --- |
| `local` | 日常开发，快速迭代 | E1 | 最低，只验证代码行为 |
| `localstack-aws` | AWS 服务集成验证 | E2 | 中，API 兼容但非真实云 |
| `azure-real` | Production-like 真实验证 | E3 | 高，真实云行为 |
| `aws-real-smoke` | 真实 AWS 信心验证 | E4 | 最高，但短生命周期 |

### 2. 每个 Profile 的技术配置

#### local
- Docker Compose 启动 PostgreSQL
- 本地 Provider Simulator（模拟外部支付 Provider）
- 本地 OTel Collector + Grafana
- 不需要 Terraform
- 应用配置：`appsettings.local.json`

#### localstack-aws
- LocalStack Ultimate 提供所有 AWS 服务
- Terraform 使用 `tflocal` 或自定义 Provider Endpoint
- 独立 State 文件（本地）
- 禁止连接真实 AWS（见下方保护措施）
- 自动 Health Check：启动后验证 LocalStack 可用
- 自动 Reset / Cleanup：测试后清理所有资源
- 应用配置：`appsettings.localstack-aws.json`

#### azure-real
- Azure 学生订阅
- GitHub Actions OIDC 认证（无长期 secret）
- Terraform Remote State（加密 + Locking）
- Budget Guard：预算预警，达到 70 USD 停止新实验
- Expiry Tag：所有资源必须标记过期时间
- Destroy Verification：实验结束必须 Destroy + 残留检查
- 应用配置：`appsettings.azure-real.json`

#### aws-real-smoke
- 默认禁用，需要显式启用
- 明确 Account / Region
- 独立审批
- 严格成本上限
- 短生命周期，完成后立即 Destroy
- 独立临时 State
- 仅用于 E4 Evidence

### 3. 防止误连真实 AWS

这是最关键的安全边界。保护措施：

- `localstack-aws` Profile 的 Terraform 配置强制使用 `http://localhost:4566` 端点
- Terraform State 文件物理隔离：`localstack-aws` 用本地文件，`aws-real-smoke` 用独立远程 State
- `aws-real-smoke` Profile 默认禁用，需要环境变量 `RELIANT_AWS_SMOKE_ENABLED=true` 才能执行
- CI 中 `aws-real-smoke` 不自动触发，只手动触发
- Account Guard：脚本检查当前 AWS Account ID，如果是真实 Account 但 Profile 是 `localstack-aws`，拒绝执行

### 4. Terraform State 隔离

| Profile | State 位置 | 加密 | Locking |
| --- | --- | --- | --- |
| local | 不使用 Terraform | - | - |
| localstack-aws | 本地文件（`terraform.tfstate.localstack`） | 否 | 否 |
| azure-real | Azure Storage（远程） | 是 | 是 |
| aws-real-smoke | 独立临时文件 | 否 | 否 |

State 规则：
- 不同 Profile 永远不共享 State
- State 不输出 Secret
- azure-real State 定期 Backup
- Drift Detection：azure-real 定期检查 State 与实际资源是否一致

### 5. Terraform 模块策略

- 只在第二个真实使用方出现后提取公共模块
- Azure 和 AWS 各自独立的 Environment Module
- 共享 Naming / Tagging Convention（通过变量，不是超大模块）
- 禁止在业务代码中散落 `if localstack` 判断
- 禁止让 LocalStack Profile 能无提示连接真实 AWS

### 6. 环境生命周期

每个 Profile 必须支持完整生命周期：

```
Plan -> Apply -> Health Check -> Verify -> Exercise -> Collect Evidence -> Destroy / Reset -> Verify Cleanup
```

- `local`：Docker Compose up/down
- `localstack-aws`：Terraform apply/destroy + LocalStack reset
- `azure-real`：Terraform apply/destroy + 残留资源检查
- `aws-real-smoke`：Terraform apply/destroy + 成本确认

## Consequences

- 4 个 Profile 的配置通过 `appsettings.{profile}.json` 区分，代码不写 `if` 判断
- 防误连机制增加了一些脚本复杂度，但保护了安全和成本
- azure-real 是唯一使用 Remote State 的 Profile，其他都用本地 State
- aws-real-smoke 的门槛最高，需要多个条件才能触发
