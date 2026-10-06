using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;

namespace Orchestrator.Infrastructure;

/// <summary>Adds the current trace and span ids to every log event (correlates logs with traces).</summary>
public sealed class ActivityEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory pf)
    {
        var a = Activity.Current;
        if (a is null) return;
        logEvent.AddPropertyIfAbsent(pf.CreateProperty("TraceId", a.TraceId.ToString()));
        logEvent.AddPropertyIfAbsent(pf.CreateProperty("SpanId", a.SpanId.ToString()));
        foreach (var tag in new[] { "process.id", "operation.id" })
            if (a.GetTagItem(tag) is string v) logEvent.AddPropertyIfAbsent(pf.CreateProperty(tag == "process.id" ? "ProcessId" : "OperationId", v));
        if (a.GetTagItem("correlation.id") is string c) logEvent.AddPropertyIfAbsent(pf.CreateProperty("CorrelationId", c));
    }
}

public static class ObservabilityExtensions
{
    private static string? Endpoint(IConfiguration cfg) =>
        cfg["OTEL_EXPORTER_OTLP_ENDPOINT"] is { Length: > 0 } e ? e : cfg["Otel:Endpoint"];

    public static LoggerConfiguration WithOrchestratorTelemetry(this LoggerConfiguration lc, IConfiguration cfg, string serviceName)
    {
        lc = lc.Enrich.With<ActivityEnricher>().Enrich.WithProperty("service.name", serviceName);
        if (Endpoint(cfg) is { } endpoint)
            lc = lc.WriteTo.OpenTelemetry(o =>
            {
                o.Endpoint = endpoint.TrimEnd('/') + "/v1/logs";
                o.Protocol = Serilog.Sinks.OpenTelemetry.OtlpProtocol.HttpProtobuf;
                o.ResourceAttributes = new Dictionary<string, object> { ["service.name"] = serviceName };
            });
        return lc;
    }

    /// <summary>Traces and metrics through OTLP (when an endpoint is configured); the sources always exist for in-process listeners.</summary>
    public static IServiceCollection AddOrchestratorTelemetry(this IServiceCollection services, IConfiguration cfg, string serviceName, Action<TracerProviderBuilder>? configureTracing = null)
    {
        var endpoint = Endpoint(cfg);
        var otel = services.AddOpenTelemetry().ConfigureResource(r => r.AddService(serviceName));
        otel.WithTracing(t =>
        {
            t.AddSource(Orchestrator.Application.Telemetry.Name).AddHttpClientInstrumentation();
            configureTracing?.Invoke(t);
            if (endpoint is not null) t.AddOtlpExporter(o => { o.Endpoint = new Uri(endpoint.TrimEnd('/') + "/v1/traces"); o.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf; });
        });
        otel.WithMetrics(m =>
        {
            m.AddMeter(Orchestrator.Application.Telemetry.Name).AddHttpClientInstrumentation();
            if (endpoint is not null) m.AddOtlpExporter(o => { o.Endpoint = new Uri(endpoint.TrimEnd('/') + "/v1/metrics"); o.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf; });
        });
        return services;
    }
}
