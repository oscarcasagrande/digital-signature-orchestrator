using FluentAssertions;
using Orchestrator.Domain.Entities;
using Orchestrator.Domain.StateMachines;
using Xunit;

namespace Orchestrator.UnitTests;

public class StateMachineTests
{
    [Theory]
    [InlineData(BusinessStatus.CREATED, BusinessStatus.DOCUMENT_RECEIVED)]
    [InlineData(BusinessStatus.DOCUMENT_RECEIVED, BusinessStatus.VALIDATING)]
    [InlineData(BusinessStatus.VALIDATING, BusinessStatus.READY_FOR_SIGNATURE)]
    [InlineData(BusinessStatus.READY_FOR_SIGNATURE, BusinessStatus.SIGNATURE_IN_PROGRESS)]
    [InlineData(BusinessStatus.SIGNATURE_IN_PROGRESS, BusinessStatus.PARTIALLY_SIGNED)]
    [InlineData(BusinessStatus.SIGNATURE_IN_PROGRESS, BusinessStatus.SIGNED)]
    [InlineData(BusinessStatus.PARTIALLY_SIGNED, BusinessStatus.SIGNED)]
    [InlineData(BusinessStatus.SIGNED, BusinessStatus.FINALIZING)]
    [InlineData(BusinessStatus.FINALIZING, BusinessStatus.COMPLETED)]
    [InlineData(BusinessStatus.SIGNATURE_IN_PROGRESS, BusinessStatus.REJECTED)]
    [InlineData(BusinessStatus.VALIDATING, BusinessStatus.CANCELLED)]
    [InlineData(BusinessStatus.SIGNED, BusinessStatus.FAILED)]
    [InlineData(BusinessStatus.CREATED, BusinessStatus.EXPIRED)]
    public void Valid_business_transitions_are_allowed(BusinessStatus from, BusinessStatus to) =>
        BusinessStateMachine.CanTransition(from, to).Should().BeTrue();

    [Theory]
    [InlineData(BusinessStatus.COMPLETED, BusinessStatus.SIGNED)]
    [InlineData(BusinessStatus.COMPLETED, BusinessStatus.CANCELLED)]
    [InlineData(BusinessStatus.CANCELLED, BusinessStatus.CREATED)]
    [InlineData(BusinessStatus.FAILED, BusinessStatus.VALIDATING)]
    [InlineData(BusinessStatus.CREATED, BusinessStatus.SIGNED)]
    [InlineData(BusinessStatus.VALIDATING, BusinessStatus.REJECTED)]
    [InlineData(BusinessStatus.SIGNED, BusinessStatus.SIGNATURE_IN_PROGRESS)]
    public void Invalid_business_transitions_are_rejected(BusinessStatus from, BusinessStatus to)
    {
        BusinessStateMachine.CanTransition(from, to).Should().BeFalse();
        var act = () => BusinessStateMachine.Ensure(from, to);
        act.Should().Throw<InvalidTransitionException>();
    }

    [Theory]
    [InlineData(BusinessStatus.COMPLETED)]
    [InlineData(BusinessStatus.FAILED)]
    [InlineData(BusinessStatus.CANCELLED)]
    [InlineData(BusinessStatus.EXPIRED)]
    [InlineData(BusinessStatus.REJECTED)]
    public void Terminal_states_are_terminal(BusinessStatus s) => BusinessStateMachine.IsTerminal(s).Should().BeTrue();

    [Theory]
    [InlineData(OperationalStatus.READY, OperationalStatus.PROCESSING, true)]
    [InlineData(OperationalStatus.PROCESSING, OperationalStatus.RETRY_PENDING, true)]
    [InlineData(OperationalStatus.RETRY_PENDING, OperationalStatus.PROCESSING, true)]
    [InlineData(OperationalStatus.PROCESSING, OperationalStatus.DLQ, true)]
    [InlineData(OperationalStatus.MANUAL_ACTION, OperationalStatus.READY, true)]
    [InlineData(OperationalStatus.READY, OperationalStatus.DLQ, false)]
    [InlineData(OperationalStatus.SUSPENDED, OperationalStatus.PROCESSING, false)]
    [InlineData(OperationalStatus.DLQ, OperationalStatus.RETRY_PENDING, false)]
    public void Operational_transitions(OperationalStatus from, OperationalStatus to, bool allowed) =>
        OperationalStateMachine.CanTransition(from, to).Should().Be(allowed);

    [Fact]
    public void Business_and_operational_states_are_independent()
    {
        var p = new SignatureProcess { BusinessStatus = BusinessStatus.SIGNED };
        p.TransitionOperational(OperationalStatus.PROCESSING, DateTime.UtcNow);
        p.TransitionOperational(OperationalStatus.RETRY_PENDING, DateTime.UtcNow);
        p.BusinessStatus.Should().Be(BusinessStatus.SIGNED);
        p.OperationalStatus.Should().Be(OperationalStatus.RETRY_PENDING);
    }

    [Fact]
    public void Completing_sets_completed_at()
    {
        var p = new SignatureProcess { BusinessStatus = BusinessStatus.FINALIZING };
        var now = DateTime.UtcNow;
        p.TransitionBusiness(BusinessStatus.COMPLETED, now);
        p.CompletedAt.Should().Be(now);
    }
}
