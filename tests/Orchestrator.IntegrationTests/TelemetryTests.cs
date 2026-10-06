using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Xunit;

namespace Orchestrator.IntegrationTests;

/// <summary>Collects spans and metrics of the in-process hosts (API, outbox publisher and consumers).</summary>
internal sealed class TelemetryCollector : IDisposable
{
    public ConcurrentBag<Activity> Spans { get; } = new();
    public ConcurrentDictionary<string, double> Totals { get; } = new();
    public ConcurrentBag<string> Instruments { get; } = new();
    private readonly ActivityListener _activities;
    private readonly MeterListener _meters = new();

    public TelemetryCollector()
    {
        _activities = new ActivityListener
        {
            ShouldListenTo = s => s.Name is "Orchestrator" or "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => Spans.Add(a)
        };
        ActivitySource.AddActivityListener(_activities);
        _meters.InstrumentPublished = (i, l) =>
        {
            if (i.Meter.Name != "Orchestrator") return;
            Instruments.Add(i.Name);
            l.EnableMeasurementEvents(i);
        };
        _meters.SetMeasurementEventCallback<long>((i, v, _, _) => Totals.AddOrUpdate(i.Name, v, (_, o) => o + v));
        _meters.SetMeasurementEventCallback<double>((i, v, _, _) => Totals.AddOrUpdate(i.Name, 1, (_, o) => o + 1));
        _meters.Start();
    }

    public async Task<double> WaitForAsync(string instrument, double atLeast = 1, int seconds = 20)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            _meters.RecordObservableInstruments();
            if (Totals.TryGetValue(instrument, out var v) && v >= atLeast) return v;
            await Task.Delay(100);
        }
        return Totals.TryGetValue(instrument, out var last) ? last : 0;
    }

    public void Dispose() { _activities.Dispose(); _meters.Dispose(); }
}

[Collection("integration")]
public class TelemetryTests(TestFixture f)
{
    [Fact]
    public async Task One_trace_connects_the_request_and_the_worker_operations_with_correlation_tags()
    {
        using var t = new TelemetryCollector();
        var traceId = ActivityTraceId.CreateRandom();
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/signature-processes")
        {
            Content = new StringContent(TestFixture.Payload("TRACE-" + Guid.NewGuid().ToString("N")[..6]), Encoding.UTF8, "application/json")
        };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        req.Headers.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");
        req.Headers.Add("X-Correlation-Id", "corr-trace-test");
        var resp = await f.Client.SendAsync(req);
        var id = (await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("processId").GetString()!;
        await f.WaitForStatusAsync(id, "COMPLETED");
        await Task.Delay(300);

        var executes = t.Spans.Where(a => a.OperationName == "operation.execute" && (string?)a.GetTagItem("process.id") == id).ToList();
        executes.Should().NotBeEmpty("the worker executes operations of the process");
        executes.Should().OnlyContain(a => a.TraceId == traceId, "the trace context travels request -> outbox -> broker -> consumer -> next operations");
        executes.Select(a => (string?)a.GetTagItem("operation.type")).Should().Contain(["DOCUMENT_DOWNLOAD", "PROVIDER_CREATE_PROCESS"]);
        foreach (var a in executes)
        {
            a.GetTagItem("operation.id").Should().BeOfType<string>().Which.Should().StartWith("op_");
            a.GetTagItem("provider.id").Should().Be("SIMULATED");
        }
        var receives = t.Spans.Where(a => a.OperationName == "operation.receive" && (string?)a.GetTagItem("process.id") == id).ToList();
        receives.Should().NotBeEmpty();
        receives.Should().OnlyContain(a => a.TraceId == traceId && (string?)a.GetTagItem("correlation.id") != null);
        t.Spans.Any(a => a.OperationName.StartsWith("provider.") && a.TraceId == traceId).Should().BeTrue("provider calls are child spans");
    }

    [Fact]
    public async Task Minimum_metrics_exist_and_move_with_a_full_flow()
    {
        using var t = new TelemetryCollector();
        var key = "tel-" + Guid.NewGuid().ToString("N")[..6];
        var ok = await f.CreateProcessAsync("TEL-OK-" + key, callbackJson: f.DynamicCallback(key));
        var flaky = await f.CreateProcessAsync("SIM-FLAKY-1-" + key);
        var rejected = await f.CreateProcessAsync("SIM-REJECT-" + key);
        var pending = await f.CreateProcessAsync("TEL-REC-" + key);
        await f.Client.PostAsync($"/v1/signature-processes/{pending}/reconcile", null);
        await f.WaitForStatusAsync(ok, "COMPLETED");
        await f.WaitForStatusAsync(flaky, "COMPLETED");
        await f.WaitForStatusAsync(rejected, "REJECTED");

        foreach (var name in new[]
                 {
                     "orchestrator.process.created", "orchestrator.process.completed", "orchestrator.process.failed", "orchestrator.process.completion_time",
                     "orchestrator.provider.latency", "orchestrator.provider.errors", "orchestrator.operation.retries", "orchestrator.deadletter.size",
                     "orchestrator.callback.deliveries", "orchestrator.reconciliation.runs", "orchestrator.proofing.failures", "orchestrator.security.denied"
                 })
            t.Instruments.Should().Contain(name);

        (await t.WaitForAsync("orchestrator.process.created", 4)).Should().BeGreaterThanOrEqualTo(4);
        (await t.WaitForAsync("orchestrator.process.completed", 2)).Should().BeGreaterThanOrEqualTo(2);
        (await t.WaitForAsync("orchestrator.process.failed", 1)).Should().BeGreaterThanOrEqualTo(1);
        (await t.WaitForAsync("orchestrator.process.completion_time", 2)).Should().BeGreaterThanOrEqualTo(2);
        (await t.WaitForAsync("orchestrator.provider.latency", 4)).Should().BeGreaterThanOrEqualTo(4);
        (await t.WaitForAsync("orchestrator.provider.errors", 1)).Should().BeGreaterThanOrEqualTo(1);
        (await t.WaitForAsync("orchestrator.operation.retries", 1)).Should().BeGreaterThanOrEqualTo(1);
        (await t.WaitForAsync("orchestrator.callback.deliveries", 1)).Should().BeGreaterThanOrEqualTo(1);
        (await t.WaitForAsync("orchestrator.reconciliation.runs", 1)).Should().BeGreaterThanOrEqualTo(1);
    }
}
