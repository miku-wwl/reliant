# Phase 0：工程基础与可靠性契约

> 目的：从头恢复 Reliant 的工程上下文，理解它为什么这样组织，而不是只记住项目文件名。

## 1. Phase 0 要解决什么问题

Phase 0 不是“先建一个空项目”。它要在写业务代码之前固定几件会影响整个项目的事情：

1. 项目的业务边界和 R1 用户旅程是什么；
2. 哪些进程长期运行，哪些进程只负责一次性任务；
3. 本地、LocalStack、Azure 和真实 AWS 分别能证明什么；
4. 哪些可靠性要求必须变成自动化检查；
5. 哪些能力还没有实现，不能因为出现在计划里就提前宣称完成。

Phase 0 的核心思想是：

```text
先定义边界和不变量
→ 再定义证据等级和验证门禁
→ 再开始实现业务
```

## 2. 必读材料

按下面顺序阅读：

1. [主 Phase 计划：原则、交付模型、Phase 0](../../reliant-phase-plan-v3.2-final.md)
2. [Phase 0 Packet](../../docs/phase-0.md)
3. [ADR-0001：System Architecture](../../docs/adr/ADR-0001-system-architecture.md)
4. [ADR-0002：Business Invariants](../../docs/adr/ADR-0002-business-invariants.md)
5. [ADR-0003：Deployment Boundaries](../../docs/adr/ADR-0003-deployment-boundaries.md)
6. [ADR-0004：Evidence and State Ownership](../../docs/adr/ADR-0004-evidence-and-state-ownership.md)
7. [Environment Baseline](../../docs/architecture/environment-baseline.md)
8. [Risk Register](../../docs/risks/risk-register.md)

阅读目标不是背 ADR，而是回答：

> 如果删掉这个决定，后面哪一类故障、误连、错误声明或维护成本会重新出现？

## 3. 核心知识

### 3.1 可靠性不等于 HTTP 200

Reliant 的成功不是“API 返回了 200”，而是业务结果最终正确，并且在重复请求、进程崩溃、队列重复投递、外部系统不确定等情况下仍然可恢复。

因此项目从一开始就把下面几件事分开：

- 业务代码是否实现；
- 自动化测试是否通过；
- 实验是否真实运行；
- 证据属于 E1、E2、E3 还是 E4；
- 当前能力是否已经达到 Gate。

这也是为什么项目不能把 Mock、聊天记录或一张截图当成完整可靠性证据。

### 3.2 Modular Monolith 与部署边界

当前项目不是一开始就拆成很多微服务，而是 Modular Monolith 加两个长期运行部署单元，以及一个独立的迁移宿主：

```text
Public API Host
  - HTTP 契约
  - 认证和租户上下文
  - 接受业务请求
  - 写入数据库和 Outbox

Unified Worker Host
  - Processing Handler
  - Reconciliation Handler
  - Notification / Webhook Handler

独立 Migrator Host（一次性任务）
  - 只负责数据库 Migration
  - 不在 API 或 Worker 启动时自动执行
```

这样做的理由：

- API 和 Worker 可以独立扩缩、重启和观察；
- Migration 不会因为应用启动而偷偷修改数据库；
- Handler 之间仍然保留清晰的故障隔离边界；
- 在真正需要独立扩缩、安全身份或发布周期之前，不提前承担微服务复杂度。

你需要区分两个概念：

- **代码模块边界**：Domain、Application、Infrastructure、API、Worker 等项目之间的依赖方向；
- **部署边界**：哪些代码以哪个进程运行、拥有哪种生命周期和权限。

### 3.3 Clean Architecture 的依赖方向

项目的基本方向是：

```text
API / Worker / Migrator
        ↓
   Application
        ↓
      Domain

Infrastructure 实现 Application 定义的接口
```

关键原则：

- Domain 不依赖数据库、HTTP、SQS 或具体云 SDK；
- Application 表达用例、端口和业务编排；
- Infrastructure 负责 EF Core、Provider、队列和外部系统实现；
- API、Worker、Migrator 是不同的宿主，不应把宿主生命周期逻辑塞进 Domain；
- Architecture Tests 用来防止依赖方向在后续提交中悄悄退化。

### 3.4 四个 Profile 与证据等级

| Profile | 用途 | 证据含义 |
| --- | --- | --- |
| `local` | 日常开发、PostgreSQL、快速测试 | 本地行为，不代表云兼容 |
| `localstack-aws` | AWS API、Terraform、SQS 等集成验证 | E2，本地 AWS-compatible 行为 |
| `azure-real` | 真实 Azure 生产类环境验证 | E3，真实 Azure 行为 |
| `aws-real-smoke` | 可选真实 AWS Smoke | E4，真实 AWS 的有限验证 |

最容易犯的错误是把 LocalStack 的通过写成“真实 AWS 已验证”。正确表述必须带上 Profile 和证据等级。

### 3.5 Terraform State 和环境生命周期

不同环境不能共享同一个 Terraform State，因为它们的资源、生命周期、权限和破坏半径不同：

- `localstack-aws` 可以使用本地临时 State；
- `azure-real` 使用需要加密、锁定和备份的远程 State；
- `aws-real-smoke` 使用独立临时 State；
- 实验资源必须有 Owner、Purpose、Expiry、Environment 标签；
- 实验结束必须 Destroy 并检查残留资源。

这不是“运维细节”，而是可靠性和成本边界的一部分。一个测试通过但留下云资源的系统，仍然可能是不合格的工程系统。

### 3.6 CI、验证脚本和防劣化门禁

Phase 0 建立了统一验证入口 `scripts/verify.ps1`，并把关键检查接入 CI。需要理解这些检查分别防什么问题：

- clean restore/build/test：防止本机缓存掩盖问题；
- Architecture Tests：防止依赖方向和租户边界退化；
- Terraform fmt/validate：防止基础设施配置本身失效；
- LocalStack Health / Apply / Destroy：防止环境只在文档里存在；
- Secret Scan：防止凭据进入仓库；
- Dependency Scan：防止已知漏洞静默进入构建；
- 故意破坏 CI：验证门禁真的能阻断，而不是只在绿色路径上运行；
- 本地完整启动和清理：验证生命周期闭环。

### 3.7 ADR、风险登记和 Fresh-context Review

ADR 不是“写完就结束的设计作文”。它应该记录：

- 当时面对的约束；
- 被选择的方案；
- 没有选择其他方案的原因；
- 这个决定带来的代价和后续影响。

风险登记则回答：

- 可能失败的是什么；
- 影响是什么；
- 当前缓解措施是什么；
- 哪个 Phase 或 Gate 负责关闭它。

Fresh-context Review 的价值是让一个没有参与实现过程的人重新检查边界、证据和声明，发现“作者已经习惯所以看不见”的问题。

## 4. 代码和证据地图

| 位置 | 学习重点 |
| --- | --- |
| `docs/adr/ADR-0001-system-architecture.md` | 宿主、分层、Provider 和租户边界 |
| `docs/adr/ADR-0002-business-invariants.md` | 12 条业务不变量及其 Phase 分配 |
| `docs/adr/ADR-0003-deployment-boundaries.md` | Profile、State 隔离、生命周期 |
| `docs/adr/ADR-0004-evidence-and-state-ownership.md` | E1-E4 和声明边界 |
| `docs/phase-0.md` | Phase 0 实际完成情况与 Partial 项 |
| `docs/architecture/environment-baseline.md` | LocalStack、Azure、预算和清理规则 |
| `docs/risks/risk-register.md` | 风险、缓解措施和未来 Gate |
| `.github/workflows/ci.yml` | CI 实际执行了什么 |
| `scripts/verify.ps1` | 本地验证入口和阻断条件 |
| `docker-compose.yml` | 本地长期运行依赖 |
| `terraform/localstack-aws/` | E2 基础设施边界 |
| `terraform/azure/` | E3 基础设施基线 |
| `tests/Reliant.Tests/Architecture/ArchitectureTests.cs` | 架构规则如何被自动检查 |

## 5. 从头学习任务

### 任务 A：画出部署图

不用看图，从记忆画出 API、Worker、Migrator、PostgreSQL、LocalStack/SQS 的关系，并在每条箭头旁写出通信方式和职责。

画完后对照 `docs/adr/ADR-0001-system-architecture.md` 修正。

### 任务 B：解释四个 Profile

对下面四句话判断是否正确，并说明原因：

1. LocalStack 测试通过，所以真实 AWS 一定通过；
2. Azure 和 LocalStack 可以共用 Terraform State；
3. 真实云实验结束后只需要停止 VM，不需要 Destroy；
4. E2 证据可以支持“已在真实 Azure 验证”的简历表述。

正确答案应该分别是：错误、错误、错误、错误。

### 任务 C：追踪一次 CI

从 `.github/workflows/ci.yml` 找到验证入口，再从 `scripts/verify.ps1` 找到至少五个实际检查。对每个检查写一句“它防止什么错误”。

### 任务 D：找出 Phase 0 的边界

阅读 [Phase 0 Packet](../../docs/phase-0.md)，区分：

- 已完成并验证的事项；
- 规则已经定义，但自动化延后的 Partial 事项；
- 明确留给 Phase 6/后续 Phase 的事项。

## 6. Owner 自测题

在没有打开 ADR 的情况下回答：

1. 为什么 API 和 Worker 不合并成一个长期运行职责？
2. 为什么 Migration 不应该在 API 启动时自动执行？
3. Modular Monolith 解决了什么问题，又没有解决什么问题？
4. Domain 层为什么不能直接依赖 SQS SDK？
5. `localstack-aws` 能证明什么，不能证明什么？
6. 为什么不同 Profile 不能共享 Terraform State？
7. 为什么云实验需要 Expiry 和 Cleanup Gate？
8. E1、E2、E3、E4 的可信度和边界分别是什么？
9. Architecture Test 和业务单元测试各自防什么退化？
10. 为什么“CI 是绿色”仍然不等于“所有项目能力已完成”？
11. Fresh-context Review 要找哪类问题？
12. 当前 Phase 0 Packet 中哪两项是策略已定义、自动化延后的 Partial？

## 7. Phase 0 学习完成标准

- [ ] 能不看文档画出 API、Worker、Migrator 和基础设施边界；
- [ ] 能解释四个 Profile 与 E1-E4 的关系；
- [ ] 能解释 Terraform State 隔离和云资源清理的原因；
- [ ] 能指出 `scripts/verify.ps1` 和 CI 的入口；
- [ ] 能说明 Domain、Application、Infrastructure、Host 的依赖方向；
- [ ] 能解释 ADR、Risk Register、Evidence 和 Gate 的区别；
- [ ] 能回答全部 Owner 自测题；
- [ ] 能说出 Phase 0 当前的两个 Partial，而不是笼统地说“Phase 0 全部完成”。

完成这些后，再进入 Phase 1。不要把 Phase 0 的设计词汇直接跳过，否则后面看到 Outbox、Worker、Provider 和云环境时，容易只记实现，不理解边界。
