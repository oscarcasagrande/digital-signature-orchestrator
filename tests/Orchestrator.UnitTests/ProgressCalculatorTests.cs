using FluentAssertions;
using Orchestrator.Application.Processes;
using Orchestrator.Domain.StateMachines;
using Xunit;

namespace Orchestrator.UnitTests;

public class ProgressCalculatorTests
{
    private static SignerFact S(string id, int pos, int? order = null, bool signed = false, bool released = false) => new(id, id.ToUpper(), pos, order, signed, released);
    private static ConfirmationFact C(string signer, string ch, ConfirmationStatus st) => new(signer, ch, st);

    private static ProgressFacts Facts(BusinessStatus b = BusinessStatus.CREATED, bool stored = false, bool docFailed = false,
        SignerFact[]? signers = null, ConfirmationFact[]? confs = null, bool callback = false, CallbackState cb = CallbackState.Pending) =>
        new(b, stored, docFailed, signers ?? [S("a", 0)], confs ?? [], callback, cb);

    [Fact]
    public void Plan_counts_document_confirmations_signatures_final_and_callback()
    {
        var p = ProgressCalculator.Compute(Facts(signers: [S("a", 0), S("b", 1)],
            confs: [C("a", "EMAIL", ConfirmationStatus.PLANNED), C("b", "SMS", ConfirmationStatus.PLANNED)], callback: true));
        p.TotalSteps.Should().Be(1 + 2 + 2 + 1 + 1);
        p.Steps.Select(s => s.Kind).Should().Equal("DOCUMENT_RECEIVED", "CONFIRMATION", "SIGNATURE", "CONFIRMATION", "SIGNATURE", "FINAL_DOCUMENT", "CALLBACK");
        p.CompletedSteps.Should().Be(0);
    }

    [Fact]
    public void Legacy_process_without_confirmations_or_callback_has_document_signature_and_final()
    {
        ProgressCalculator.Compute(Facts()).TotalSteps.Should().Be(3);
    }

    [Fact]
    public void Current_step_is_the_first_unfinished_and_advances()
    {
        var f = Facts(BusinessStatus.SIGNATURE_IN_PROGRESS, stored: true, signers: [S("a", 0, released: true)],
            confs: [C("a", "EMAIL", ConfirmationStatus.SENT)]);
        var p = ProgressCalculator.Compute(f);
        p.CompletedSteps.Should().Be(1);
        p.CurrentStep!.Kind.Should().Be("CONFIRMATION");
        p.CurrentStep.Channel.Should().Be("EMAIL");
        p.CurrentStep.SignerId.Should().Be("a");
        p.CurrentStep.Status.Should().Be("IN_PROGRESS");

        p = ProgressCalculator.Compute(f with { Confirmations = [C("a", "EMAIL", ConfirmationStatus.CONFIRMED)] });
        p.CompletedSteps.Should().Be(2);
        p.CurrentStep!.Kind.Should().Be("SIGNATURE");
    }

    [Fact]
    public void Completed_process_reaches_total_with_no_current_step()
    {
        var p = ProgressCalculator.Compute(Facts(BusinessStatus.COMPLETED, true, signers: [S("a", 0, signed: true)],
            confs: [C("a", "EMAIL", ConfirmationStatus.CONFIRMED)], callback: true, cb: CallbackState.Delivered));
        (p.CompletedSteps, p.TotalSteps).Should().Be((5, 5));
        p.CurrentStep.Should().BeNull();
    }

    [Fact]
    public void Completed_process_waits_for_the_callback_delivery()
    {
        var p = ProgressCalculator.Compute(Facts(BusinessStatus.COMPLETED, true, signers: [S("a", 0, signed: true)], callback: true));
        (p.CompletedSteps, p.TotalSteps).Should().Be((3, 4));
        p.CurrentStep!.Kind.Should().Be("CALLBACK");
    }

    [Fact]
    public void Failed_callback_is_not_completed()
    {
        var p = ProgressCalculator.Compute(Facts(BusinessStatus.COMPLETED, true, signers: [S("a", 0, signed: true)], callback: true, cb: CallbackState.Failed));
        p.CompletedSteps.Should().Be(3);
        p.Steps[^1].Status.Should().Be("FAILED");
    }

    [Fact]
    public void Cancelled_process_keeps_completed_steps_and_cancels_the_rest()
    {
        var p = ProgressCalculator.Compute(Facts(BusinessStatus.CANCELLED, true, signers: [S("a", 0)],
            confs: [C("a", "EMAIL", ConfirmationStatus.SENT)], callback: true));
        p.CompletedSteps.Should().Be(1);
        p.Steps.Skip(1).Should().OnlyContain(s => s.Status == "CANCELLED");
        p.CurrentStep.Should().BeNull();
    }

    [Fact]
    public void Failed_process_marks_the_first_unfinished_step_as_failed_and_does_not_count_it()
    {
        var p = ProgressCalculator.Compute(Facts(BusinessStatus.FAILED, stored: false, docFailed: true));
        p.CompletedSteps.Should().Be(0);
        p.Steps[0].Status.Should().Be("FAILED");
        p.Steps.Skip(1).Should().OnlyContain(s => s.Status == "CANCELLED");
    }

    [Fact]
    public void Rejection_fails_the_pending_signatures()
    {
        var p = ProgressCalculator.Compute(Facts(BusinessStatus.REJECTED, true, signers: [S("a", 0, signed: true), S("b", 1)]));
        p.CompletedSteps.Should().Be(2);
        p.Steps.Single(s => s.Kind == "SIGNATURE" && s.SignerId == "b").Status.Should().Be("FAILED");
    }

    [Fact]
    public void Locked_confirmation_is_failed_but_still_the_current_step()
    {
        var p = ProgressCalculator.Compute(Facts(BusinessStatus.SIGNATURE_IN_PROGRESS, true, confs: [C("a", "SMS", ConfirmationStatus.LOCKED)]));
        p.CompletedSteps.Should().Be(1);
        p.CurrentStep!.Status.Should().Be("FAILED");
    }

    [Fact]
    public void Sequential_signers_are_planned_in_order()
    {
        var p = ProgressCalculator.Compute(Facts(signers: [S("a", 0, order: 2), S("b", 1, order: 1)]));
        p.Steps.Where(s => s.Kind == "SIGNATURE").Select(s => s.SignerId).Should().Equal("b", "a");
    }

    [Fact]
    public void Document_failure_never_counts_as_completed_while_the_process_is_alive()
    {
        var p = ProgressCalculator.Compute(Facts(BusinessStatus.CREATED, docFailed: true));
        p.CompletedSteps.Should().Be(0);
        p.CurrentStep!.Status.Should().Be("FAILED");
    }
}
