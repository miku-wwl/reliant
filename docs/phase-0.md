# Reliant - Phase 0 Packet

> Greenfield Foundation and Reliability Contracts

## Stage A: Discovery

- [x] 明确业务场景（outline 第1节）
- [x] 明确 R1 用户旅程（outline 第2节）
- [x] 确定支付模拟边界（outline 第1.3节）
- [x] 确定部署单元：Public API Host + Unified Worker Host（outline 第4节）
- [x] 确定 Profile：local / localstack-aws / azure-real / aws-real-smoke（outline 第11.2节）
- [x] 盘点 LocalStack Ultimate 实际可用服务
- [x] 确定 Azure 100 美元额度预算策略
- [x] 建立初始风险登记

## Stage B: Design

- [ ] ADR-0001: System Architecture
- [ ] ADR-0002: Business Invariants
- [ ] ADR-0003: Deployment Boundaries
- [ ] ADR-0004: Evidence and State Ownership
- [ ] AI Pair Programming Protocol
- [ ] Test and Evidence Level Strategy
- [ ] LocalStack Compatibility Policy
- [ ] Azure 100 USD Budget Plan
- [ ] Cost and Cleanup Policy

## Stage C: Build

- [ ] 创建 Solution 和项目结构
- [ ] PostgreSQL + Docker Compose
- [ ] LocalStack Ultimate Profile
- [ ] CI 基线
- [ ] Architecture Tests
- [ ] 统一验证脚本
- [ ] Evidence Schema
- [ ] 基础 Terraform 目录
- [ ] 基础 CLI
- [ ] OpenAPI 基线

## Stage D: Verify

- [ ] clean restore/build/test
- [ ] Architecture Test 负向夹具
- [ ] Migration 空库和重复执行
- [ ] Terraform fmt/validate
- [ ] LocalStack Health / Apply / Reset
- [ ] 防止 LocalStack Profile 误连真实 AWS
- [ ] Azure Budget Guard 配置验证
- [ ] Secret Scan / Dependency Scan / Container Build
- [ ] 故意破坏 CI 验证阻断
- [ ] 本地环境完整启动和清理

## Stage E: Review

- [ ] Fresh-context 架构 Review
- [ ] Owner 决定 ACCEPT / VALIDATION / BLOCKED

## Gate

- [ ] 只有一套权威规划
- [ ] 业务不变量已明确
- [ ] Public API Host、Unified Worker Host 和独立 Migrator 边界明确
- [ ] CI 与本地验证一致
- [ ] Startup 不执行 Migration
- [ ] 未实现能力没有提前声明
- [ ] Evidence 目录支持 E1/E2/E3/E4 标签
- [ ] LocalStack Ultimate 可运行最小 AWS Terraform
- [ ] Azure 100 美元额度有预算、预警、Expiry 和 Cleanup 边界
- [ ] 模拟与真实云使用独立 State

## Open Risks

- RISK-001: Azure 100 美元额度耗尽
- RISK-003: 项目范围膨胀
- RISK-004: 文档描述超过实际代码能力
- RISK-007: 本地开发环境与 CI 不一致
