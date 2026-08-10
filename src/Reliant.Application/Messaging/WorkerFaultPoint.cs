namespace Reliant.Application.Messaging;

/// <summary>
/// Points in the processing pipeline where a fault can be injected to simulate
/// worker crashes. Only enabled in test/development - the default injector is a
/// no-op.
/// </summary>
public enum WorkerFaultPoint
{
    BeforeAttemptPersisted,
    AfterAttemptPersisted,
    AfterProviderProcessed,
    BeforeProviderResponseHandled,
    AfterStateUpdated,
    BeforeInboxCommitted,
    AfterInboxCommitted,
    BeforeMessageAck
}

/// <summary>
/// Test/development fault signal that represents abrupt loss of the current
/// worker execution. Provider exception classification must not translate this
/// signal into a provider Timeout or Unknown response.
/// </summary>
public sealed class InjectedWorkerCrashException(
    WorkerFaultPoint faultPoint,
    string contributionId)
    : Exception(
        $"Injected worker crash at {faultPoint} for {contributionId}")
{
    public WorkerFaultPoint FaultPoint { get; } = faultPoint;
    public string ContributionId { get; } = contributionId;
}
