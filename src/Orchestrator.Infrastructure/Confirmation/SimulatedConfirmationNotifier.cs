using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Confirmation;
using Orchestrator.Application.Providers;
using Orchestrator.Application.Security;
using Orchestrator.Domain.Entities;

namespace Orchestrator.Infrastructure.Confirmation;

/// <summary>
/// Simulated notification capability: instead of calling a messaging vendor it records the message in the sink table
/// (queried by tests and the development endpoint). Failure injection by destination: e-mail local part
/// <c>sim-down</c> always fails (transient), <c>sim-flaky-N</c> fails the first N attempts, <c>sim-perm</c> fails permanently;
/// phones starting with <c>+550000</c> always fail (transient). The code is written to the sink only, never to logs.
/// </summary>
public sealed partial class SimulatedConfirmationNotifier(IOrchestratorDb db, IClock clock, ILogger<SimulatedConfirmationNotifier> log) : IConfirmationNotifier
{
    [GeneratedRegex(@"^sim-flaky-(\d+)")]
    private static partial Regex Flaky();

    public Task SendAsync(ConfirmationMessage m, CancellationToken ct)
    {
        var local = m.Destination.Split('@')[0];
        if (local.StartsWith("sim-down", StringComparison.Ordinal) || m.Destination.StartsWith("+550000", StringComparison.Ordinal))
            throw new OperationException("Simulated notification outage", transient: true);
        if (local.StartsWith("sim-perm", StringComparison.Ordinal))
            throw new OperationException("Simulated permanent notification failure", transient: false);
        if (Flaky().Match(local) is { Success: true } f && m.Attempt <= int.Parse(f.Groups[1].Value))
            throw new OperationException($"Simulated transient notification failure (attempt {m.Attempt})", transient: true);

        db.NotificationSink.Add(new NotificationSinkEntry
        {
            Id = Ids.New("ntf"), ProcessId = m.ProcessId, SignerId = m.SignerId, Channel = m.Channel,
            Destination = m.Destination, Code = m.Code, CreatedAt = clock.UtcNow
        });
        log.LogInformation("Simulated notification delivered: process {ProcessId} signer {SignerId} channel {Channel} to {Destination}",
            m.ProcessId, m.SignerId, m.Channel, m.Channel == "EMAIL" ? SensitiveMasker.MaskEmail(m.Destination) : SensitiveMasker.MaskPhone(m.Destination));
        return Task.CompletedTask;
    }
}
