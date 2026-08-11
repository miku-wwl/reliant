# Known Limitations (Phase 3.1)

The following limitations do NOT block the Phase 3.1 Final Gates. They are
deliberately deferred or out of scope for this phase.

## Out of scope / deferred

- **Real AWS (E4) smoke test** - all integration uses Testcontainers PostgreSQL
  (E1) and LocalStack SQS (E2). Real AWS IAM, quotas, KMS, network and provider
  webhook behaviour are not exercised.
- **Notification Handler** is still a skeleton and is explicitly out of the
  Phase 3.1 gate scope.
- **Provider secret management** - `Provider:Secret` comes from configuration; a
  real KMS / secret-vault rotation integration is not implemented.
- **Provider webhook delivery-order edge cases** - callback ordering is proven
  with the sandbox provider; real provider redelivery/ordering quirks are not
  simulated.
- **Retry policy configuration** - retry delays use the fixed `RetryPolicy`
  (exponential backoff with jitter); policy override configuration is not
  exposed.
- **Circuit breaker tuning** - failure threshold (5) and open duration (30 s)
  are constructor defaults, not runtime-configurable.
- **LocalStack vs real SQS parity** - redelivery counters, visibility semantics
  and dead-letter behaviour are validated against LocalStack; real SQS latency
  and eventual-consistency behaviour may differ.

## Infrastructure / environment notes

- **Tenant filter caching** - the global query filters reference a per-context
  instance member (`TenantOrganizationId`) so EF Core re-evaluates the filter per
  context instead of baking the static AsyncLocal value into the process-wide
  compiled query cache. This is required when multiple worker hosts run in one
  process (tests). See `ReliantDbContext.TenantOrganizationId`.
- **Per-instance SQS queues in tests** - LocalStack persists queue state in a
  shared volume across container disposal; E2E fixtures therefore use a unique
  queue name per instance (`Queue:QueueName`, default `reliant-processing`).
- **Local Windows builds** - a stuck `testhost` process can lock the test output
  DLLs; rebuild to an alternate output (`dotnet build -o <dir>`) or restart the
  runner. CI (ubuntu-latest) is unaffected.

## Scope guardrail

None of the above affects the Phase 3.1 Final Gate evidence, which is
established by 163 passing tests (65 unit, 5 architecture, 93 integration;
23 match the WorkerHost filter) over PostgreSQL + LocalStack + a real worker
host. The historical 146-test closure remains preserved in `ci-run.md`.
