namespace Reliant.Domain.Enums;

public enum ReconciliationDifference
{
    None = 1,
    StateMismatch = 2,
    ProviderNotFound = 3,
    ProviderUnavailable = 4
}
