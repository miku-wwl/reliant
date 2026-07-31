using Reliant.Application.Messaging;

namespace Reliant.Tests.TestHelpers;

/// <summary>
/// A fault injector that throws (simulating a worker crash) exactly once at the
/// configured point. After the first injection it becomes a no-op, so a
/// subsequent submission can be used to simulate recovery against the same
/// provider instance.
/// </summary>
public class ThrowingFaultInjector : IWorkerFaultInjector
{
    private readonly WorkerFaultPoint _throwAt;
    private int _thrown;

    public ThrowingFaultInjector(WorkerFaultPoint throwAt)
    {
        _throwAt = throwAt;
    }

    public void Inject(WorkerFaultPoint point, string contributionId)
    {
        if (point == _throwAt && Interlocked.CompareExchange(ref _thrown, 1, 0) == 0)
        {
            throw new InvalidOperationException($"Simulated crash at {point} for {contributionId}");
        }
    }
}
