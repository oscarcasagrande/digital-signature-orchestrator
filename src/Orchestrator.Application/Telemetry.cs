using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Orchestrator.Application;

/// <summary>Traces and metrics of the platform (OpenTelemetry source/meter name: "Orchestrator").</summary>
public static class Telemetry
{
    public const string Name = "Orchestrator";

    public static readonly ActivitySource Source = new(Name);
    public static readonly Meter Meter = new(Name);

    public static readonly Counter<long> ProcessCreated = Meter.CreateCounter<long>("orchestrator.process.created", description: "Signature processes created");
    public static readonly Counter<long> ProcessCompleted = Meter.CreateCounter<long>("orchestrator.process.completed", description: "Signature processes completed");
    public static readonly Counter<long> ProcessFailed = Meter.CreateCounter<long>("orchestrator.process.failed", description: "Signature processes failed, rejected or expired");
    public static readonly Histogram<double> CompletionTime = Meter.CreateHistogram<double>("orchestrator.process.completion_time", "s", "Seconds from creation to completion");
    public static readonly Histogram<double> ProviderLatency = Meter.CreateHistogram<double>("orchestrator.provider.latency", "ms", "Signature provider call latency");
    public static readonly Counter<long> ProviderErrors = Meter.CreateCounter<long>("orchestrator.provider.errors", description: "Signature provider call errors");
    public static readonly Counter<long> OperationRetries = Meter.CreateCounter<long>("orchestrator.operation.retries", description: "Operation retries scheduled");
    public static readonly Counter<long> CallbackDeliveries = Meter.CreateCounter<long>("orchestrator.callback.deliveries", description: "Callback delivery attempts by result");
    public static readonly Counter<long> ReconciliationRuns = Meter.CreateCounter<long>("orchestrator.reconciliation.runs", description: "Reconciliations by outcome");
    public static readonly Counter<long> ProofingFailures = Meter.CreateCounter<long>("orchestrator.proofing.failures", description: "Identity validations that failed or were rejected");
    public static readonly Counter<long> SecurityDenied = Meter.CreateCounter<long>("orchestrator.security.denied", description: "Requests denied by authentication or authorization");

    private static long _deadLetterSize;
    public static void SetDeadLetterSize(long value) => Interlocked.Exchange(ref _deadLetterSize, value);

    // ReSharper disable once UnusedMember.Global
    public static readonly ObservableGauge<long> DeadLetterSize =
        Meter.CreateObservableGauge("orchestrator.deadletter.size", () => Interlocked.Read(ref _deadLetterSize), description: "Pending dead letters");

    public static Activity? StartOperation(string name, string? parent, string correlationId, string processId, string operationId, string? operationType, string? providerId)
    {
        ActivityContext.TryParse(parent, null, out var ctx);
        var activity = Source.StartActivity(name, ActivityKind.Consumer, ctx);
        if (activity is null) return null;
        activity.SetTag("correlation.id", correlationId);
        activity.SetTag("process.id", processId);
        activity.SetTag("operation.id", operationId);
        if (operationType is not null) activity.SetTag("operation.type", operationType);
        if (providerId is not null) activity.SetTag("provider.id", providerId);
        return activity;
    }
}
