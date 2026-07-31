namespace Reliant.Domain.Enums;

public enum ContributionState
{
    Created = 1,
    Accepted = 2,
    Processing = 3,
    Succeeded = 4,
    ReceiptPending = 5,
    Completed = 6,
    RetryPending = 7,
    Failed = 8,
    ProviderUnknown = 9,
    ReconciliationPending = 10
}
