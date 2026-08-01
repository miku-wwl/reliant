# Phase 3.1 CI Run

This document records the Phase 3.1 Final Gate CI evidence: the commit, the
workflow, the environment, and the verified test counts (the same
`scripts/verify.ps1` gate that GitHub Actions runs).

- **Commit SHA:** `d77fcb9` (Phase 3.1 Commit 19)
- **Workflow Run:** GitHub Actions `CI` on `miku-wwl/reliant` (triggered on push of the commit above)
- **Runner:** `ubuntu-latest`
- **.NET SDK:** `10.0.x` (actions/setup-dotnet@v4)
- **PostgreSQL image:** `postgres:17` (Testcontainers)
- **LocalStack image:** `localstack/localstack:3` (Testcontainers)

## Verification steps (scripts/verify.ps1)

1. `dotnet restore`
2. `dotnet format --verify-no-changes`
3. `dotnet build`
4. Test count gates (discovery) - a category that matches 0 tests FAILS the run
5. Execute unit, architecture and integration tests with TRX logging
6. Write `artifacts/test-summary.md`

## Verified test counts (2026-08-02, local verify.ps1)

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
- **Result:** PASS (all categories matched their minimums; all executed suites green)

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
