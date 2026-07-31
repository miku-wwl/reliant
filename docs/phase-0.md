# Reliant - Phase 0 Packet

> Greenfield Foundation and Reliability Contracts

## Stage A: Discovery

- [x] 明确业务场景（outline 第1节）
- [x] 明确 R1 用户旅程（outline 第2节）
- [x] 确定支付模拟边界（outline 第1.3节）
- [x] 确定部署单元：Public API Host + Unified Worker Host（outline 第4节）
- [x] 确定 Profile：local / localstack-aws / azure-real / aws-real-smoke（outline 第11.2节）
- [x] 盘点 LocalStack Ultimate 实际可用服务（`docs/architecture/environment-baseline.md`）
- [x] 确定 Azure 100 美元额度预算策略（`docs/architecture/environment-baseline.md`）
- [x] 建立初始风险登记（`docs/risks/risk-register.md`）

## Stage B: Design

- [x] ADR-0001: System Architecture
- [x] ADR-0002: Business Invariants
- [x] ADR-0003: Deployment Boundaries
- [x] ADR-0004: Evidence and State Ownership
- [x] AI Pair Programming Protocol（phase-plan 第17节）
- [x] Test and Evidence Level Strategy（ADR-0004）
- [x] LocalStack Compatibility Policy（ADR-0003）
- [x] Azure 100 USD Budget Plan（environment-baseline.md）
- [x] Cost and Cleanup Policy（ADR-0003 + environment-baseline.md）

## Stage C: Build

- [x] 创建 Solution 和项目结构（`Reliant.slnx`，8 个项目）
- [x] PostgreSQL Docker Compose（`docker-compose.yml`）
- [x] CI 基线（Stage A 已完成）
- [x] Architecture Tests（5 条规则，全部通过；ADR-0001 规则 #3/#4/#5 延迟到有业务代码后实现）
- [x] 统一验证脚本（Stage A 已完成）
- [x] 基础 Terraform 目录（`terraform/localstack-aws/` + `terraform/azure/`）
- [x] 基础 CLI（`reliantctl` 骨架）
- [x] OpenAPI 基线

## Stage D: Verify

- [x] clean restore/build/test
- [x] Architecture Test 负向夹具（临时测试确认违规检测有效，已删除）
- [x] Terraform fmt/validate（两个 Profile 均通过）
- [x] LocalStack Health / Apply / Destroy
- [x] 防止 LocalStack Profile 误连真实 AWS（9 个端点全部指向 localhost:4566）
- [x] Secret Scan（无真实 secret）
- [x] 故意破坏 CI 验证阻断（临时测试失败已确认，已删除）
- [x] 本地环境完整启动和清理（PostgreSQL up/down 正常）

## Stage E: Review

- [x] Fresh-context 架构 Review（完成，发现 4 个问题已修复）
- [x] Owner 决定 ACCEPT（Phase 0 Gate 通过，2 项 PARTIAL 为策略已定义、自动化延后）

## Gate

- [x] 只有一套权威规划
- [x] 业务不变量已明确（ADR-0002，12 条）
- [x] Public API Host、Unified Worker Host 和独立 Migrator 边界明确
- [x] CI 与本地验证一致（`scripts/verify.ps1`）
- [x] Startup 不执行 Migration
- [x] 未实现能力没有提前声明（`docs/current-state.md`）
- [x] LocalStack Ultimate 可运行最小 AWS Terraform
- [x] 模拟与真实云使用独立 State
- [~] Evidence 目录支持 E1/E2/E3/E4 标签（规则在 ADR-0004 定义，目录到使用时创建）
- [~] Azure 100 美元额度有预算、预警、Expiry 和 Cleanup 边界（策略在 environment-baseline.md 定义，自动化执行延到 Phase 6）

## Open Risks

- RISK-001: Azure 100 美元额度耗尽
- RISK-003: 项目范围膨胀
- RISK-004: 文档描述超过实际代码能力
- RISK-007: 本地开发环境与 CI 不一致（Mitigated）
