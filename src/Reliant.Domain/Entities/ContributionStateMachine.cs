using Reliant.Domain.Enums;

namespace Reliant.Domain.Entities;

public static class ContributionStateMachine
{
    private static readonly Dictionary<ContributionState, HashSet<ContributionState>> _transitions = new()
    {
        [ContributionState.Created] = [ContributionState.Accepted],
        [ContributionState.Accepted] = [ContributionState.Processing],
        [ContributionState.Processing] =
        [
            ContributionState.Succeeded,
            ContributionState.RetryPending,
            ContributionState.ProviderUnknown,
            ContributionState.Failed
        ],
        [ContributionState.RetryPending] = [ContributionState.Processing, ContributionState.Failed],
        [ContributionState.ProviderUnknown] = [ContributionState.ReconciliationPending],
        [ContributionState.ReconciliationPending] = [ContributionState.Succeeded, ContributionState.Failed, ContributionState.RetryPending],
        [ContributionState.Succeeded] = [ContributionState.ReceiptPending],
        [ContributionState.ReceiptPending] = [ContributionState.Completed],
        [ContributionState.Completed] = [],
        [ContributionState.Failed] = []
    };

    public static bool IsValidTransition(ContributionState from, ContributionState to)
    {
        return _transitions.TryGetValue(from, out var allowed) && allowed.Contains(to);
    }

    public static IReadOnlyCollection<ContributionState> GetValidTransitions(ContributionState from)
    {
        return _transitions.TryGetValue(from, out var allowed) ? allowed : Array.Empty<ContributionState>();
    }
}
