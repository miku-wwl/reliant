# Phase 3.1 Evidence - Reliability Gate Closure

> Evidence Level: E1 (Testcontainers + PostgreSQL)

## Test Summary

| Test Category | Count | Status |
| --- | --- | --- |
| Unit Tests | 58 | All Passed |
| Architecture Tests | 5 | All Passed |
| Integration Tests | 7 | All Passed |
| **Total** | **70** | **All Passed** |

## Provider Idempotency

### SameContribution_ShouldProduceSingleProviderOperation

- 场景：同一 Contribution 两次 Submit
- 预期：Provider 只产生一个 Operation
- 实际：ProviderOperationCount == 1，两次返回相同 Reference
- 结果：PASS
- 测试：`Integration/ProviderIdempotencyTests.cs`

### AttemptPersisted_BeforeProviderCall

- 场景：Submit 后检查 Attempt 在 Provider 调用前已持久化
- 预期：Attempt 存在，Status=Succeeded，包含 IdempotencyKey
- 实际：PASS

## Unknown Outcome

### ProcessedButResponseLost_ShouldConvergeToSucceeded_WithoutSecondProviderEffect

- 场景：Provider 处理成功但响应丢失（Timeout）
- 预期：Attempt Status=Unknown，Provider OperationCount=1
- 实际：PASS

### ProviderNotFound_OnReconciliation_ShouldTransitionToRetryPending

- 场景：Provider 超时未处理，Reconciliation 查询返回 NotFound
- 预期：可以安全重试
- 实际：PASS

## Database Constraints

### UniqueIndex_ShouldPreventDuplicateIdempotencyKey

- 场景：插入相同 IdempotencyKey
- 预期：DbUpdateException
- 实际：PASS

### TenantFilter_ShouldIsolateData

- 场景：Org A 的 Campaign 对 Org B 不可见
- 预期：Org B 查询返回空
- 实际：PASS

### OptimisticConcurrency_ShouldPreventLostUpdate

- 场景：两个 DbContext 同时更新同一 Organization
- 预期：第二个抛 DbUpdateConcurrencyException
- 实际：PASS

## Known Limitations

- Integration Tests 使用 Testcontainers PostgreSQL，CI 需要支持 Docker
- LocalStack SQS Integration Tests 尚未实现（Phase 7 深入验证）
- Callback 处理的 Integration Tests 尚未实现（需 API 端到端测试）
- Retry Scheduling 的 Integration Tests 尚未实现（需 Worker 端到端测试）
- SandboxProvider 是 Singleton，跨测试共享状态（通过 EnsureDeleted 重置）
