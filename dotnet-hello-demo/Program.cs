using System.Diagnostics.Metrics;
using OpenTelemetry.Metrics;

const string meterName = "DotnetHelloDemo.Business";

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter(meterName)
        .AddAspNetCoreInstrumentation()
        .AddPrometheusExporter());

var app = builder.Build();

using var meter = new Meter(meterName);
var businessOperation = meter.CreateCounter<long>("demo_business_operation");

app.MapGet("/hello", () =>
{
    RecordBusinessResult(businessOperation, "success");
    return Results.Ok("Hello from .NET");
});

app.MapGet("/hello/fail", () =>
{
    RecordBusinessResult(businessOperation, "failure");
    return Results.Problem("Intentional demo failure", statusCode: StatusCodes.Status500InternalServerError);
});

app.MapGet("/health", () => Results.Ok(new { status = "UP" }));

app.MapPrometheusScrapingEndpoint();

app.Run();

static void RecordBusinessResult(Counter<long> counter, string result)
{
    counter.Add(
        1,
        new KeyValuePair<string, object?>("operation", "hello"),
        new KeyValuePair<string, object?>("result", result));
}
