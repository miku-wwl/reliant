using Reliant.Domain.Enums;

namespace Reliant.Domain.Entities;

public class InvalidStateTransitionException : Exception
{
    public ContributionState FromState { get; }
    public ContributionState ToState { get; }

    public InvalidStateTransitionException(ContributionState from, ContributionState to)
        : base($"Invalid state transition from {from} to {to}")
    {
        FromState = from;
        ToState = to;
    }
}
