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
