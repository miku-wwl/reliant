namespace Reliant.Domain.Enums;

public enum JobStatus
{
    Pending = 1,
    Running = 2,
    Succeeded = 3,
    Failed = 4,
    DeadLettered = 5,
    Cancelled = 6
}

public enum JobAttemptStatus
{
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Abandoned = 4,
    Deferred = 5
}
