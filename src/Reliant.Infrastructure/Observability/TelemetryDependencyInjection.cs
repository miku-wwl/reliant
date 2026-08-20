using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Reliant.Application.Observability;

namespace Reliant.Infrastructure.Observability;

public static class TelemetryDependencyInjection
{
    public static IHostApplicationBuilder AddReliantObservability(
        this IHostApplicationBuilder builder,
        string serviceName)
    {
        var deployment = DeploymentInfo.Create(
            builder.Configuration,
            serviceName,
            builder.Environment.EnvironmentName);
        builder.Services.AddSingleton(deployment);

        var resource = ResourceBuilder.CreateDefault()
            .AddService(
                deployment.ServiceName,
                serviceVersion: deployment.Version,
                serviceInstanceId: deployment.InstanceId)
            .AddAttributes(
            [
                new KeyValuePair<string, object>(
                    "deployment.environment.name",
                    deployment.Environment),
                new KeyValuePair<string, object>(
                    "service.commit.id",
                    deployment.Commit)
            ]);

        var endpoint = ResolveOtlpEndpoint(builder.Configuration);
        var protocol = ResolveOtlpProtocol(builder.Configuration);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resourceBuilder => resourceBuilder
                .AddService(
                    deployment.ServiceName,
                    serviceVersion: deployment.Version,
                    serviceInstanceId: deployment.InstanceId)
                .AddAttributes(
                [
                    new KeyValuePair<string, object>(
                        "deployment.environment.name",
                        deployment.Environment),
                    new KeyValuePair<string, object>(
                        "service.commit.id",
                        deployment.Commit)
                ]))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(ReliantTelemetry.ActivitySourceName)
                    .AddSource("Npgsql")
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        options.RecordException = true;
                    })
                    .AddHttpClientInstrumentation(options =>
                    {
                        options.RecordException = true;
                    });

                if (endpoint is not null)
                {
                    tracing.AddOtlpExporter(options =>
                        ConfigureExporter(options, endpoint, protocol));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(ReliantTelemetry.MeterName)
                    .AddMeter(
                        ReliantTelemetry.OperationalHistoryMeterName)
                    .AddMeter("Microsoft.AspNetCore.Hosting")
                    .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
                    .AddMeter("System.Net.Http")
                    .AddMeter("Npgsql")
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();

                if (endpoint is not null)
                {
                    metrics.AddOtlpExporter(options =>
                        ConfigureExporter(options, endpoint, protocol));
                }
            });

        builder.Logging.Configure(options =>
        {
            options.ActivityTrackingOptions =
                ActivityTrackingOptions.TraceId |
                ActivityTrackingOptions.SpanId |
                ActivityTrackingOptions.ParentId |
                ActivityTrackingOptions.Baggage |
                ActivityTrackingOptions.Tags;
        });
        builder.Logging.AddOpenTelemetry(options =>
        {
            options.SetResourceBuilder(resource);
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
            options.ParseStateValues = true;
            if (endpoint is not null)
            {
                options.AddOtlpExporter(exporter =>
                    ConfigureExporter(exporter, endpoint, protocol));
            }
        });

        return builder;
    }

    private static Uri? ResolveOtlpEndpoint(
        IConfiguration configuration)
    {
        var configured = configuration["Telemetry:OtlpEndpoint"] ??
            Environment.GetEnvironmentVariable(
                "OTEL_EXPORTER_OTLP_ENDPOINT");
        return Uri.TryCreate(
            configured,
            UriKind.Absolute,
            out var endpoint)
            ? endpoint
            : null;
    }

    private static OtlpExportProtocol ResolveOtlpProtocol(
        IConfiguration configuration)
    {
        var configured = configuration["Telemetry:OtlpProtocol"] ??
            Environment.GetEnvironmentVariable(
                "OTEL_EXPORTER_OTLP_PROTOCOL");
        return string.Equals(
            configured,
            "http/protobuf",
            StringComparison.OrdinalIgnoreCase)
            ? OtlpExportProtocol.HttpProtobuf
            : OtlpExportProtocol.Grpc;
    }

    private static void ConfigureExporter(
        OtlpExporterOptions options,
        Uri endpoint,
        OtlpExportProtocol protocol)
    {
        options.Endpoint = endpoint;
        options.Protocol = protocol;
        options.ExportProcessorType = ExportProcessorType.Batch;
        options.BatchExportProcessorOptions.MaxQueueSize = 2048;
        options.BatchExportProcessorOptions.MaxExportBatchSize = 512;
        options.BatchExportProcessorOptions.ScheduledDelayMilliseconds =
            1000;
        options.BatchExportProcessorOptions.ExporterTimeoutMilliseconds =
            3000;
    }
}
