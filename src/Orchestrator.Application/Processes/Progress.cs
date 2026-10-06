using Orchestrator.Domain.StateMachines;

namespace Orchestrator.Application.Processes;

public static class StepKinds
{
    public const string DocumentReceived = "DOCUMENT_RECEIVED", Confirmation = "CONFIRMATION", Signature = "SIGNATURE",
        FinalDocument = "FINAL_DOCUMENT", Callback = "CALLBACK";
}

public static class StepStatuses
{
    public const string Pending = "PENDING", InProgress = "IN_PROGRESS", Completed = "COMPLETED", Failed = "FAILED", Cancelled = "CANCELLED";
}

public sealed record StepDto(int Order, string Kind, string? SignerId, string? Channel, string Label, string Status);

public sealed record ProgressDto(int CompletedSteps, int TotalSteps, StepDto? CurrentStep);

public sealed record ProgressDetailDto(int CompletedSteps, int TotalSteps, StepDto? CurrentStep, IReadOnlyList<StepDto> Steps);

public enum CallbackState { Pending, Delivered, Failed }

public sealed record SignerFact(string Id, string Name, int Position, int? Order, bool Signed, bool Released);

public sealed record ConfirmationFact(string SignerId, string Channel, ConfirmationStatus Status);

/// <summary>Everything the progress depends on, already read from storage.</summary>
public sealed record ProgressFacts(
    BusinessStatus Business, bool DocumentStored, bool DocumentFailed, IReadOnlyList<SignerFact> Signers,
    IReadOnlyList<ConfirmationFact> Confirmations, bool HasCallback, CallbackState FinalCallback);

/// <summary>
/// Derives the planned steps of a process and their state from facts (pure function). The plan is fixed at creation:
/// document received, per signer (confirmation per channel, then signature), final document and, when configured, the final callback.
/// Cancelled and failed steps never count as completed.
/// </summary>
public static class ProgressCalculator
{
    public static ProgressDetailDto Compute(ProgressFacts f)
    {
        var steps = new List<(string Kind, string? SignerId, string? Channel, string Label)>
        {
            (StepKinds.DocumentReceived, null, null, "Documento recebido")
        };
        var ordered = f.Signers.OrderBy(s => s.Order ?? 0).ThenBy(s => s.Position).ToList();
        foreach (var s in ordered)
        {
            foreach (var c in f.Confirmations.Where(c => c.SignerId == s.Id))
                steps.Add((StepKinds.Confirmation, s.Id, c.Channel, $"Confirmação por {Channel(c.Channel)} - {s.Name}"));
            steps.Add((StepKinds.Signature, s.Id, null, $"Assinatura - {s.Name}"));
        }
        steps.Add((StepKinds.FinalDocument, null, null, "Documento final"));
        if (f.HasCallback) steps.Add((StepKinds.Callback, null, null, "Callback"));

        var closed = f.Business is BusinessStatus.FAILED or BusinessStatus.CANCELLED or BusinessStatus.EXPIRED or BusinessStatus.REJECTED;
        var gated = f.Signers.Any(s => s.Order is not null) || f.Confirmations.Count > 0;
        var failedUsed = false;

        var result = new List<StepDto>();
        for (var i = 0; i < steps.Count; i++)
        {
            var (kind, signerId, channel, label) = steps[i];
            var signer = signerId is null ? null : f.Signers.First(s => s.Id == signerId);
            var conf = kind == StepKinds.Confirmation ? f.Confirmations.First(c => c.SignerId == signerId && c.Channel == channel) : null;

            var done = kind switch
            {
                StepKinds.DocumentReceived => f.DocumentStored,
                StepKinds.Confirmation => conf!.Status == ConfirmationStatus.CONFIRMED,
                StepKinds.Signature => signer!.Signed,
                StepKinds.FinalDocument => f.Business == BusinessStatus.COMPLETED,
                _ => f.FinalCallback == CallbackState.Delivered
            };

            string status;
            if (done) status = StepStatuses.Completed;
            else if (kind == StepKinds.DocumentReceived && f.DocumentFailed) status = StepStatuses.Failed;
            else if (kind == StepKinds.Confirmation && conf!.Status == ConfirmationStatus.LOCKED) status = StepStatuses.Failed;
            else if (kind == StepKinds.Callback && f.FinalCallback == CallbackState.Failed) status = StepStatuses.Failed;
            else if (closed)
            {
                // The first unfinished step of a FAILED process is the one that failed; a rejection fails the pending signatures.
                if ((f.Business == BusinessStatus.FAILED && !failedUsed) || (f.Business == BusinessStatus.REJECTED && kind == StepKinds.Signature))
                { status = StepStatuses.Failed; failedUsed = true; }
                else status = StepStatuses.Cancelled;
            }
            else status = IsRunning(kind, f, signer, conf, gated) ? StepStatuses.InProgress : StepStatuses.Pending;

            if (status == StepStatuses.Failed) failedUsed = true;
            result.Add(new StepDto(i + 1, kind, signerId, channel, label, status));
        }

        var completed = result.Count(s => s.Status == StepStatuses.Completed);
        StepDto? current = closed ? null : result.FirstOrDefault(s => s.Status != StepStatuses.Completed);
        if (current is not null && current.Status == StepStatuses.Pending)
            current = current with { Status = StepStatuses.InProgress };
        return new ProgressDetailDto(completed, result.Count, current, result);
    }

    private static bool IsRunning(string kind, ProgressFacts f, SignerFact? signer, ConfirmationFact? conf, bool gated) => kind switch
    {
        StepKinds.DocumentReceived => !f.DocumentStored,
        StepKinds.Confirmation => conf!.Status is ConfirmationStatus.SENDING or ConfirmationStatus.SENT,
        StepKinds.Signature => gated ? signer!.Released : f.Business is BusinessStatus.SIGNATURE_IN_PROGRESS or BusinessStatus.PARTIALLY_SIGNED,
        StepKinds.FinalDocument => f.Business is BusinessStatus.SIGNED or BusinessStatus.FINALIZING,
        _ => f.Business == BusinessStatus.COMPLETED
    };

    private static string Channel(string c) => c switch { "EMAIL" => "e-mail", "SMS" => "SMS", "WHATSAPP" => "WhatsApp", _ => c };

    public static ProgressDto Summary(ProgressDetailDto d) => new(d.CompletedSteps, d.TotalSteps, d.CurrentStep);
}
