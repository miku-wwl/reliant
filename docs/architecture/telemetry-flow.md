# Reliant Telemetry Flow

## End-to-end path

```text
HTTP request
  -> API span + PostgreSQL span
  -> Contribution and Outbox commit (trace context persisted)
  -> Outbox publisher span
  -> SQS producer span + message attributes
  -> SQS processing consumer span
  -> Processing handler + PostgreSQL + Provider client span
  -> result and notification Outbox commit
  -> SQS notification producer/consumer span
  -> notification delivery span
```

The same correlation ID describes the business flow. Causation ID identifies
which message or action created the next message. W3C trace context describes
one diagnostic execution path. On SQS redelivery, Reliant creates a new trace
with an `ActivityLink` to the original producer context.

## Signal ownership

| Question | Primary evidence |
| --- | --- |
| What is the durable business state? | PostgreSQL Contribution, Attempt, Outbox, Inbox and Reconciliation rows |
| Where is latency or failure occurring? | Tempo trace and component metrics |
| What happened inside one process? | Loki structured logs joined by TraceId/CorrelationId |
| Is work accumulating? | Queue, Outbox, retry, dead-letter and reconciliation gauges |
| Did a deployment change? | Service version/commit/environment resource attributes and `/version` |
| Is telemetry itself missing? | Collector health, Prometheus `up`, exporter queue metrics |

## Cardinality boundary

Metrics contain only bounded dimensions. Message ID, contribution ID,
correlation ID and tenant-safe ID may appear in trace/log context but not in
metric labels. Raw tenant IDs and sensitive payloads are excluded from all
three signals.

## Local topology

`docker-compose.observability.yml` starts Collector, Prometheus, Tempo, Loki
and Grafana. Applications export OTLP/gRPC to `http://localhost:4317`.
Prometheus scrapes Collector-exported application metrics and Collector
self-metrics. Grafana provisions all three data sources and the
`Reliant · Phase 4 Operations` dashboard.

