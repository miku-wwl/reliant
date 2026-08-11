# Evidence — Final End-to-End (Definition of Done)

## Invariant

The full production pipeline (Outbox -> LocalStack SQS -> Worker -> Sandbox
Provider -> Reconciliation -> Callback) converges to the correct terminal state
with exactly one provider effect, and the class of reliability scenarios
(duplicate callback, duplicate message, crash-before-ACK, safe retry, circuit
open) is proven over REAL infrastructure.

## Scenario

Five isolated worker-host E2E suites, each spinning up its own PostgreSQL +
LocalStack + real worker host (`WorkerHostFixture`).

## Failure injection

- `ProcessedButResponseLost` provider mode.
- Duplicate callback (same EventId twice).
- New message for a Succeeded contribution.
- One-shot `BeforeMessageAck` crash.
- `TimeoutBeforeProcessing` then runtime mode switch to `Success`.
- Circuit opened via `RecordFailure` x5, closed via `RecordSuccess`.

## Runtime path

Real `OutboxPublisher -> SQS -> ProcessingHandler -> provider ->
ReconciliationHandler -> RetryScheduler` with per-fixture unique queues and
per-instance tenant filter (fixing EF query-filter caching across hosts in one
process).

## Exact tests

- `FinalE2ETests.ProcessedResponseLost_WithDuplicateMessageAndCallback_ShouldConverge_WithoutSecondProviderEffect`
- `FinalE2ETests.CallbackBeforeReconciliation_WithDuplicateEvent_ShouldConvergeOnce`
- `DuplicateMessageE2ETests` (5 tests, see provider-idempotency.md / localstack-sqs.md)
- `CrashBeforeAckE2ETests.CrashBeforeMessageAck_ShouldRedeliverAndDeduplicate_WithoutSecondProviderEffect`
- `SafeRetryE2ETests.TimeoutBeforeProcessing_ShouldReconcileNotFound_ThenRetryAndSucceed_WithOneProviderEffect`
- `CircuitOpenE2ETests.CircuitOpen_ShouldLeaveMessageUnacked_AndRedeliverAfterVisibilityTimeout`

## Exact assertions

```text
Contribution converges to Succeeded
ProviderOperationCount == 1
ProviderReferenceCount == 1
ProcessingInboxCount == 1
Full transition path audited (Created->Accepted->Processing->ProviderUnknown->ReconciliationPending->Succeeded)
CallbackInboxCount == 1 for the duplicate callback
SqsReceiveCount >= 2 (crash/circuit scenarios)
QueueEventuallyEmpty
NoDeadLetter
```

## Observed result

The original 10 Phase 3.1 E2E scenarios still pass together. The current
WorkerHost-filtered suite has expanded to 23 tests; CI run `31446024186`
completed 163/163 repository tests with 0 failures and 0 skipped tests.

## Commit SHA

Commits 13-17 (`03bbeb5` ... `8b95c0d`); re-verified with the expanded suite at
implementation commit `3dc26f9`.

## CI run

GitHub Actions `CI` run `31446024186` (see `ci-run.md`).

## Limitations

Each E2E starts fresh containers (slow but isolated). LocalStack/Testcontainers
only - real AWS E4 is out of scope.

## Conclusion

**Definition of Done PASS** - the reliability scenarios are proven end-to-end
over real infrastructure with exactly-once provider effects.
