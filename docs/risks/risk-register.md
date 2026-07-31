# Reliant - Risk Register

> 最后更新：Phase 0 Stage A（Discovery）

## RISK-001: Azure 100 美元额度耗尽

- **Severity**: High | **Likelihood**: Medium
- **Impact**: 无法完成 E3 Production-like 验证和 Backup/Restore 演练
- **Mitigation**: 计划内实验不超过 70% 额度；保留 30% 用于失败重建和最终 Demo；每次实验后 Terraform Destroy；所有资源带 Expiry Tag；设置预算预警
- **Due**: 持续 | **Status**: Open

## RISK-002: LocalStack Ultimate 服务行为与真实 AWS 不一致

- **Severity**: Medium | **Likelihood**: High
- **Impact**: E2 验证通过但 E4 真实 AWS 失败；简历措辞超出实际证据
- **Mitigation**: 每项 LocalStack 服务必须通过最小实验确认行为；E2 Evidence 不得表述为真实 AWS Evidence；完成 E4 后才能声称 "validated across Azure and AWS"
- **Due**: Phase 7 Gate | **Status**: Open

## RISK-003: 项目范围膨胀

- **Severity**: High | **Likelihood**: High
- **Impact**: R1 无法在 12 周 + 2 周缓冲内完成
- **Mitigation**: 严格遵守 Phase Gate；R1 只有 2 个长期运行部署单元；不做商业级 UI；不做通用 SRE 平台；不做第三个云
- **Due**: 持续 | **Status**: Open

## RISK-004: 文档描述超过实际代码能力

- **Severity**: High | **Likelihood**: High
- **Impact**: 简历陈述无法通过面试深挖
- **Mitigation**: current-state.md 作为唯一能力真值源；未实现能力不得提前声明；所有简历主张标明 Evidence Level
- **Due**: 持续 | **Status**: Open

## RISK-005: 消息重复产生重复业务结果

- **Severity**: Critical | **Likelihood**: Medium
- **Impact**: 违反业务不变量；数据正确性问题
- **Mitigation**: Outbox/Inbox 模式；Idempotency Key；Consumer 幂等；Worker Crash + Duplicate Redelivery 场景验证
- **Due**: Phase 2 Gate | **Status**: Open

## RISK-006: Provider Unknown Outcome 导致重复创建

- **Severity**: Critical | **Likelihood**: Medium
- **Impact**: Provider 已处理但响应丢失，盲目重试导致重复业务结果
- **Mitigation**: Provider Idempotency Key；Unknown State；ProcessingAttempt 记录；Reconciliation 解决；不盲目重试
- **Due**: Phase 3 Gate | **Status**: Open

## RISK-007: 本地开发环境与 CI 不一致

- **Severity**: Medium | **Likelihood**: Low
- **Impact**: 本地通过但 CI 失败，或反之
- **Mitigation**: `scripts/verify.ps1` 作为唯一验证入口，GitHub Actions CI 调用同一脚本
- **Due**: Phase 0 Gate | **Status**: Mitigated
