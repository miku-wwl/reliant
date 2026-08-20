# Phase 4 Consolidated Evidence

> Evidence Level: E1（local process/PostgreSQL/Grafana）+
> E2（LocalStack SQS + real Worker host）
>
> Verified: 2026-08-20
>
> Status: **Completed locally — Phase 4 Gate PASS**

This evidence follows Phase 4 in
`reliant-phase-plan-v3.2-final.md` section 9. SLI/SLO, Error Budget and k6 are
Phase 5 and are deliberately excluded from this decision.

## 1. Delivered Architecture

```text
Reliant.Api / Reliant.Worker
  -> OpenTelemetry SDK
  -> OTLP Collector
  -> Prometheus + Tempo + Loki
  -> Grafana
```

Implemented boundaries:

- API and Worker JSON logs with W3C TraceId/SpanId and structured scopes;
- ASP.NET Core, HttpClient, Npgsql, runtime and custom Reliant telemetry;
- durable Outbox `TraceParent`/`TraceState` migration;
- trace/correlation/causation/deployment propagation through SQS attributes;
- producer, consumer, Outbox, Worker handler, Provider, reconciliation and
  notification delivery spans;
- bounded operational metrics and tenant-safe hashing;
- liveness, dependency-aware readiness and deployment version endpoints;
- fail-open bounded OTLP exporters;
- redacted `reliantctl diagnostics collect` snapshot;
- provisioned Collector, Prometheus, Tempo, Loki, Grafana, dashboard and
  component alerts with Owner/action/Runbook.

Architecture decision: `docs/adr/ADR-0023-telemetry-and-health.md`.

## 2. Automated Verification

### Phase 4 suite

```text
dotnet test tests/Reliant.Tests/Reliant.Tests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~Reliant.Tests.Integration.Phase4"

Total: 6; Passed: 6; Failed: 0
```

| Test | Evidence |
| --- | --- |
| Metric labels are bounded | Business IDs absent; arbitrary queue names normalize to logical roles |
| Redelivery trace semantics | New trace contains an ActivityLink to original producer context |
| Tenant-safe identifier | Stable hash does not expose organization GUID |
| API operational endpoints | `/health/live`, `/version`, correlation response |
| SQS propagation E2E | LocalStack round-trip of trace, correlation, causation and deployment context |
| Collector fail-open E2E | Unreachable OTLP endpoint still commits, processes, writes Inbox and ACKs; Provider effect = 1 |

### Regression and quality gates

```text
Release build:       0 warnings, 0 errors
Vulnerable packages: 0 known
dotnet format:       PASS
Phase 3 regression:  25 passed, 0 failed
Full regression:     169 passed, 0 failed, 0 skipped (5m40s)
```

Current discovered baseline after Phase 4:

| Category | Count |
| --- | ---: |
| Unit | 68 |
| Architecture | 5 |
| Integration | 96 |
| PostgreSQL-filtered integration | 86 |
| LocalStack-filtered integration | 37 |
| HTTP API-filtered integration | 10 |
| WorkerHost-filtered integration | 24 |
| Phase 4 | 6 |
| **Total unique category baseline** | **169** |

Dependency and Phase filters overlap and are not summed into the total.

## 3. Running-stack Evidence

`docker compose -f docker-compose.observability.yml config --quiet` passed.
After recreation, every endpoint returned HTTP 200:

| Component | Endpoint | Result |
| --- | --- | --- |
| Collector | `localhost:13133` | 200 |
| Prometheus | `localhost:9090/-/ready` | 200 |
| Tempo | `localhost:3200/ready` | 200 |
| Loki | `localhost:3100/ready` | 200 |
| Grafana | `localhost:3000/api/health` | 200 |

Grafana provisioning verification:

- data sources: Prometheus, Tempo and Loki;
- dashboard UID: `reliant-phase4-operations`;
- dashboard title: `Reliant · Phase 4 Operations`;
- 14 actionable panels;
- Prometheus rule group: `reliant-phase4-component-alerts`, five rules.

Live application export produced:

- Tempo traces for `Reliant.Worker`, including Npgsql and SQS HTTP spans;
- Loki resource labels including `service_name`, deployment environment and
  service instance;
- Prometheus series including `queue_depth`, `outbox_pending_count`,
  `http_server_request_duration_seconds_count` and
  `db_client_operation_duration_seconds_count`;
- `/health/live`, `/health/ready` and `/version` HTTP 200 for API and Worker.

`reliantctl diagnostics collect` connected to PostgreSQL and returned only
aggregate counts/ages plus deployment identity; no payload, secret or business
identifier was emitted.

## 4. Gate Decision

| Final v3.2 Phase 4 Gate | Result | Evidence |
| --- | --- | --- |
| End-to-end trace is complete | PASS | OTel instrumentation, durable Outbox context, SQS E2 test and Tempo search |
| API/DB/Queue/Worker handlers/Provider distinguishable | PASS | spans, metric dimensions and 14-panel dashboard |
| Telemetry failure does not block business | PASS | `TelemetryFailOpenE2ETests` |
| No secret or sensitive payload in logs | PASS | structured field policy, redacted CLI and bounded-label tests |
| Dashboard answers impact/time/location/change/recovery | PASS | API impact, DB, queue/outbox, Worker, Provider, correctness, deployment, traces and logs panels |
| Important alert candidates have Owner and action | PASS | five Prometheus rules linked to four Runbooks |

Decision: **PASS for local E1/E2 Phase 4 construction.** It does not claim an
Azure E3 deployment, real AWS E4 validation or production capacity.

## 5. Known Boundaries

- Notification delivery is still the existing sandbox delivery boundary; a
  real customer webhook transport remains a later business capability.
- LocalStack SQS exposes approximate queue depth but not the age of the oldest
  queued message through the SQS API. Local evidence uses processing delay;
  AWS deployments must ingest CloudWatch
  `ApproximateAgeOfOldestMessage` into the same metric contract.
- PostgreSQL pool and operation latency come from Npgsql. Production lock-wait
  and managed-database platform metrics must be supplied by the chosen cloud
  database integration.
- The local Grafana credential is development-only and must not be used in a
  deployed environment.
- Phase 5 still owns SLI/SLO, Error Budget, k6 and release-performance gates.

## 6. Navigation

- Current state: `docs/current-state.md`
- Telemetry flow: `docs/architecture/telemetry-flow.md`
- Telemetry ADR: `docs/adr/ADR-0023-telemetry-and-health.md`
- Local verification: `scripts/verify-phase4.ps1`
- Runbooks: `docs/runbooks/`
