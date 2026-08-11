using System.CommandLine;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Reliant.Application;
using Reliant.Application.Abstractions;
using Reliant.Application.Operations;
using Reliant.Infrastructure;
using Reliant.Infrastructure.Persistence;

var rootCommand = new RootCommand(
    "reliantctl - Reliant operational CLI");
var connectionStringOption = new Option<string?>(
    "--connection-string")
{
    Description =
        "PostgreSQL connection string. Defaults to CONNECTION_STRING."
};
rootCommand.Options.Add(connectionStringOption);

var diagnosticsCollect = new Command(
    "collect",
    "Collect diagnostic information");
diagnosticsCollect.SetAction(_ =>
{
    Console.WriteLine(
        "Use scripts/verify.ps1 and the experiment evidence pack " +
        "for diagnostics collection.");
    return Task.CompletedTask;
});
var diagnosticsCommand = new Command(
    "diagnostics",
    "Diagnostic operations");
diagnosticsCommand.Subcommands.Add(diagnosticsCollect);

var jobIdOption = RequiredGuidOption(
    "--id",
    "JobRun identifier.");
var jobOrganizationOption = RequiredGuidOption(
    "--organization",
    "Organization identifier used for tenant isolation.");
var jobsInspect = new Command("inspect", "Inspect job status");
jobsInspect.Options.Add(jobIdOption);
jobsInspect.Options.Add(jobOrganizationOption);
jobsInspect.SetAction(async (parseResult, cancellationToken) =>
{
    await using var provider = BuildServiceProvider(
        parseResult.GetValue(connectionStringOption));
    await using var scope = provider.CreateAsyncScope();
    var db = scope.ServiceProvider
        .GetRequiredService<ReliantDbContext>();
    var jobId = parseResult.GetRequiredValue(jobIdOption);
    var organizationId = parseResult.GetRequiredValue(
        jobOrganizationOption);
    var job = await db.JobRuns
        .IgnoreQueryFilters()
        .SingleOrDefaultAsync(
            x =>
                x.Id == jobId &&
                x.OrganizationId == organizationId,
            cancellationToken);
    if (job is null)
    {
        Console.Error.WriteLine($"JobRun {jobId} was not found.");
        return 3;
    }

    var attempts = await db.JobAttempts
        .IgnoreQueryFilters()
        .Where(x => x.JobRunId == jobId)
        .OrderBy(x => x.AttemptNumber)
        .ToListAsync(cancellationToken);
    var leases = await db.Leases
        .IgnoreQueryFilters()
        .Where(x => x.JobRunId == jobId)
        .OrderBy(x => x.AcquiredAt)
        .ToListAsync(cancellationToken);
    var checkpoints = await db.Checkpoints
        .IgnoreQueryFilters()
        .Where(x => x.JobRunId == jobId)
        .OrderBy(x => x.SavedAt)
        .ToListAsync(cancellationToken);

    WriteJson(new { job, attempts, leases, checkpoints });
    return 0;
});

var jobsRetry = new Command(
    "retry",
    "Retry a failed job through its pending dead-letter record");
jobsRetry.SetAction(_ =>
{
    Console.Error.WriteLine(
        "Direct JobRun state mutation is intentionally disabled. " +
        "Use 'deadletter list' followed by 'deadletter replay --confirm' " +
        "so the retry is claimed, audited, and dispatched through Outbox.");
    return 2;
});
var jobsCommand = new Command("jobs", "Job operations");
jobsCommand.Subcommands.Add(jobsInspect);
jobsCommand.Subcommands.Add(jobsRetry);

var deadletterListOrganizationOption = RequiredGuidOption(
    "--organization",
    "Organization identifier used for tenant isolation.");
var limitOption = new Option<int>("--limit")
{
    Description = "Maximum records to return.",
    DefaultValueFactory = _ => 50
};
var deadletterList = new Command(
    "list",
    "List dead-letter items");
deadletterList.Options.Add(deadletterListOrganizationOption);
deadletterList.Options.Add(limitOption);
deadletterList.SetAction(async (parseResult, cancellationToken) =>
{
    var organizationId = parseResult.GetRequiredValue(
        deadletterListOrganizationOption);
    var limit = Math.Clamp(
        parseResult.GetValue(limitOption),
        1,
        500);
    TenantFilterAccessor.SetOrganizationId(organizationId);
    try
    {
        await using var provider = BuildServiceProvider(
            parseResult.GetValue(connectionStringOption));
        await using var scope = provider.CreateAsyncScope();
        var repository = scope.ServiceProvider
            .GetRequiredService<IDeadLetterRepository>();
        var records = await repository.ListAsync(
            organizationId,
            limit,
            cancellationToken);
        WriteJson(records.Select(x => new
        {
            x.Id,
            x.OriginalMessageId,
            x.MessageType,
            x.CorrelationId,
            x.CausationId,
            x.ErrorCategory,
            x.ErrorMessage,
            x.AttemptCount,
            x.DeadLetteredAt,
            x.Status,
            x.ReplayCount,
            x.ReplayedAt,
            x.ReplayMessageId,
            x.ReplayRequestedBy
        }));
        return 0;
    }
    finally
    {
        TenantFilterAccessor.Clear();
    }
});

var deadletterReplayOrganizationOption = RequiredGuidOption(
    "--organization",
    "Organization identifier used for tenant isolation.");
var deadLetterIdOption = RequiredGuidOption(
    "--id",
    "DeadLetterRecord identifier.");
var operatorOption = new Option<string>("--operator")
{
    Description = "Identity recorded in the immutable audit event.",
    Required = true
};
var confirmOption = new Option<bool>("--confirm")
{
    Description = "Explicitly authorize this high-risk replay."
};
var payloadFileOption = new Option<FileInfo?>("--payload-file")
{
    Description =
        "Optional corrected payload. The original is retained on the " +
        "DeadLetterRecord."
};
var deadletterReplay = new Command(
    "replay",
    "Claim and replay one pending dead-letter item through Outbox");
deadletterReplay.Options.Add(deadletterReplayOrganizationOption);
deadletterReplay.Options.Add(deadLetterIdOption);
deadletterReplay.Options.Add(operatorOption);
deadletterReplay.Options.Add(confirmOption);
deadletterReplay.Options.Add(payloadFileOption);
deadletterReplay.SetAction(async (parseResult, cancellationToken) =>
{
    if (!parseResult.GetValue(confirmOption))
    {
        Console.Error.WriteLine(
            "Replay refused: pass --confirm after reviewing the record.");
        return 2;
    }

    var organizationId = parseResult.GetRequiredValue(
        deadletterReplayOrganizationOption);
    var payloadFile = parseResult.GetValue(payloadFileOption);
    string? replacementPayload = null;
    if (payloadFile is not null)
    {
        replacementPayload = await File.ReadAllTextAsync(
            payloadFile.FullName,
            cancellationToken);
    }

    TenantFilterAccessor.SetOrganizationId(organizationId);
    try
    {
        await using var provider = BuildServiceProvider(
            parseResult.GetValue(connectionStringOption));
        await using var scope = provider.CreateAsyncScope();
        var replayService = scope.ServiceProvider
            .GetRequiredService<DeadLetterReplayService>();
        var result = await replayService.ReplayAsync(
            organizationId,
            parseResult.GetRequiredValue(deadLetterIdOption),
            parseResult.GetRequiredValue(operatorOption),
            replacementPayload,
            cancellationToken);
        WriteJson(result);
        return result.Outcome == DeadLetterReplayOutcome.Replayed
            ? 0
            : 3;
    }
    finally
    {
        TenantFilterAccessor.Clear();
    }
});
var deadletterCommand = new Command(
    "deadletter",
    "Dead-letter operations");
deadletterCommand.Subcommands.Add(deadletterList);
deadletterCommand.Subcommands.Add(deadletterReplay);

rootCommand.Subcommands.Add(diagnosticsCommand);
rootCommand.Subcommands.Add(jobsCommand);
rootCommand.Subcommands.Add(deadletterCommand);

return await rootCommand.Parse(args).InvokeAsync();

static Option<Guid> RequiredGuidOption(
    string name,
    string description)
    => new(name)
    {
        Description = description,
        Required = true
    };

static ServiceProvider BuildServiceProvider(
    string? commandLineConnectionString)
{
    var connectionString = commandLineConnectionString ??
        Environment.GetEnvironmentVariable("CONNECTION_STRING") ??
        "Host=localhost;Port=5432;Database=reliant;" +
        "Username=reliant;Password=reliant-dev";
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:PostgreSQL"] = connectionString
            })
        .Build();
    var services = new ServiceCollection();
    services.AddReliantApplication();
    services.AddReliantInfrastructure(configuration);
    return services.BuildServiceProvider();
}

static void WriteJson(object value)
    => Console.WriteLine(
        JsonSerializer.Serialize(
            value,
            new JsonSerializerOptions
            {
                WriteIndented = true
            }));
