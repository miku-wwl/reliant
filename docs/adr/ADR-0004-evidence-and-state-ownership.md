# ADR-0004: Evidence and State Ownership

## Status

Proposed

## Context

Reliant 的简历陈述必须有证据支撑。outline 第10.2节定义了四级证据模型（E1-E4），第10.7节定义了简历声明规则。本 ADR 说明证据怎么标记、怎么存储、谁来拥有，以及 Terraform State 的所有权和隔离。

## Decision

### 1. 四级证据模型

| Level | 环境 | 可以证明 | 不能单独证明 |
| --- | --- | --- | --- |
| E1 | Unit / Testcontainers | 代码、数据库和局部组件行为 | 云服务语义 |
| E2 | LocalStack Ultimate | AWS API、Terraform、服务集成、SQS 语义、故障注入 | 真实 AWS 性能、配额、网络 |
| E3 | Real Azure | Production-like 部署、身份、网络、数据库、Backup/Restore | 真实 AWS 行为 |
| E4 | Real AWS Smoke | 真实 AWS 控制面和数据面 | 完整生产规模和长期运行 |

### 2. Evidence 存储规则

- 所有 Evidence 存储在 `docs/evidence/` 目录下
- 子目录按类型分：`deployments/`、`failures/`、`recovery/`、`load-tests/`
- 每份 Evidence 文件名格式：`{phase}-{scenario}-{level}.md`
  - 例：`phase8-deployment-regression-e3.md`
- 每份 Evidence 必须包含：
  - Evidence Level（E1/E2/E3/E4）
  - 执行时间
  - 执行环境
  - 执行命令
  - 结果截图或输出
  - 结论

### 3. 简历声明规则

| 完成的 Evidence Level | 允许的简历措辞 |
| --- | --- |
| E2 + E3（无 E4） | Built and operated on Azure, with AWS deployment path validated through LocalStack Ultimate |
| E2 + E3 + E4 | Deployed and validated across Azure and AWS |

- 不得把 E2 LocalStack Evidence 表述为真实 AWS Evidence
- 不得把 E1 Unit Test 表述为集成验证
- 未完成的 Level 必须标记为 Known Limitation

### 4. Terraform State 所有权

| Profile | State 位置 | 拥有者 | 生命周期 |
| --- | --- | --- | --- |
| local | 不使用 Terraform | - | - |
| localstack-aws | 本地文件 | 开发者 | 可随时删除重建 |
| azure-real | Azure Storage Account | 项目 | 持久化，定期 Backup |
| aws-real-smoke | 临时文件 | 开发者 | 用完即删 |

State 规则：
- 不同 Profile 永远不共享 State
- State 不输出 Secret（`sensitive = true`）
- azure-real State 启用 Encryption 和 Locking
- Drift Detection：azure-real 定期 `terraform plan -detailed-exitcode` 检查漂移

### 5. Evidence 不可用作永久状态

- Evidence 是某次执行的快照，不是系统当前状态
- `docs/current-state.md` 是能力真值源
- Evidence 过期后不自动删除，但标注 "Historical"
- 临时控制台输出和聊天记录不属于 Evidence

## Consequences

- Evidence Schema 在 Stage C 定义为模板，后续 Phase 填充
- 简历措辞严格受限于完成的 Evidence Level
- Terraform State 隔离与 ADR-0003 一致
- azure-real State 是唯一需要持久化和 Backup 的 State
