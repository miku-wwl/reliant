# Reliant 学习入口

这组文档服务于 **Owner 自己重新学会 Reliant**，不是把 Git 提交或 CI 结果重新抄一遍。

项目证据可以证明代码被实现、测试或验证过；只有你能独立解释设计、预测故障结果、指出代码位置，并在必要时亲自运行验证，才算个人学习完成。

## 建议学习顺序

1. [Phase 0：工程基础与可靠性契约](phase-0/foundation-and-reliability-contracts.md)
2. [Phase 1：多租户业务核心与业务不变量](phase-1/multi-tenant-business-invariants.md)
3. [Phase 2 Gate 与实验](Reliant-Phase-2-Gate-Review-and-Learning-Checklist.md)
   - 按顺序复习 `learning/phase-2/exp1` 到 `exp12`
4. [Phase 2/3/3.1 Completion Audit](phase-2-3-3.1-completion-audit.md)
5. [Phase 3 Gate 与实验](Reliant-Phase-3-Gate-Review-and-Learning-Checklist.md)
   - 按顺序复习 `learning/phase-3/exp1` 到 `exp15`
6. [Phase 4 Observability Checklist](Reliant-Phase-4-Gate-Review-and-Learning-Checklist.md)
   - 目前是学习和实验计划，不代表 Phase 4 已经实现。

## 每个阶段的学习循环

```text
读目标和不变量
→ 读 ADR 和关键代码
→ 不看答案先预测结果
→ 运行测试或实验
→ 对照实际状态
→ 用自己的话写结论
→ 完成口头自测
```

不要只用“文档存在”“测试通过”给自己打勾。尤其是 Phase 2/3 的 Owner Knowledge Gate，必须由 Owner 自己完成。
