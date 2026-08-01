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

All 10 E2E tests PASS together (and pass 3x consecutively for the two classes
that were timing-sensitive): 146 tests total, 0 failures.

## Commit SHA

Commits 13-17 (`03bbeb5` ... `8b95c0d`); re-verified in Commit 19 (`39b492c`).

## CI run

GitHub Actions `CI` (Commit 19, see `ci-run.md`).

## Limitations

Each E2E starts fresh containers (slow but isolated). LocalStack/Testcontainers
only - real AWS E4 is out of scope.

## Conclusion

**Definition of Done PASS** - the reliability scenarios are proven end-to-end
over real infrastructure with exactly-once provider effects.
