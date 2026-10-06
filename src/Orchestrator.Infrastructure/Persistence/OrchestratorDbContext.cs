using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Abstractions;
using Orchestrator.Domain.Entities;
using Orchestrator.Domain.StateMachines;

namespace Orchestrator.Infrastructure.Persistence;

public class OrchestratorDbContext(DbContextOptions<OrchestratorDbContext> options) : DbContext(options), IOrchestratorDb
{
    public DbSet<SignatureProcess> Processes => Set<SignatureProcess>();
    public DbSet<Signer> Signers => Set<Signer>();
    public DbSet<Operation> Operations => Set<Operation>();
    public DbSet<JournalEvent> Journal => Set<JournalEvent>();
    public DbSet<OutboxEvent> Outbox => Set<OutboxEvent>();
    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();
    public DbSet<IdempotencyRecord> Idempotency => Set<IdempotencyRecord>();
    public DbSet<ProviderProcess> ProviderProcesses => Set<ProviderProcess>();
    public DbSet<Artifact> Artifacts => Set<Artifact>();
    public DbSet<DeadLetterEntry> DeadLetters => Set<DeadLetterEntry>();
    public DbSet<CallbackRegistration> CallbackRegistrations => Set<CallbackRegistration>();
    public DbSet<CallbackDelivery> CallbackDeliveries => Set<CallbackDelivery>();
    public DbSet<ProofingSession> ProofingSessions => Set<ProofingSession>();
    public DbSet<IdentityValidation> IdentityValidations => Set<IdentityValidation>();
    public DbSet<ReconciliationRecord> ReconciliationRecords => Set<ReconciliationRecord>();
    public DbSet<SignerConfirmation> Confirmations => Set<SignerConfirmation>();
    public DbSet<DocumentUpload> Uploads => Set<DocumentUpload>();
    public DbSet<NotificationSinkEntry> NotificationSink => Set<NotificationSinkEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<SignatureProcess>(e =>
        {
            e.ToTable("signature_process");
            e.HasKey(x => x.Id);
            e.Property(x => x.BusinessStatus).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.OperationalStatus).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.ExternalId).HasMaxLength(200);
            e.Property(x => x.SignatureType).HasMaxLength(20);
            e.Property(x => x.DocumentFileName).HasMaxLength(300);
            e.Property(x => x.RequestJson).HasColumnType("jsonb");
            e.Property(x => x.CallbackJson).HasColumnType("jsonb");
            e.Property(x => x.IdentityValidationsJson).HasColumnType("jsonb");
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasMany(x => x.Signers).WithOne().HasForeignKey(x => x.ProcessId);
            e.Ignore(x => x.Operations); // operations also belong to proofing sessions, so there is no FK to this table
            e.HasIndex(x => x.ExternalId);
            e.HasIndex(x => x.CreatedAt);
        });
        b.Entity<Signer>(e =>
        {
            e.ToTable("signer");
            e.HasKey(x => x.Id);
            e.Property(x => x.ExternalId).HasMaxLength(200);
            e.Property(x => x.Name).HasMaxLength(300);
            e.Property(x => x.Document).HasMaxLength(11);
            e.Property(x => x.Email).HasMaxLength(254);
            e.Property(x => x.Phone).HasMaxLength(20);
            e.Property(x => x.SignatureType).HasMaxLength(20);
            e.Property(x => x.ConfirmationChannels).HasMaxLength(40);
            e.Ignore(x => x.Channels);
            e.Property(x => x.Order).HasColumnName("sign_order");
        });
        b.Entity<Operation>(e =>
        {
            e.ToTable("operation");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(60);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.ErrorClass).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.InputJson).HasColumnType("jsonb");
            e.Property(x => x.OutputJson).HasColumnType("jsonb");
            e.Property(x => x.ErrorJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.ProcessId, x.Type, x.Sequence }).IsUnique();
        });
        b.Entity<JournalEvent>(e =>
        {
            e.ToTable("journal_event");
            e.HasKey(x => x.Id);
            e.Property(x => x.Seq).ValueGeneratedOnAdd().UseIdentityByDefaultColumn();
            e.Property(x => x.MetadataJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.ProcessId, x.Seq });
        });
        b.Entity<OutboxEvent>(e =>
        {
            e.ToTable("outbox_event");
            e.HasKey(x => x.Id);
            e.Property(x => x.PayloadJson).HasColumnType("jsonb");
            e.HasIndex(x => x.AvailableAt).HasFilter("published_at IS NULL").HasDatabaseName("ix_outbox_unpublished");
        });
        b.Entity<InboxMessage>(e =>
        {
            e.ToTable("inbox_message");
            e.HasKey(x => new { x.Consumer, x.MessageId });
        });
        b.Entity<IdempotencyRecord>(e =>
        {
            e.ToTable("idempotency_record");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(128);
        });
        b.Entity<Artifact>(e =>
        {
            e.ToTable("artifact");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.ContentType).HasMaxLength(200);
            e.Property(x => x.Sha256).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.FileName).HasMaxLength(300);
            e.Property(x => x.MetadataJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.ProcessId, x.Type }).IsUnique();
        });
        b.Entity<DeadLetterEntry>(e =>
        {
            e.ToTable("dead_letter_entry");
            e.HasKey(x => x.Id);
            e.Property(x => x.OperationType).HasMaxLength(60);
            e.Property(x => x.Domain).HasMaxLength(40);
            e.Property(x => x.Queue).HasMaxLength(80);
            e.Property(x => x.ErrorClass).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.ResolvedBy).HasMaxLength(100);
            e.HasIndex(x => new { x.Domain, x.ResolvedAt });
            e.HasIndex(x => x.OperationId);
        });
        b.Entity<CallbackRegistration>(e =>
        {
            e.ToTable("callback_registration");
            e.HasKey(x => x.CallbackId);
            e.Property(x => x.CallbackId).HasMaxLength(100);
            e.Property(x => x.Url).HasMaxLength(2000);
            e.Property(x => x.Secret).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(300);
        });
        b.Entity<CallbackDelivery>(e =>
        {
            e.ToTable("callback_delivery");
            e.HasKey(x => x.Id);
            e.Property(x => x.EventType).HasMaxLength(80);
            e.Property(x => x.ProcessStatus).HasMaxLength(40);
            e.Property(x => x.Destination).HasMaxLength(2100);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.LastError).HasMaxLength(500);
            e.HasIndex(x => x.OperationId).IsUnique();
            e.HasIndex(x => x.EventId).IsUnique();
            e.HasIndex(x => new { x.ProcessId, x.CreatedAt });
        });
        b.Entity<ProofingSession>(e =>
        {
            e.ToTable("proofing_session");
            e.HasKey(x => x.Id);
            e.Property(x => x.ExternalId).HasMaxLength(200);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.Result).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.SubjectJson).HasColumnType("jsonb");
            e.HasMany(x => x.Validations).WithOne().HasForeignKey(x => x.SessionId);
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => new { x.Status, x.CompletedAt });
        });
        b.Entity<IdentityValidation>(e =>
        {
            e.ToTable("identity_validation");
            e.HasKey(x => x.Id);
            e.Property(x => x.Capability).HasMaxLength(40);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.DetailsJson).HasColumnType("jsonb");
            e.Property(x => x.ProviderCode).HasMaxLength(40);
            e.Ignore(x => x.IsTerminal);
            e.HasIndex(x => new { x.SessionId, x.Capability }).IsUnique();
        });
        b.Entity<ReconciliationRecord>(e =>
        {
            e.ToTable("reconciliation_record");
            e.HasKey(x => x.Id);
            e.Property(x => x.ProcessId).HasMaxLength(40);
            e.Property(x => x.Trigger).HasMaxLength(20);
            e.Property(x => x.InternalStatus).HasMaxLength(40);
            e.Property(x => x.ProviderStatus).HasMaxLength(40);
            e.Property(x => x.Outcome).HasMaxLength(20);
            e.Property(x => x.ResultingStatus).HasMaxLength(40);
            e.Property(x => x.DetailsJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.ProcessId, x.CreatedAt });
            e.HasIndex(x => new { x.Outcome, x.CreatedAt });
        });
        b.Entity<SignerConfirmation>(e =>
        {
            e.ToTable("signer_confirmation");
            e.HasKey(x => x.Id);
            e.Property(x => x.Channel).HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.CodeHash).HasMaxLength(64);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.SignerId, x.Channel }).IsUnique();
            e.HasIndex(x => x.ProcessId);
        });
        b.Entity<DocumentUpload>(e =>
        {
            e.ToTable("document_upload");
            e.HasKey(x => x.Id);
            e.Property(x => x.FileName).HasMaxLength(300);
            e.Property(x => x.ContentType).HasMaxLength(200);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.StorageKey).HasMaxLength(200);
            e.Property(x => x.ClientId).HasMaxLength(200);
        });
        b.Entity<NotificationSinkEntry>(e =>
        {
            e.ToTable("notification_sink");
            e.HasKey(x => x.Id);
            e.Property(x => x.Channel).HasMaxLength(20);
            e.Property(x => x.Destination).HasMaxLength(300);
            e.Property(x => x.Code).HasMaxLength(20);
            e.HasIndex(x => new { x.ProcessId, x.CreatedAt });
        });
        b.Entity<ProviderProcess>(e =>
        {
            e.ToTable("provider_process");
            e.HasKey(x => x.ProcessId);
            e.Property(x => x.MetadataJson).HasColumnType("jsonb");
            e.HasIndex(x => x.ExternalReference).IsUnique();
        });
    }

    /// <summary>Bumps the concurrency token of every modified process so races (cancel vs. workflow) are detected.</summary>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var created = 0;
        var finished = new List<(BusinessStatus Status, TimeSpan Elapsed)>();
        foreach (var entry in ChangeTracker.Entries<SignatureProcess>())
        {
            if (entry.State == EntityState.Added) created++;
            if (entry.State != EntityState.Modified) continue;
            entry.Entity.Version++;
            var status = entry.Entity.BusinessStatus;
            if (entry.Property(p => p.BusinessStatus).IsModified && entry.Property(p => p.BusinessStatus).OriginalValue != status
                && status is BusinessStatus.COMPLETED or BusinessStatus.FAILED or BusinessStatus.REJECTED or BusinessStatus.EXPIRED)
                finished.Add((status, DateTime.UtcNow - entry.Entity.CreatedAt));
        }
        var saved = await base.SaveChangesAsync(cancellationToken);
        if (created > 0) Orchestrator.Application.Telemetry.ProcessCreated.Add(created);
        foreach (var (status, elapsed) in finished)
        {
            if (status == BusinessStatus.COMPLETED)
            {
                Orchestrator.Application.Telemetry.ProcessCompleted.Add(1);
                Orchestrator.Application.Telemetry.CompletionTime.Record(elapsed.TotalSeconds);
            }
            else Orchestrator.Application.Telemetry.ProcessFailed.Add(1, new KeyValuePair<string, object?>("status", status.ToString()));
        }
        return saved;
    }
}
