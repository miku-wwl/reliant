# Runbook: Telemetry Pipeline Unavailable

Owner: Platform on-call

Trigger: `ReliantTelemetryCollectorDown` or missing traces/logs/metrics.

1. Check `http://localhost:13133/` and Prometheus `up{job="otel-collector"}`.
2. Inspect Collector logs and `otelcol_exporter_queue_size` versus capacity.
3. Verify Tempo, Loki and Prometheus readiness independently.
4. Check recent deployment version/commit; determine whether only one service
   stopped exporting.
5. Preserve business processing. Do not restart healthy Workers merely to make
   telemetry appear.
6. Restore the failed backend or Collector, then confirm new traces, logs and
   metrics arrive.

Expected behavior: API commits, Worker processing and SQS ACK continue while
the telemetry endpoint is unreachable. PostgreSQL remains the source of truth
during the evidence gap. Record the gap in the incident timeline.

