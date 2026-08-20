using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace Reliant.Infrastructure.Observability;

public sealed record DeploymentInfo(
    string ServiceName,
    string Version,
    string Environment,
    string Commit,
    string InstanceId)
{
    public static DeploymentInfo Create(
        IConfiguration configuration,
        string serviceName,
        string environment)
    {
        var entryAssembly = Assembly.GetEntryAssembly();
        var informationalVersion = entryAssembly?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var assemblyVersion = entryAssembly?.GetName().Version?.ToString();

        return new DeploymentInfo(
            serviceName,
            configuration["Deployment:Version"] ??
                informationalVersion ??
                assemblyVersion ??
                "unknown",
            configuration["Deployment:Environment"] ?? environment,
            configuration["Deployment:Commit"] ??
                System.Environment.GetEnvironmentVariable(
                    "DEPLOYMENT_COMMIT") ??
                "local",
            configuration["Deployment:InstanceId"] ??
                System.Environment.GetEnvironmentVariable("HOSTNAME") ??
                System.Environment.MachineName);
    }
}
