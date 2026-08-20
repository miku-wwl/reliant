# Runbook: Worker Lease or Visibility Heartbeat Failure

Owner: Reliant on-call

Trigger: `ReliantWorkerHeartbeatFailure`, SQS redelivery increase or stale
owner/fencing warnings.

1. Correlate lease heartbeat and SQS visibility renewal failures by trace,
   logical message ID and physical SQS message ID.
2. Check PostgreSQL reachability/locks, SQS/network latency and Worker CPU or
   thread-pool saturation.
3. Verify whether ownership moved to a newer fencing token. A stale Worker must
   stop and must not overwrite the newer result.
4. Do not delete the SQS message manually. Inbox deduplication, stable Provider
   key and fencing protect a legitimate redelivery.
5. Restore the failing dependency or replace the unhealthy Worker.
6. Confirm heartbeats recover, redelivery stops and the queue drains.

