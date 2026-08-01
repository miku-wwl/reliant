# Evidence — Crash Recovery (Gate 8)

## Invariant

If the worker crashes AFTER the DB commit (state + inbox) but BEFORE the SQS
delete, the message redelivers with the same SQS MessageId; the inbox dedup
swallows it with NO second provider call, and the message is eventually acked.

## Scenario

Worker processes successfully (contribution -> Succeeded, inbox committed),
then a `BeforeMessageAck` fault fires BEFORE `DeleteMessageAsync`; the message is
left unacked -> visibility timeout -> redelivery with the SAME MessageId -> the
worker's inbox dedup deletes it without reprocessing.

## Failure injection

- One-shot `ThrowingFaultInjector(WorkerFaultPoint.BeforeMessageAck)`.
- Real visibility timeout (3 s) through LocalStack.
- Also: crash after attempt persisted, crash before response handled.

## Runtime path

`ProcessingHandler` -> state + inbox `SaveChangesAsync` (committed) ->
`BeforeMessageAck` throws -> catch logs, NO delete -> visibility expiry ->
redelivery -> `inboxRepo.GetByMessageIdAsync(MessageId)` finds `Processed` ->
delete + skip.

## Exact tests

- `CrashBeforeAckE2ETests.CrashBeforeMessageAck_ShouldRedeliverAndDeduplicate_WithoutSecondProviderEffect`
- `DuplicateMessageE2ETests.RedeliveredSameMessage_ShouldBeDeduplicatedByInbox`
- `DuplicateMessageE2ETests.Redelivery_ShouldNotInvokeProviderAgain`
- `CrashRecoveryTests.CrashAfterAttemptPersisted_ShouldRecoverWithSameKey_WithoutSecondEffect`
- `CrashRecoveryTests.CrashBeforeResponseHandled_ShouldNotCreateSecondProviderEffect`
- `CrashRecoveryTests.DuplicateInboxDelivery_ShouldBeDeduplicated`

## Exact assertions

```text
ProviderOperationCount == 1
ProviderReferenceCount == 1
Contribution.State == Succeeded
ProcessingInboxCount == 1
SqsReceiveCount >= 2
QueueEventuallyEmpty == true
NoDeadLetter == true
NoSecondSuccessfulAttempt == true (ProcessingAttempt count == 1)
```

## Observed result

All PASS. The crash-after-commit-before-ACK E2E observes `ReceiveCount >= 2`
(the redelivery), a single inbox row, a single provider effect, an empty queue,
no dead letters and a single successful attempt.

## Commit SHA

Commits 14-15 (`cb907da`, `40e2f42`); re-verified in Commit 19 (`39b492c`).

## CI run

GitHub Actions `CI` (Commit 19, see `ci-run.md`).

## Limitations

Crash is simulated by a fault injector (a real process kill is approximated).
Recovery depends on SQS redelivery semantics (reproduced by LocalStack).

## Conclusion

**Gate 8 PASS** — a crash between DB commit and ACK is safe: exactly-once
provider effect via inbox dedup, and the message is eventually acked.
