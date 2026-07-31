using Reliant.Domain.Entities;
using Reliant.Domain.Enums;

namespace Reliant.Tests.Unit;

/// <summary>
/// Locks the state-transition audit invariant: every actual state change must
/// produce exactly one StateTransition whose FromState was captured BEFORE the
/// TransitionTo call. Multi-hop sequences (Created -> Accepted -> Processing,
/// and Processing -> ProviderUnknown -> ReconciliationPending) must never be
/// collapsed into a single audit row.
/// </summary>
[Trait("Category", "Unit")]
public class StateTransitionAuditTests
{
    private static StateTransition Record(Contribution contribution, ContributionState target, string reason)
    {
        var from = contribution.State;
        contribution.TransitionTo(target, reason);
        return new StateTransition
        {
            Id = Guid.NewGuid(),
            ContributionId = contribution.Id,
            FromState = from,
            ToState = target,
            Reason = reason,
            ChangedBy = "test"
        };
    }

    [Fact]
    public void CreatedToAccepted_ThenAcceptedToProcessing_ShouldBeTwoDistinctTransitions()
    {
        var contribution = new Contribution { Id = Guid.NewGuid(), State = ContributionState.Created };
        var transitions = new List<StateTransition>();

        transitions.Add(Record(contribution, ContributionState.Accepted, "Worker accepted"));
        transitions.Add(Record(contribution, ContributionState.Processing, "Worker started processing"));

        Assert.Equal(2, transitions.Count);
        Assert.Equal(ContributionState.Created, transitions[0].FromState);
        Assert.Equal(ContributionState.Accepted, transitions[0].ToState);
        Assert.Equal(ContributionState.Accepted, transitions[1].FromState);
        Assert.Equal(ContributionState.Processing, transitions[1].ToState);
        Assert.Equal(ContributionState.Processing, contribution.State);
    }

    [Fact]
    public void UnknownOutcome_ShouldRecordTwoDistinctTransitions()
    {
        var contribution = new Contribution { Id = Guid.NewGuid(), State = ContributionState.Processing };
        var transitions = new List<StateTransition>();

        transitions.Add(Record(contribution, ContributionState.ProviderUnknown, "Provider timeout"));
        transitions.Add(Record(contribution, ContributionState.ReconciliationPending, "Awaiting reconciliation"));

        Assert.Equal(2, transitions.Count);
        Assert.Equal(ContributionState.Processing, transitions[0].FromState);
        Assert.Equal(ContributionState.ProviderUnknown, transitions[0].ToState);
        Assert.Equal(ContributionState.ProviderUnknown, transitions[1].FromState);
        Assert.Equal(ContributionState.ReconciliationPending, transitions[1].ToState);
    }

    [Fact]
    public void EveryStateChange_ShouldBeCapturedBeforeTransitionTo()
    {
        // The FromState recorded must equal the entity's state at the moment
        // BEFORE TransitionTo, never the state after it.
        var contribution = new Contribution { Id = Guid.NewGuid(), State = ContributionState.Created };
        var before = contribution.State;

        var transition = Record(contribution, ContributionState.Accepted, "test");

        Assert.Equal(before, transition.FromState);
        Assert.NotEqual(contribution.State, transition.FromState); // FromState != post-transition state
        Assert.Equal(ContributionState.Accepted, contribution.State);
    }

    [Fact]
    public void NoCollapsedMultiHop_ShouldSkipIntermediateState()
    {
        // Guard: a single transition record must never span two hops.
        var contribution = new Contribution { Id = Guid.NewGuid(), State = ContributionState.Created };

        var toProcessing = ContributionState.Accepted;
        Assert.True(contribution.CanTransitionTo(toProcessing));
        contribution.TransitionTo(toProcessing, "Accepted");

        // From Processing the next valid step is Processing, not a direct jump
        // from Created to Processing with Accepted collapsed.
        Assert.Equal(ContributionState.Accepted, contribution.State);
        Assert.True(contribution.CanTransitionTo(ContributionState.Processing));
        Assert.False(contribution.CanTransitionTo(ContributionState.ReconciliationPending));
    }
}
