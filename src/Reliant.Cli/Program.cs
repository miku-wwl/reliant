using System.CommandLine;

var rootCommand = new RootCommand("reliantctl - Reliant operational CLI");

var diagnosticsCollect = new Command("collect", "Collect diagnostic information");
diagnosticsCollect.SetAction(_ => { Console.WriteLine("Diagnostics collection not yet implemented."); return Task.CompletedTask; });
var diagnosticsCommand = new Command("diagnostics", "Diagnostic operations");
diagnosticsCommand.Subcommands.Add(diagnosticsCollect);

var jobsInspect = new Command("inspect", "Inspect job status");
jobsInspect.SetAction(_ => { Console.WriteLine("Job inspection - connect to database required."); return Task.CompletedTask; });
var jobsRetry = new Command("retry", "Retry a failed job");
jobsRetry.SetAction(_ => { Console.WriteLine("Job retry - connect to database required."); return Task.CompletedTask; });
var jobsCommand = new Command("jobs", "Job operations");
jobsCommand.Subcommands.Add(jobsInspect);
jobsCommand.Subcommands.Add(jobsRetry);

var deadletterList = new Command("list", "List dead-letter items");
deadletterList.SetAction(_ => { Console.WriteLine("Dead-letter listing - connect to database required."); return Task.CompletedTask; });
var deadletterReplay = new Command("replay", "Replay a dead-letter item");
deadletterReplay.SetAction(_ => { Console.WriteLine("Dead-letter replay - connect to database required."); return Task.CompletedTask; });
var deadletterCommand = new Command("deadletter", "Dead-letter operations");
deadletterCommand.Subcommands.Add(deadletterList);
deadletterCommand.Subcommands.Add(deadletterReplay);

rootCommand.Subcommands.Add(diagnosticsCommand);
rootCommand.Subcommands.Add(jobsCommand);
rootCommand.Subcommands.Add(deadletterCommand);

await rootCommand.Parse(args).InvokeAsync();
