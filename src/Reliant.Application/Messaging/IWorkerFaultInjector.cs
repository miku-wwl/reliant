namespace Reliant.Application.Messaging;

/// <summary>
/// Fault injection hook for crash-recovery testing. Implementations may throw
/// at a given point to simulate a worker crash mid-pipeline. Default is a
/// no-op; production never injects faults.
/// </summary>
public interface IWorkerFaultInjector
{
    void Inject(WorkerFaultPoint point, string contributionId);
}

public sealed class NoopWorkerFaultInjector : IWorkerFaultInjector
{
    public void Inject(WorkerFaultPoint point, string contributionId)
    {
    }
}
