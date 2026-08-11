# Phase 3.1 CI Run

This document records the Phase 3.1 Final Gate CI evidence: the commit, the
workflow, the environment, and the verified test counts (the same
`scripts/verify.ps1` gate that GitHub Actions runs).

## Current completion audit run (2026-08-11)

- **Implementation Commit SHA:** `3dc26f95c3a1a6ae81c6b2e905cb47fcc27be407`
- **Workflow Run:** https://github.com/miku-wwl/reliant/actions/runs/31446024186
  (job 93640326100)
- **Result:** **Success** (6m 53s)
- **Runner:** `ubuntu-latest`
- **Artifact:** `reliant-test-results` (50,275 bytes, artifact id 9084685146,
  sha256 `56ef88c544820bd4d00f02dfb57ab91ed56255aa2d1c9e561d3851d8b83677d7`)
- **Build:** 0 warnings, 0 errors
- **Dependency audit:** 0 known vulnerable packages
- **Experiment inventory:** Phase 2 Exp1-Exp12 and Phase 3 Exp1-Exp15 all
  have at least one executable test and exactly one aggregate report

### Current verified test counts

| Category | Filter | Count | Minimum | Status |
|----------|--------|------:|--------:|--------|
| Unit | `Category=Unit` | 65 | 1 | PASS |
| Architecture | `Category=Architecture` | 5 | 1 | PASS |
| PostgreSQL Integration | `Category=Integration&Dependency=PostgreSQL` | 85 | 1 | PASS |
| LocalStack Integration | `Category=Integration&Dependency=LocalStack` | 35 | 1 | PASS |
| HttpApi Integration | `Category=Integration&Dependency=HttpApi` | 10 | 1 | PASS |
| WorkerHost E2E | `Category=Integration&Dependency=WorkerHost` | 23 | 10 | PASS |
| **Integration total** | `Category=Integration` | **93** | | **PASS** |
| **Total tests** | | **163** | | **PASS** |

The dependency filters overlap. CI executed 65 unit, 5 architecture and 93
integration tests: 163 passed, 0 failed and 0 skipped.

### Current uploaded artifacts

- `test-results/unit.trx`
- `test-results/architecture.trx`
- `test-results/integration.trx`
- `logs/final-e2e.log`
- `test-summary.md`
- `experiment-summary.md`
- `vulnerable-packages.txt`

## Historical baseline

### Actual run (2026-08-02)

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

### Historical verification steps (scripts/verify.ps1)

1. `dotnet restore`
2. `dotnet format --verify-no-changes`
3. `dotnet build`
4. Test count gates (discovery) - a category that matches 0 tests FAILS the run
5. Execute unit, architecture and integration tests with TRX logging
6. Write `artifacts/test-summary.md`

### Historical verified test counts (2026-08-02, CI run 30720948018)

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

### Historical artifacts (uploaded by the workflow)

- `artifacts/test-results/unit.trx`
- `artifacts/test-results/architecture.trx`
- `artifacts/test-results/integration.trx`
- `artifacts/logs/final-e2e.log`
- `artifacts/test-summary.md`

## Count gates (no silent empty run)

Each category is discovered with `dotnet test --list-tests --filter <category>`
BEFORE execution and must match at least its minimum. If a filter matches 0
tests (or the E2E minimum is not met) the run exits non-zero - a "0 tests
matched but command succeeded" state can never be reported as PASS.

`scripts/verify-experiments.ps1` adds a second gate: every Phase 2/3 experiment
must retain at least one discovered test and exactly one aggregate report.
