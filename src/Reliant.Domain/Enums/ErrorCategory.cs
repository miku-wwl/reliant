namespace Reliant.Domain.Enums;

public enum ErrorCategory
{
    Timeout = 1,
    RateLimited = 2,
    ServerError = 3,
    NetworkFailure = 4,
    ValidationFailure = 5,
    AuthenticationFailure = 6,
    PermanentBusinessRejection = 7,
    UnknownOutcome = 8
}
