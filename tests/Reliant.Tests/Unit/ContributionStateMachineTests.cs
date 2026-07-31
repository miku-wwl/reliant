using Reliant.Domain.Entities;
using Reliant.Domain.Enums;

namespace Reliant.Tests.Unit;

[Trait("Category", "Unit")]
public class ContributionStateMachineTests
{
    [Theory]
    [InlineData(ContributionState.Created, ContributionState.Accepted)]
    [InlineData(ContributionState.Accepted, ContributionState.Processing)]
    [InlineData(ContributionState.Processing, ContributionState.Succeeded)]
    [InlineData(ContributionState.Processing, ContributionState.RetryPending)]
    [InlineData(ContributionState.Processing, ContributionState.ProviderUnknown)]
    [InlineData(ContributionState.Processing, ContributionState.Failed)]
    [InlineData(ContributionState.RetryPending, ContributionState.Processing)]
    [InlineData(ContributionState.ProviderUnknown, ContributionState.ReconciliationPending)]
    [InlineData(ContributionState.ReconciliationPending, ContributionState.Succeeded)]
    [InlineData(ContributionState.ReconciliationPending, ContributionState.Failed)]
    [InlineData(ContributionState.Succeeded, ContributionState.ReceiptPending)]
    [InlineData(ContributionState.ReceiptPending, ContributionState.Completed)]
    public void ValidTransition_ShouldReturnTrue(ContributionState from, ContributionState to)
    {
        var result = ContributionStateMachine.IsValidTransition(from, to);
        Assert.True(result);
    }

    [Theory]
    [InlineData(ContributionState.Created, ContributionState.Succeeded)]
    [InlineData(ContributionState.Created, ContributionState.Completed)]
    [InlineData(ContributionState.Accepted, ContributionState.Succeeded)]
    [InlineData(ContributionState.Processing, ContributionState.Completed)]
    [InlineData(ContributionState.Completed, ContributionState.Created)]
    [InlineData(ContributionState.Failed, ContributionState.Processing)]
    [InlineData(ContributionState.Succeeded, ContributionState.Failed)]
    [InlineData(ContributionState.Completed, ContributionState.Processing)]
    public void InvalidTransition_ShouldReturnFalse(ContributionState from, ContributionState to)
    {
        var result = ContributionStateMachine.IsValidTransition(from, to);
        Assert.False(result);
    }

    [Fact]
    public void TransitionTo_ShouldUpdateState_WhenValid()
    {
        var contribution = new Contribution { State = ContributionState.Created };

        contribution.TransitionTo(ContributionState.Accepted, "test");

        Assert.Equal(ContributionState.Accepted, contribution.State);
    }

    [Fact]
    public void TransitionTo_ShouldThrow_WhenInvalid()
    {
        var contribution = new Contribution { State = ContributionState.Created };

        Assert.Throws<InvalidStateTransitionException>(
            () => contribution.TransitionTo(ContributionState.Succeeded, "skip"));
    }

    [Fact]
    public void GetValidTransitions_FromCompleted_ShouldBeEmpty()
    {
        var transitions = ContributionStateMachine.GetValidTransitions(ContributionState.Completed);
        Assert.Empty(transitions);
    }

    [Fact]
    public void GetValidTransitions_FromProcessing_ShouldHaveFour()
    {
        var transitions = ContributionStateMachine.GetValidTransitions(ContributionState.Processing);
        Assert.Equal(4, transitions.Count);
    }

    [Fact]
    public void CanTransitionTo_ShouldReturnTrue_ForValid()
    {
        var contribution = new Contribution { State = ContributionState.Accepted };
        Assert.True(contribution.CanTransitionTo(ContributionState.Processing));
    }

    [Fact]
    public void CanTransitionTo_ShouldReturnFalse_ForInvalid()
    {
        var contribution = new Contribution { State = ContributionState.Accepted };
        Assert.False(contribution.CanTransitionTo(ContributionState.Completed));
    }
}
