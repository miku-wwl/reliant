# Phase 3.1 CI Run

This document records the Phase 3.1 Final Gate CI evidence: the commit, the
workflow, the environment, and the verified test counts (the same
`scripts/verify.ps1` gate that GitHub Actions runs).

## Actual run (2026-08-02)

- **Commit SHA:** `9cecfe5` (pushed to `main`; includes Phase 3.1 Commits 11-20)
- **Workflow Run:** https://github.com/miku-wwl/reliant/actions/runs/30720948018 (job 91424539349)
- **Result:** **Success** (3m 11s) - all `verify.ps1` steps green
- **Artifact:** `reliant-test-results` (34.3 KB,
  sha256 `0b9c339752108ee2849a433961f24f593d68426c48f422e9ff91df92ac9d3007`)
- **Runner:** `ubuntu-latest`
- **.NET SDK:** `10.0.x` (actions/setup-dotnet@v4)
- **PostgreSQL image:** `postgres:17` (Testcontainers)
- **LocalStack image:** `localstack/localstack:3` (Testcontainers)
- **Warnings:** 9 (non-fatal: Node 20 deprecation on runner actions, obsolete
  `AttributeNames` SDK API, unread constructor parameters) - none affect PASS

## Verification steps (scripts/verify.ps1)

1. `dotnet restore`
2. `dotnet format --verify-no-changes`
3. `dotnet build`
4. Test count gates (discovery) - a category that matches 0 tests FAILS the run
5. Execute unit, architecture and integration tests with TRX logging
6. Write `artifacts/test-summary.md`

## Verified test counts (2026-08-02, CI run 30720948018)

| Category | Filter | Count | Minimum |
|----------|--------|------:|--------:|
| Unit | `Category=Unit` | 65 | 1 |
| Architecture | `Category=Architecture` | 5 | 1 |
| PostgreSQL Integration | `Category=Integration&Dependency=PostgreSQL` | 52 | 1 |
| LocalStack Integration | `Category=Integration&Dependency=LocalStack` | 15 | 1 |
| HttpApi Integration | `Category=Integration&Dependency=HttpApi` | 9 | 1 |
| WorkerHost E2E | `Category=Integration&Dependency=WorkerHost` | 10 | 10 |
| **Integration total** | `Category=Integration` | **76** | |
| **Total tests** | | **146** | |

- **Unit:** 65 passed, 0 failed
- **Architecture:** 5 passed, 0 failed
- **Integration:** 76 passed, 0 failed (PostgreSQL + LocalStack + WorkerHost E2E + HttpApi)
- **Result:** **PASS** - all categories matched their minimums; all executed suites green (verified by CI run `30720948018`)

## Artifacts (uploaded by the workflow)

- `artifacts/test-results/unit.trx`
- `artifacts/test-results/architecture.trx`
- `artifacts/test-results/integration.trx`
- `artifacts/logs/final-e2e.log`
- `artifacts/test-summary.md`

## Count gate (no silent empty run)

Each category is discovered with `dotnet test --list-tests --filter <category>`
BEFORE execution and must match at least its minimum. If a filter matches 0
tests (or the E2E minimum is not met) the run exits non-zero - a "0 tests
matched but command succeeded" state can never be reported as PASS.
