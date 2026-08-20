# Runbook: Queue or Outbox Backlog

Owner: Reliant on-call

Trigger: `ReliantQueueBacklogHigh`, growing Outbox pending count or increasing
processing delay.

1. Confirm whether the backlog is SQS, Outbox, retry, dead-letter or
   reconciliation work.
2. Compare arrival rate, Worker completion rate, active concurrency and
   redelivery rate.
3. Inspect PostgreSQL pool/operation latency, lock symptoms, SQS errors,
   Provider latency/429/5xx and circuit state.
4. Open a slow/failing trace at the suspected boundary.
5. Scale Workers only when downstream PostgreSQL and Provider capacity can
   accept the additional concurrency.
6. After mitigation, verify backlog slope becomes negative, processing delay
   falls and failures do not migrate to another boundary.

Do not purge the queue or replay dead letters as a generic recovery action.

