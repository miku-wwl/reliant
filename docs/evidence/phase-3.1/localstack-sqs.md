# Evidence — LocalStack SQS (Gate 9)

## Invariant

The integration uses a real SQS implementation (LocalStack) for send, receive,
delete, visibility-timeout redelivery, duplicate delivery, and the
`ApproximateReceiveCount` counter - not mocks.

## Scenario

Real SQS round-trips and redelivery semantics, plus the worker consuming through
a raw-SDK adapter that captures `ApproximateReceiveCount` per delivery.

## Failure injection

- Unacked message -> visibility timeout -> redelivery (same MessageId).
- Unacked message not visible within the visibility window.
- Duplicate delivery observed and deduplicated by the consumer.

## Runtime path

`SqsQueueAdapter` -> `AmazonSQSClient` -> LocalStack `localstack/localstack:3` ->
queues created lazily (`GetOrCreateQueueAsync`) -> send/receive/delete.

## Exact tests

- `LocalStackSqsTests.SendAndReceive_ShouldRoundTripMessage`
- `LocalStackSqsTests.Delete_ShouldRemoveMessage`
- `LocalStackSqsTests.VisibilityTimeout_ShouldRedeliverUnackedMessage`
- `LocalStackSqsTests.UnackedMessage_ShouldNotBeVisible_WithinVisibilityWindow`
- `LocalStackSqsTests.DuplicateDelivery_ShouldBeObservable_AndDeduplicatedByConsumer`
- `DuplicateMessageE2ETests.UnackedSameSqsMessage_ShouldRedeliverWithSameMessageId`
- `CircuitOpenE2ETests.CircuitOpen_ShouldLeaveMessageUnacked_AndRedeliverAfterVisibilityTimeout` (ApproximateReceiveCount >= 2)

## Exact assertions

```text
Message body round-trips unchanged
Delete removes the message
Unacked message redelivers with the SAME MessageId after visibility timeout
Unacked message is NOT visible inside the visibility window
ApproximateReceiveCount >= 2 after forced redelivery
```

## Observed result

All PASS. LocalStack reproduces real SQS redelivery and counters, and the worker
(using its own raw adapter) observes `ApproximateReceiveCount >= 2`.

## Commit SHA

Commits 9-17 (`c591004` ... `8b95c0d`); re-verified in Commit 19 (`39b492c`).

## CI run

GitHub Actions `CI` (Commit 19, see `ci-run.md`).

## Limitations

LocalStack (E2), not real AWS (E4). Real AWS quotas, IAM, KMS and network
behaviour are out of scope for this gate.

## Conclusion

**Gate 9 PASS** — real SQS behaviour (including redelivery and receive counts)
is exercised via LocalStack.
