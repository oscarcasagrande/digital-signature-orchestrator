using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Abstractions;
using Orchestrator.Application.Workflow;
using Orchestrator.Domain.Entities;
using Orchestrator.Infrastructure.Persistence;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Orchestrator.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string User { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";
    public int Prefetch { get; set; } = 10;
    public int OutboxBatchSize { get; set; } = 50;
    public int OutboxPollMs { get; set; } = 300;
    /// <summary>Command queues this process consumes (default: signature-provider and artifact).</summary>
    public string[]? Queues { get; set; }

    public ConnectionFactory CreateFactory() => new()
    {
        HostName = Host, Port = Port, UserName = User, Password = Password, VirtualHost = VirtualHost,
        DispatchConsumersAsync = true, AutomaticRecoveryEnabled = true, NetworkRecoveryInterval = TimeSpan.FromSeconds(2)
    };
}

public static class Topology
{
    public const string Exchange = "orchestrator.commands";
    public const string DeadLetterExchange = "orchestrator.dlx";
    public const string ProviderQueue = "signature-provider";
    public const string ProviderDlq = "signature-provider-dlq";

    /// <summary>Declares exchanges and, per integration domain, a command queue and its DLQ. Idempotent.</summary>
    public static void Declare(IModel ch)
    {
        ch.ExchangeDeclare(Exchange, ExchangeType.Direct, durable: true);
        ch.ExchangeDeclare(DeadLetterExchange, ExchangeType.Direct, durable: true);
        foreach (var domain in Orchestrator.Application.Resilience.OperationDomains.All)
        {
            var dlq = Orchestrator.Application.Resilience.OperationDomains.DlqFor(domain);
            ch.QueueDeclare(dlq, durable: true, exclusive: false, autoDelete: false);
            ch.QueueBind(dlq, DeadLetterExchange, dlq);
            ch.QueueDeclare(domain, durable: true, exclusive: false, autoDelete: false, arguments: new Dictionary<string, object>
            {
                ["x-dead-letter-exchange"] = DeadLetterExchange,
                ["x-dead-letter-routing-key"] = dlq
            });
            ch.QueueBind(domain, Exchange, domain);
        }
    }

    /// <summary>DLQ messages go through the dead-letter exchange; everything else through the commands exchange.</summary>
    public static string ExchangeFor(string queue) => queue.EndsWith("-dlq", StringComparison.Ordinal) ? DeadLetterExchange : Exchange;
}

/// <summary>Publishes committed outbox rows to the broker (at-least-once; safe with several instances).</summary>
public sealed class OutboxPublisher(IServiceScopeFactory scopes, IOptions<RabbitMqOptions> options, ILogger<OutboxPublisher> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var conn = options.Value.CreateFactory().CreateConnection("outbox-publisher");
                using var ch = conn.CreateModel();
                Topology.Declare(ch);
                ch.ConfirmSelect();
                log.LogInformation("Outbox publisher connected");
                while (!stop.IsCancellationRequested)
                {
                    var n = await PublishBatchAsync(ch, stop);
                    if (n == 0) await Task.Delay(options.Value.OutboxPollMs, stop);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Outbox publisher error; retrying in 2s");
                try { await Task.Delay(2000, stop); } catch (OperationCanceledException) { }
            }
        }
    }

    public async Task<int> PublishBatchAsync(IModel ch, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.Outbox.FromSqlRaw(
                "SELECT * FROM outbox_event WHERE published_at IS NULL AND available_at <= now() ORDER BY available_at LIMIT {0} FOR UPDATE SKIP LOCKED",
                options.Value.OutboxBatchSize)
            .ToListAsync(ct);
        if (rows.Count == 0) return 0;

        foreach (var row in rows)
        {
            var props = ch.CreateBasicProperties();
            props.MessageId = row.Id;
            props.Type = row.Type;
            props.Persistent = true;
            props.ContentType = "application/json";
            using var doc = JsonDocument.Parse(row.PayloadJson);
            if (doc.RootElement.TryGetProperty("correlationId", out var c) && c.GetString() is { } corr) props.CorrelationId = corr;
            props.Headers = new Dictionary<string, object>();
            if (doc.RootElement.TryGetProperty("traceparent", out var tp) && tp.GetString() is { Length: > 0 } trace)
                props.Headers["traceparent"] = trace;
            if (doc.RootElement.TryGetProperty("causationId", out var ca) && ca.GetString() is { } cause)
                props.Headers["causationId"] = cause;

            ch.BasicPublish(Topology.ExchangeFor(row.Queue), row.Queue, props, Encoding.UTF8.GetBytes(row.PayloadJson));
            ch.WaitForConfirmsOrDie(TimeSpan.FromSeconds(10));
            row.PublishedAt = DateTime.UtcNow;
            row.Attempts++;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return rows.Count;
    }
}

public sealed record OperationMessage(string processId, string operationId, string? correlationId, string? causationId, string? traceparent = null);

/// <summary>Consumes operation commands; duplicates are discarded by the inbox inside the workflow engine.</summary>
public sealed class OperationConsumer(string queue, IServiceScopeFactory scopes, IOptions<RabbitMqOptions> options, ILogger<OperationConsumer> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await WaitForSchemaAsync(stop);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var conn = options.Value.CreateFactory().CreateConnection("operation-consumer-" + queue);
                using var ch = conn.CreateModel();
                Topology.Declare(ch);
                ch.BasicQos(0, (ushort)options.Value.Prefetch, false);
                var consumer = new AsyncEventingBasicConsumer(ch);
                consumer.Received += (_, ea) => HandleAsync(ch, ea, stop);
                ch.BasicConsume(queue, autoAck: false, consumer);
                log.LogInformation("Operation consumer connected to {Queue}", queue);
                var closed = new TaskCompletionSource();
                conn.ConnectionShutdown += (_, _) => closed.TrySetResult();
                using var reg = stop.Register(() => closed.TrySetResult());
                await closed.Task;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Operation consumer error; retrying in 2s");
                try { await Task.Delay(2000, stop); } catch (OperationCanceledException) { }
            }
        }
    }

    private async Task HandleAsync(IModel ch, BasicDeliverEventArgs ea, CancellationToken stop)
    {
        OperationMessage? msg;
        try { msg = JsonSerializer.Deserialize<OperationMessage>(ea.Body.Span); }
        catch (JsonException) { msg = null; }

        if (msg is null || string.IsNullOrEmpty(msg.operationId) || string.IsNullOrEmpty(ea.BasicProperties.MessageId))
        {
            log.LogError("Malformed message rejected to DLQ");
            ch.BasicNack(ea.DeliveryTag, false, false);
            return;
        }

        var parent = msg.traceparent;
        if (string.IsNullOrEmpty(parent) && ea.BasicProperties.Headers?.TryGetValue("traceparent", out var h) == true && h is byte[] raw)
            parent = Encoding.UTF8.GetString(raw);
        using var receive = Orchestrator.Application.Telemetry.StartOperation("operation.receive", parent,
            msg.correlationId ?? msg.processId, msg.processId, msg.operationId, null, null);
        receive?.SetTag("messaging.queue", queue);
        try
        {
            using var scope = scopes.CreateScope();
            var engine = scope.ServiceProvider.GetRequiredService<WorkflowEngine>();
            var outcome = await engine.ExecuteAsync(ea.BasicProperties.MessageId, msg.operationId,
                msg.correlationId ?? msg.processId, msg.causationId, stop);
            log.LogInformation("Operation {OperationId} of {ProcessId}: {Outcome}", msg.operationId, msg.processId, outcome);
            ch.BasicAck(ea.DeliveryTag, false);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Unexpected error handling operation {OperationId}; requeueing", msg.operationId);
            await Task.Delay(1000, CancellationToken.None);
            try { ch.BasicNack(ea.DeliveryTag, false, true); } catch (Exception) { /* channel closed */ }
        }
    }

    private async Task WaitForSchemaAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                if (!(await db.Database.GetPendingMigrationsAsync(stop)).Any()
                    && (await db.Database.GetAppliedMigrationsAsync(stop)).Any()) return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogInformation("Waiting for database schema: {Message}", ex.Message);
            }
            try { await Task.Delay(2000, stop); } catch (OperationCanceledException) { return; }
        }
    }
}

public static class MessagingRegistration
{
    public static readonly string[] DefaultQueues = [Orchestrator.Application.Resilience.OperationDomains.SignatureProvider,
        Orchestrator.Application.Resilience.OperationDomains.Callback,
        Orchestrator.Application.Resilience.OperationDomains.Notification,
        Orchestrator.Application.Resilience.OperationDomains.IdentityProofing,
        Orchestrator.Application.Resilience.OperationDomains.Artifact];

    /// <summary>Registers one consumer per command queue (own connection and channel, so domains stay isolated).</summary>
    public static IServiceCollection AddOperationConsumers(this IServiceCollection s, params string[] queues)
    {
        foreach (var q in queues)
            s.AddSingleton<IHostedService>(sp => new OperationConsumer(q, sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<IOptions<RabbitMqOptions>>(), sp.GetRequiredService<ILogger<OperationConsumer>>()));
        return s;
    }

    public static IServiceCollection AddOperationConsumers(this IServiceCollection s, IConfiguration cfg) =>
        s.AddOperationConsumers(cfg.GetSection("RabbitMq:Queues").Get<string[]>() is { Length: > 0 } q ? q : DefaultQueues);
}
