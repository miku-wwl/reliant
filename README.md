# Reliant

Multi-Cloud SaaS Reliability Engineering System

## Project Status

```text
Phase 4 — Completed locally (E1/E2)
```

Phase 3.1 remains CI-proven and the Phase 4 observability/operability gate is
locally verified with 169 tests. See
[`docs/current-state.md`](docs/current-state.md) and
[`docs/evidence/phase-4.md`](docs/evidence/phase-4.md).

Phase 4 architecture and learning material:
[`docs/adr/ADR-0023-telemetry-and-health.md`](docs/adr/ADR-0023-telemetry-and-health.md),
[`learning/Reliant-Phase-4-Gate-Review-and-Learning-Checklist.md`](learning/Reliant-Phase-4-Gate-Review-and-Learning-Checklist.md).

Owner learning path:
[`learning/README.md`](learning/README.md).

## Local Observability

```powershell
docker compose up -d postgres
docker compose -f docker-compose.observability.yml up -d

$env:Telemetry__OtlpEndpoint = "http://localhost:4317"
$env:Telemetry__OtlpProtocol = "grpc"
$env:Deployment__Version = "phase4-local"
$env:Deployment__Commit = (git rev-parse --short HEAD)

dotnet run --project src/Reliant.Migrator
dotnet run --project src/Reliant.Api
dotnet run --project src/Reliant.Worker
```

- Grafana: `http://localhost:3000` (`admin` / `reliant`, local only)
- API liveness/readiness/version: ports configured for the API host
- Worker liveness/readiness/version: `http://localhost:8081`
- Redacted snapshot: `dotnet run --project src/Reliant.Cli -- diagnostics collect`
- Phase 4 gate: `./scripts/verify-phase4.ps1 -CheckRunningStack`

Telemetry export is optional and fail-open. Without an OTLP endpoint the
business services still run; PostgreSQL remains the authoritative business
state.
