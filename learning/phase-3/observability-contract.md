# Phase 3 Observability Contract

> 状态：Contract Complete；Runtime Instrumentation 属于 Phase 4。
> 目的：先回答每个信号对应哪个故障，再决定如何用 OpenTelemetry、日志、Metric
> Backend 和 Dashboard 实现。

## 1. 设计规则

- Counter 只递增；Gauge 表达当前积压；Histogram 表达延迟和年龄。
- ContributionId、MessageId、AttemptId 等高基数字段只能进入 Trace/Log，不能做
  Metric label。
- Metric label 只允许 provider、result、error_category、queue、handler、
  circuit_state、resolution 等有限枚举。
- Tenant 维度默认不进入全局 Metric；需要租户视图时通过受控日志或独立聚合实现。
- 每个告警必须能回指至少一个 Phase 2/3 故障实验和一份 Runbook。

## 2. Unknown Outcome / Reconciliation

| Signal | 类型 | 定义 | 对应实验 | 主要故障 |
| --- | --- | --- | --- | --- |
| provider_unknown_total | Counter | Provider 结果被持久化为 Unknown 的次数 | P3 Exp2/3/6 | Timeout、Response Lost、Crash |
| provider_unknown_rate | Derived | unknown / provider requests | P3 Exp2/3/14 | Provider 退化 |
| reconciliation_pending_count | Gauge | 未解决 ReconciliationRecord 数 | P3 Exp2/3/10 | 收敛积压 |
| reconciliation_oldest_age | Gauge | 最老未解决记录年龄 | P3 Exp3/14 | 长时间不收敛 |
| reconciliation_resolution_total | Counter | 按 resolution 分类的收敛次数 | P3 Exp2/3/10 | 决策分布异常 |
| reconciliation_manual_required_total | Counter | 进入 ManualRequired 的次数 | P3 Exp12 | 终态冲突 |

建议告警意图：

- unknown rate 持续升高：检查 Provider latency/timeout/circuit；
- oldest age 超过业务 RTO：检查 Reconciliation Worker 和 Provider Query；
- ManualRequired 突增：检查 Callback 与 Submit response 的冲突。

## 3. Provider

| Signal | 类型 | 最小标签 | 对应实验 |
| --- | --- | --- | --- |
| provider_request_total | Counter | provider, operation, result | P3 Exp1–6/14 |
| provider_request_duration | Histogram | provider, operation | P3 Exp1/2/3/14 |
| provider_error_total | Counter | provider, error_category | P3 Exp2/3/13/14 |
| provider_timeout_total | Counter | provider, operation | P3 Exp2/3/6 |
| provider_idempotency_conflict_total | Counter | provider | P3 Exp5/6 |
| provider_duplicate_effect_detected_total | Counter | provider | P3 Exp3–6；正常值应为 0 |

Provider Operation Count 是实验断言，也是生产审计查询；不能只依赖进程内 Counter
证明“最多一个外部副作用”。

## 4. Callback

| Signal | 类型 | 最小标签 | 对应实验 |
| --- | --- | --- | --- |
| callback_received_total | Counter | provider, result | P3 Exp7–9 |
| callback_invalid_signature_total | Counter | provider | P3 Exp7 |
| callback_invalid_timestamp_total | Counter | provider, reason | P3 Exp7 |
| callback_duplicate_total | Counter | provider | P3 Exp8 |
| callback_orphan_total | Counter | provider | P3 Exp7/9 |
| callback_terminal_conflict_total | Counter | provider | P3 Exp12 |
| callback_processing_duration | Histogram | provider, result | P3 Exp7–9 |

安全信号不得包含签名、Secret 或完整 Payload。

## 5. Retry / Dead-letter

| Signal | 类型 | 最小标签 | 对应实验 |
| --- | --- | --- | --- |
| retry_pending_count | Gauge | provider, error_category | P2 Exp7；P3 Exp13/14 |
| retry_scheduled_total | Counter | provider, error_category | P3 Exp2/13 |
| retry_exhausted_total | Counter | provider, error_category | P2 Exp7；P3 Exp13 |
| retry_oldest_age | Gauge | provider | P3 Exp14 |
| deadletter_pending_count | Gauge | message_type, error_category | P2 Exp6/7 |
| deadletter_replay_total | Counter | message_type, result | P2 Exp6 Replay 补全 |

Replay Metric 只记录结果和类型；Operator、DeadLetterId、ReplayMessageId 放入
AuditEvent/Trace。

## 6. Circuit / Queue / Worker

| Signal | 类型 | 最小标签 | 对应实验 |
| --- | --- | --- | --- |
| circuit_state | Gauge | provider, state | P3 Exp11/14 |
| circuit_transition_total | Counter | provider, from, to | P3 Exp11/14 |
| circuit_half_open_probe_total | Counter | provider, result | P3 Exp11 |
| queue_depth | Gauge | queue | P2 Exp10；P3 Exp14 |
| queue_oldest_message_age | Gauge | queue | P2 Exp10；P3 Exp14 |
| queue_receive_total | Counter | queue | P2 Exp3/4/12 |
| queue_delete_total | Counter | queue, result | P2 Exp4/9/12 |
| queue_redelivery_total | Counter | queue | P2 Exp3/4/12；P3 Exp4/6 |
| worker_inflight | Gauge | handler | P2 Exp9/10 |
| queue_drain_rate | Derived | queue | P2 Exp10；P3 Exp14 |
| lease_heartbeat_failure_total | Counter | handler, reason | P2 Exp12 |
| visibility_renewal_failure_total | Counter | queue, reason | P2 Exp12 |

## 7. Trace 关联字段

| 字段 | 创建点 | 传播/持久化点 | 用途 |
| --- | --- | --- | --- |
| CorrelationId | API request / fallback MessageId | Outbox、DeadLetter、Audit | 一次业务链路 |
| CausationId | 产生后续消息的上游 MessageId | Outbox、DeadLetter | 因果链 |
| OutboxMessageId | Outbox insert | SQS logical MessageId | DB 到 Queue |
| SqsPhysicalMessageId | Broker | Receive log/trace | Broker 投递诊断 |
| ContributionId | API | Payload、DB、Provider key | 业务聚合 |
| JobRunId | Outbox/Message | JobAttempt、Lease、Checkpoint | Worker 执行 |
| ProcessingAttemptId | Provider 前持久化 | Provider span、DB | 外部调用证据 |
| ProviderIdempotencyKey | Stable key factory | Attempt、Provider span | 副作用去重 |
| ProviderReference | Provider response/callback | DB、Reconciliation | 外部结果 |
| CallbackEventId | Provider callback | Inbox/Orphan/Audit | Callback dedup |
| ReconciliationRecordId | Reconcile command | DB、trace | 收敛决策 |
| ReplayMessageId | Dead-letter Replay | DeadLetter、Outbox、Audit | 人工恢复链 |

## 8. Phase 4 验收入口

Phase 4 实现时必须：

1. 为上述信号建立代码 instrumentation；
2. 用对应实验触发信号并保存查询截图/导出；
3. 验证 label cardinality；
4. 建立 Dashboard、告警和 Runbook；
5. 定义 SLI/SLO 和 Error Budget；
6. 不把日志文本搜索当成唯一 Metric；
7. 不把 Sandbox/LocalStack 结果冒充 E3/E4。
