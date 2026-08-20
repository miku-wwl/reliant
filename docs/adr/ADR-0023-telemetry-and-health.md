# ADR-0023: Telemetry Architecture and Health Contract

## Status

Accepted — Phase 4

## Context

Reliant already had durable business evidence in PostgreSQL, but an operator
could not follow one request across HTTP, Outbox, SQS, Worker and Provider or
separate a business failure from missing telemetry. Phase 4 requires a
diagnostic system without making telemetry part of the correctness path.

## Decision

### 1. Pipeline

```text
Reliant.Api / Reliant.Worker
  -> OpenTelemetry SDK (bounded batch queues)
  -> OTLP Collector
  -> Prometheus (metrics)
  -> Tempo (traces)
  -> Loki (logs)
  -> Grafana
```

The application uses one `ActivitySource` and one `Meter` contract from the
Application layer. Infrastructure configures ASP.NET Core, HttpClient, Npgsql,
runtime and Reliant instrumentation. An OTLP exporter is enabled only when
`Telemetry:OtlpEndpoint` or `OTEL_EXPORTER_OTLP_ENDPOINT` is set.

### 2. Trace propagation

- HTTP uses W3C `traceparent` and `tracestate`.
- Contribution creation persists the active trace context in the Outbox row.
- The Outbox publisher sends trace context, correlation, causation and producer
  deployment version as SQS message attributes.
- The first delivery uses the producer context as its remote parent.
- A redelivery starts a new consumer trace and links to the producer context.
  This prevents one trace from growing forever while preserving the causal
  relationship.
- Logical message ID and SQS physical message ID are separate fields.

The database remains the authoritative business-state evidence. A trace is a
diagnostic view and may be sampled or expire.

### 3. Logs and sensitive-data boundary

Console logs are JSON and OpenTelemetry logs retain structured state, scopes,
TraceId and SpanId. Operational scopes may contain correlation ID, causation
ID, message ID, error category and a tenant-safe ID. Deployment version,
commit, environment and instance are resource attributes.

Never emit access tokens, secrets, card data, raw provider/callback payloads,
full personal information or database connection strings. Metric labels never
contain tenant, contribution, message, attempt, job, provider reference or
idempotency identifiers. `TenantSafeId` is a stable truncated SHA-256 value;
the tenant GUID is not exposed.

### 4. Metrics

Metric names use lower-case snake case and base units such as seconds. Labels
are bounded operational dimensions: handler, queue role, result, operation,
error category, circuit state and provider name.

The contract covers:

- API rate limiting plus ASP.NET Core RED metrics;
- Npgsql connection-pool and operation metrics/traces;
- queue publish, receive, delete, redelivery, depth and processing delay;
- Outbox, retry, dead-letter and reconciliation backlog/age;
- Worker duration, result, in-flight, lease and visibility heartbeat;
- Provider duration, error class, timeout, unknown outcome and circuit state;
- callback, deduplication and reconciliation correctness events.

The standard SQS API exposes approximate depth but not the age of the oldest
still-queued message. The `queue_oldest_message_age` contract accepts that
signal, while local E2 evidence uses receive-time `queue_processing_delay`.
Production AWS must source oldest age from the `AWS/SQS`
`ApproximateAgeOfOldestMessage` CloudWatch metric.

### 5. Failure behavior

Telemetry is fail-open:

- exporters use bounded batch queues;
- export timeout is three seconds;
- Collector/export failure never rolls back a database transaction, prevents
  SQS ACK or changes a Contribution state;
- runtime metric collection failure only writes a warning;
- no synchronous exporter is allowed in a request or message-processing path.

Missing telemetry is itself visible through Collector health, scrape status
and exporter queue metrics.

### 6. Health and deployment identity

- `/health/live` answers whether the process is alive and never checks remote
  dependencies.
- API `/health/ready` checks PostgreSQL, its synchronous correctness boundary.
- Worker `/health/ready` checks PostgreSQL and whether SQS has completed a
  successful operation since its latest failure.
- `/version` returns service name, version, environment, commit and instance.
- Worker hosts Kestrel only for operational endpoints; business processing
  remains in hosted services and retains graceful shutdown behavior.

## Consequences

- Operators can move from a dashboard to correlated logs and traces without
  putting high-cardinality business IDs in Prometheus.
- Collector loss reduces diagnostics but does not reduce correctness.
- Local dashboards prove component diagnosis, not production capacity or SLO
  compliance. SLI/SLO, Error Budget and k6 remain Phase 5 work.

