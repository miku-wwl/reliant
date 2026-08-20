using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using Reliant.Application.Abstractions;
using Reliant.Domain.Enums;

namespace Reliant.Application.Observability;

/// <summary>
/// Central telemetry contract for Reliant. Metric labels are deliberately
/// restricted to bounded operational dimensions; business identifiers belong
/// on activities and structured logs only.
/// </summary>
public static class ReliantTelemetry
{
    public const string ActivitySourceName = "Reliant";
    public const string MeterName = "Reliant";
    public const string OperationalHistoryMeterName =
        "Reliant.OperationalHistory";

    public static readonly ActivitySource Activities =
        new(ActivitySourceName, "1.0.0");

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    private static readonly Counter<long> ApiRateLimitRejections =
        Meter.CreateCounter<long>("api_rate_limit_rejection_total");
    private static readonly Counter<long> QueuePublish =
        Meter.CreateCounter<long>("queue_publish_total");
    private static readonly Counter<long> QueueReceive =
        Meter.CreateCounter<long>("queue_receive_total");
    private static readonly Counter<long> QueueDelete =
        Meter.CreateCounter<long>("queue_delete_total");
    private static readonly Counter<long> QueueRedelivery =
        Meter.CreateCounter<long>("queue_redelivery_total");
    private static readonly Histogram<double> QueueProcessingDelay =
        Meter.CreateHistogram<double>(
            "queue_processing_delay",
            unit: "s");
    private static readonly UpDownCounter<long> WorkerInflight =
        Meter.CreateUpDownCounter<long>("worker_inflight");
    private static readonly Counter<long> WorkerRuns =
        Meter.CreateCounter<long>("worker_run_total");
    private static readonly Histogram<double> WorkerRunDuration =
        Meter.CreateHistogram<double>("worker_run_duration", unit: "s");
    private static readonly Counter<long> LeaseHeartbeatFailures =
        Meter.CreateCounter<long>("lease_heartbeat_failure_total");
    private static readonly Counter<long> VisibilityRenewalFailures =
        Meter.CreateCounter<long>("visibility_renewal_failure_total");

    private static readonly Counter<long> ProviderRequests =
        Meter.CreateCounter<long>("provider_request_total");
    private static readonly Histogram<double> ProviderRequestDuration =
        Meter.CreateHistogram<double>(
            "provider_request_duration",
            unit: "s");
    private static readonly Counter<long> ProviderErrors =
        Meter.CreateCounter<long>("provider_error_total");
    private static readonly Counter<long> ProviderTimeouts =
        Meter.CreateCounter<long>("provider_timeout_total");
    private static readonly Counter<long> ProviderUnknown =
        Meter.CreateCounter<long>("provider_unknown_total");
    private static readonly Counter<long> ProviderIdempotencyConflicts =
        Meter.CreateCounter<long>(
            "provider_idempotency_conflict_total");
    private static readonly Counter<long> ProviderDuplicateEffects =
        Meter.CreateCounter<long>(
            "provider_duplicate_effect_detected_total");

    private static readonly Counter<long> CallbackReceived =
        Meter.CreateCounter<long>("callback_received_total");
    private static readonly Counter<long> CallbackInvalidSignature =
        Meter.CreateCounter<long>("callback_invalid_signature_total");
    private static readonly Counter<long> CallbackInvalidTimestamp =
        Meter.CreateCounter<long>("callback_invalid_timestamp_total");
    private static readonly Counter<long> CallbackDuplicate =
        Meter.CreateCounter<long>("callback_duplicate_total");
    private static readonly Counter<long> CallbackOrphan =
        Meter.CreateCounter<long>("callback_orphan_total");
    private static readonly Counter<long> CallbackTerminalConflict =
        Meter.CreateCounter<long>("callback_terminal_conflict_total");
    private static readonly Histogram<double> CallbackDuration =
        Meter.CreateHistogram<double>(
            "callback_processing_duration",
            unit: "s");

    private static readonly Counter<long> RetryScheduled =
        Meter.CreateCounter<long>("retry_scheduled_total");
    private static readonly Counter<long> RetryExhausted =
        Meter.CreateCounter<long>("retry_exhausted_total");
    private static readonly Counter<long> DeadLetterReplay =
        Meter.CreateCounter<long>("deadletter_replay_total");
    private static readonly Counter<long> ReconciliationResolution =
        Meter.CreateCounter<long>("reconciliation_resolution_total");
    private static readonly Counter<long> ReconciliationManualRequired =
        Meter.CreateCounter<long>(
            "reconciliation_manual_required_total");

    private static readonly Counter<long> CircuitTransitions =
        Meter.CreateCounter<long>("circuit_transition_total");
    private static readonly Counter<long> CircuitHalfOpenProbes =
        Meter.CreateCounter<long>("circuit_half_open_probe_total");

    private static RuntimeMetricSnapshot _runtimeSnapshot =
        RuntimeMetricSnapshot.Empty;
    private static int _circuitState =
        (int)CircuitBreakerState.Closed;

    private static readonly ObservableGauge<long> QueueDepth =
        Meter.CreateObservableGauge(
            "queue_depth",
            ObserveQueueDepth);
    private static readonly ObservableGauge<double> QueueOldestAge =
        Meter.CreateObservableGauge(
            "queue_oldest_message_age",
            ObserveQueueOldestAge,
            unit: "s");
    private static readonly ObservableGauge<long> OutboxPending =
        Meter.CreateObservableGauge(
            "outbox_pending_count",
            () => Volatile.Read(ref _runtimeSnapshot).OutboxPending);
    private static readonly ObservableGauge<long> RetryPending =
        Meter.CreateObservableGauge(
            "retry_pending_count",
            ObserveRetryPending);
    private static readonly ObservableGauge<double> RetryOldestAge =
        Meter.CreateObservableGauge(
            "retry_oldest_age",
            ObserveRetryOldestAge,
            unit: "s");
    private static readonly ObservableGauge<long> DeadLetterPending =
        Meter.CreateObservableGauge(
            "deadletter_pending_count",
            ObserveDeadLetterPending);
    private static readonly ObservableGauge<long> ReconciliationPending =
        Meter.CreateObservableGauge(
            "reconciliation_pending_count",
            () => Volatile.Read(ref _runtimeSnapshot)
                .ReconciliationPending);
    private static readonly ObservableGauge<double>
        ReconciliationOldestAge = Meter.CreateObservableGauge(
            "reconciliation_oldest_age",
            () => Volatile.Read(ref _runtimeSnapshot)
                .ReconciliationOldestAgeSeconds,
            unit: "s");
    private static readonly ObservableGauge<long> CircuitState =
        Meter.CreateObservableGauge(
            "circuit_state",
            ObserveCircuitState);

    public static Activity? StartActivity(
        string name,
        ActivityKind kind = ActivityKind.Internal)
        => Activities.StartActivity(name, kind);

    public static Activity? StartActivity(
        string name,
        ActivityKind kind,
        string? traceParent,
        string? traceState = null,
        bool linkInsteadOfParent = false)
    {
        if (!ActivityContext.TryParse(
                traceParent,
                traceState,
                isRemote: true,
                out var propagatedContext))
        {
            return Activities.StartActivity(name, kind);
        }

        return linkInsteadOfParent
            ? Activities.StartActivity(
                name,
                kind,
                default(ActivityContext),
                links: [new ActivityLink(propagatedContext)])
            : Activities.StartActivity(
                name,
                kind,
                propagatedContext);
    }

    public static Activity? StartQueueConsumer(
        IQueueMessage message,
        string queue,
        string handler)
    {
        var activity = StartActivity(
            $"{message.MessageType} process",
            ActivityKind.Consumer,
            message.TraceParent,
            message.TraceState,
            linkInsteadOfParent: message.ApproximateReceiveCount > 1);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag("messaging.system", "aws_sqs");
        activity.SetTag("messaging.destination.name", queue);
        activity.SetTag("messaging.operation.type", "process");
        activity.SetTag("messaging.message.type", message.MessageType);
        activity.SetTag("messaging.message.id", message.MessageId);
        activity.SetTag(
            "messaging.sqs.message.id",
            message.PhysicalMessageId);
        activity.SetTag(
            "messaging.message.receive_count",
            message.ApproximateReceiveCount);
        activity.SetTag("reliant.handler", handler);
        activity.SetTag("reliant.correlation_id", message.CorrelationId);
        activity.SetTag("reliant.causation_id", message.CausationId);
        activity.SetTag(
            "service.producer.version",
            message.DeploymentVersion);

        if (!string.IsNullOrWhiteSpace(message.CorrelationId))
        {
            activity.AddBaggage(
                "reliant.correlation_id",
                message.CorrelationId);
        }

        return activity;
    }

    public static Activity? StartQueueProducer(
        string queue,
        string messageType,
        string messageId)
    {
        var activity = Activities.StartActivity(
            $"{messageType} send",
            ActivityKind.Producer);
        activity?.SetTag("messaging.system", "aws_sqs");
        activity?.SetTag("messaging.destination.name", queue);
        activity?.SetTag("messaging.operation.type", "send");
        activity?.SetTag("messaging.message.type", messageType);
        activity?.SetTag("messaging.message.id", messageId);
        return activity;
    }

    public static void RecordApiRateLimitRejection(string endpoint)
        => ApiRateLimitRejections.Add(
            1,
            MetricTag("endpoint", NormalizeEndpoint(endpoint)));

    public static void RecordQueuePublish(
        string queue,
        string result)
        => QueuePublish.Add(
            1,
            MetricTag("queue", NormalizeQueue(queue)),
            MetricTag("result", NormalizeResult(result)));

    public static void RecordQueueReceive(
        string queue,
        IQueueMessage message)
    {
        var normalizedQueue = NormalizeQueue(queue);
        QueueReceive.Add(1, MetricTag("queue", normalizedQueue));
        if (message.ApproximateReceiveCount > 1)
        {
            QueueRedelivery.Add(1, MetricTag("queue", normalizedQueue));
        }

        if (message.SentAt.HasValue)
        {
            var age = Math.Max(
                0,
                (DateTimeOffset.UtcNow - message.SentAt.Value)
                    .TotalSeconds);
            QueueProcessingDelay.Record(
                age,
                MetricTag("queue", normalizedQueue));
        }
    }

    public static void RecordQueueDelete(string queue, string result)
        => QueueDelete.Add(
            1,
            MetricTag("queue", NormalizeQueue(queue)),
            MetricTag("result", NormalizeResult(result)));

    public static void ChangeWorkerInflight(string handler, long delta)
        => WorkerInflight.Add(
            delta,
            MetricTag("handler", NormalizeHandler(handler)));

    public static void RecordWorkerRun(
        string handler,
        string result,
        TimeSpan duration)
    {
        var normalizedHandler = NormalizeHandler(handler);
        var normalizedResult = NormalizeResult(result);
        WorkerRuns.Add(
            1,
            MetricTag("handler", normalizedHandler),
            MetricTag("result", normalizedResult));
        WorkerRunDuration.Record(
            Math.Max(0, duration.TotalSeconds),
            MetricTag("handler", normalizedHandler),
            MetricTag("result", normalizedResult));
    }

    public static void RecordLeaseHeartbeatFailure(string reason)
        => LeaseHeartbeatFailures.Add(
            1,
            MetricTag("handler", "processing"),
            MetricTag("reason", NormalizeReason(reason)));

    public static void RecordVisibilityRenewalFailure(
        string queue,
        string reason)
        => VisibilityRenewalFailures.Add(
            1,
            MetricTag("queue", NormalizeQueue(queue)),
            MetricTag("reason", NormalizeReason(reason)));

    public static void RecordProviderRequest(
        string operation,
        string result,
        TimeSpan duration,
        ErrorCategory? errorCategory = null)
    {
        var normalizedOperation = NormalizeOperation(operation);
        var normalizedResult = NormalizeResult(result);
        ProviderRequests.Add(
            1,
            MetricTag("provider", "sandbox"),
            MetricTag("operation", normalizedOperation),
            MetricTag("result", normalizedResult));
        ProviderRequestDuration.Record(
            Math.Max(0, duration.TotalSeconds),
            MetricTag("provider", "sandbox"),
            MetricTag("operation", normalizedOperation));

        if (errorCategory.HasValue)
        {
            ProviderErrors.Add(
                1,
                MetricTag("provider", "sandbox"),
                MetricTag(
                    "error_category",
                    NormalizeErrorCategory(errorCategory.Value)));
        }

        if (errorCategory == ErrorCategory.Timeout)
        {
            ProviderTimeouts.Add(
                1,
                MetricTag("provider", "sandbox"),
                MetricTag("operation", normalizedOperation));
        }
    }

    public static void RecordProviderUnknown(ErrorCategory? errorCategory)
        => ProviderUnknown.Add(
            1,
            MetricTag("provider", "sandbox"),
            MetricTag(
                "error_category",
                errorCategory.HasValue
                    ? NormalizeErrorCategory(errorCategory.Value)
                    : "unknown"));

    public static void RecordProviderIdempotencyConflict()
        => ProviderIdempotencyConflicts.Add(
            1,
            MetricTag("provider", "sandbox"));

    public static void RecordProviderDuplicateEffect()
        => ProviderDuplicateEffects.Add(
            1,
            MetricTag("provider", "sandbox"));

    public static void RecordCallback(
        string result,
        TimeSpan duration)
    {
        var normalizedResult = NormalizeResult(result);
        CallbackReceived.Add(
            1,
            MetricTag("provider", "sandbox"),
            MetricTag("result", normalizedResult));
        CallbackDuration.Record(
            Math.Max(0, duration.TotalSeconds),
            MetricTag("provider", "sandbox"),
            MetricTag("result", normalizedResult));
    }

    public static void RecordCallbackVerificationFailure(string reason)
    {
        if (reason.Contains("timestamp", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("window", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("UTC", StringComparison.OrdinalIgnoreCase))
        {
            CallbackInvalidTimestamp.Add(
                1,
                MetricTag("provider", "sandbox"),
                MetricTag("reason", NormalizeReason(reason)));
            return;
        }

        CallbackInvalidSignature.Add(
            1,
            MetricTag("provider", "sandbox"));
    }

    public static void RecordCallbackDuplicate()
        => CallbackDuplicate.Add(
            1,
            MetricTag("provider", "sandbox"));

    public static void RecordCallbackOrphan()
        => CallbackOrphan.Add(
            1,
            MetricTag("provider", "sandbox"));

    public static void RecordCallbackTerminalConflict()
        => CallbackTerminalConflict.Add(
            1,
            MetricTag("provider", "sandbox"));

    public static void RecordRetryScheduled(ErrorCategory? category)
        => RetryScheduled.Add(
            1,
            MetricTag("provider", "sandbox"),
            MetricTag(
                "error_category",
                category.HasValue
                    ? NormalizeErrorCategory(category.Value)
                    : "none"));

    public static void RecordRetryExhausted(ErrorCategory? category)
        => RetryExhausted.Add(
            1,
            MetricTag("provider", "sandbox"),
            MetricTag(
                "error_category",
                category.HasValue
                    ? NormalizeErrorCategory(category.Value)
                    : "none"));

    public static void RecordDeadLetterReplay(
        string messageType,
        string result)
        => DeadLetterReplay.Add(
            1,
            MetricTag("message_type", NormalizeMessageType(messageType)),
            MetricTag("result", NormalizeResult(result)));

    public static void RecordReconciliationResolution(string resolution)
    {
        var normalized = NormalizeResolution(resolution);
        ReconciliationResolution.Add(
            1,
            MetricTag("resolution", normalized));
        if (normalized == "manual_required")
        {
            ReconciliationManualRequired.Add(1);
        }
    }

    public static void RecordCircuitTransition(
        CircuitBreakerState from,
        CircuitBreakerState to)
    {
        Volatile.Write(ref _circuitState, (int)to);
        if (from == to)
        {
            return;
        }

        CircuitTransitions.Add(
            1,
            MetricTag("provider", "sandbox"),
            MetricTag("from", NormalizeCircuitState(from)),
            MetricTag("to", NormalizeCircuitState(to)));
    }

    public static void RecordCircuitHalfOpenProbe(string result)
        => CircuitHalfOpenProbes.Add(
            1,
            MetricTag("provider", "sandbox"),
            MetricTag("result", NormalizeResult(result)));

    public static void SetRuntimeSnapshot(RuntimeMetricSnapshot snapshot)
        => Volatile.Write(ref _runtimeSnapshot, snapshot);

    public static string NormalizeQueue(string queue)
    {
        if (queue.Contains("notification", StringComparison.OrdinalIgnoreCase))
        {
            return "notification";
        }

        if (queue.Contains("processing", StringComparison.OrdinalIgnoreCase))
        {
            return "processing";
        }

        if (queue.Contains("dead", StringComparison.OrdinalIgnoreCase) ||
            queue.Contains("dlq", StringComparison.OrdinalIgnoreCase))
        {
            return "deadletter";
        }

        return "other";
    }

    public static string TenantSafeId(Guid organizationId)
    {
        var hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(
                organizationId.ToString("N")));
        return Convert.ToHexString(hash.AsSpan(0, 8))
            .ToLowerInvariant();
    }

    private static IEnumerable<Measurement<long>> ObserveQueueDepth()
    {
        var snapshot = Volatile.Read(ref _runtimeSnapshot);
        yield return new Measurement<long>(
            snapshot.ProcessingQueueDepth,
            MetricTag("queue", "processing"));
        yield return new Measurement<long>(
            snapshot.NotificationQueueDepth,
            MetricTag("queue", "notification"));
    }

    private static IEnumerable<Measurement<double>> ObserveQueueOldestAge()
    {
        var snapshot = Volatile.Read(ref _runtimeSnapshot);
        if (snapshot.ProcessingQueueOldestAgeSeconds.HasValue)
        {
            yield return new Measurement<double>(
                snapshot.ProcessingQueueOldestAgeSeconds.Value,
                MetricTag("queue", "processing"));
        }

        if (snapshot.NotificationQueueOldestAgeSeconds.HasValue)
        {
            yield return new Measurement<double>(
                snapshot.NotificationQueueOldestAgeSeconds.Value,
                MetricTag("queue", "notification"));
        }
    }

    private static Measurement<long> ObserveRetryPending()
        => new(
            Volatile.Read(ref _runtimeSnapshot).RetryPending,
            MetricTag("provider", "sandbox"),
            MetricTag("error_category", "all"));

    private static Measurement<double> ObserveRetryOldestAge()
        => new(
            Volatile.Read(ref _runtimeSnapshot).RetryOldestAgeSeconds,
            MetricTag("provider", "sandbox"));

    private static Measurement<long> ObserveDeadLetterPending()
        => new(
            Volatile.Read(ref _runtimeSnapshot).DeadLetterPending,
            MetricTag("message_type", "all"),
            MetricTag("error_category", "all"));

    private static Measurement<long> ObserveCircuitState()
    {
        var state = (CircuitBreakerState)Volatile.Read(
            ref _circuitState);
        return new Measurement<long>(
            1,
            MetricTag("provider", "sandbox"),
            MetricTag("state", NormalizeCircuitState(state)));
    }

    private static KeyValuePair<string, object?> MetricTag(
        string name,
        object? value)
        => new(name, value);

    private static string NormalizeEndpoint(string endpoint)
        => endpoint.StartsWith("/api/callbacks", StringComparison.Ordinal)
            ? "callbacks"
            : endpoint.StartsWith("/api/contributions", StringComparison.Ordinal)
                ? "contributions"
                : "other";

    private static string NormalizeOperation(string operation)
        => operation.ToLowerInvariant() switch
        {
            "submit" => "submit",
            "query_by_reference" => "query_by_reference",
            "query_by_idempotency_key" => "query_by_idempotency_key",
            "cancel" => "cancel",
            "health" => "health",
            _ => "other"
        };

    private static string NormalizeHandler(string handler)
        => handler.ToLowerInvariant() switch
        {
            "processing" => "processing",
            "notification" => "notification",
            "reconciliation" => "reconciliation",
            "outbox" => "outbox",
            "maintenance" => "maintenance",
            _ => "other"
        };

    private static string NormalizeMessageType(string messageType)
        => messageType switch
        {
            "ContributionCreated" => "contribution_created",
            "ContributionRetryRequested" =>
                "contribution_retry_requested",
            "ContributionSucceeded" => "contribution_succeeded",
            "OperatorAlert" => "operator_alert",
            _ => "other"
        };

    private static string NormalizeResolution(string resolution)
        => resolution.ToLowerInvariant() switch
        {
            "autofixed" => "auto_fixed",
            "saferetry" => "safe_retry",
            "waitnextcycle" => "wait_next_cycle",
            "manualrequired" => "manual_required",
            _ => "other"
        };

    private static string NormalizeResult(string result)
        => result.ToLowerInvariant() switch
        {
            "success" or "succeeded" or "processed" => "success",
            "failure" or "failed" or "error" => "failure",
            "unknown" => "unknown",
            "timeout" => "timeout",
            "deferred" => "deferred",
            "duplicate" => "duplicate",
            "orphan" => "orphan",
            "rejected" => "rejected",
            "not_found" => "not_found",
            "not_pending" => "not_pending",
            _ => "other"
        };

    private static string NormalizeReason(string reason)
    {
        var value = reason.ToLowerInvariant();
        if (value.Contains("receipt")) return "invalid_receipt";
        if (value.Contains("rate") || value.Contains("throttl"))
            return "rate_limited";
        if (value.Contains("timeout") || value.Contains("window"))
            return "timeout";
        if (value.Contains("timestamp") || value.Contains("utc"))
            return "invalid_timestamp";
        if (value.Contains("stale") || value.Contains("owner"))
            return "stale_owner";
        if (value.Contains("network") || value.Contains("transport"))
            return "network";
        return "other";
    }

    private static string NormalizeErrorCategory(ErrorCategory category)
        => category.ToString().ToLowerInvariant();

    private static string NormalizeCircuitState(CircuitBreakerState state)
        => state.ToString().ToLowerInvariant();
}

public sealed record RuntimeMetricSnapshot(
    long ProcessingQueueDepth,
    long NotificationQueueDepth,
    double? ProcessingQueueOldestAgeSeconds,
    double? NotificationQueueOldestAgeSeconds,
    long OutboxPending,
    long RetryPending,
    double RetryOldestAgeSeconds,
    long DeadLetterPending,
    long ReconciliationPending,
    double ReconciliationOldestAgeSeconds)
{
    public static RuntimeMetricSnapshot Empty { get; } = new(
        0,
        0,
        null,
        null,
        0,
        0,
        0,
        0,
        0,
        0);
}
