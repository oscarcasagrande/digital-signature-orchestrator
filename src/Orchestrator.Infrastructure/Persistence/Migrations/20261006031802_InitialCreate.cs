using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Orchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "idempotency_record",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_hash = table.Column<string>(type: "text", nullable: false),
                    process_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_record", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "inbox_message",
                columns: table => new
                {
                    consumer = table.Column<string>(type: "text", nullable: false),
                    message_id = table.Column<string>(type: "text", nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox_message", x => new { x.consumer, x.message_id });
                });

            migrationBuilder.CreateTable(
                name: "journal_event",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    process_id = table.Column<string>(type: "text", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    type = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    actor_type = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: false),
                    metadata_json = table.Column<string>(type: "jsonb", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: false),
                    causation_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_journal_event", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_event",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    aggregate_id = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    queue = table.Column<string>(type: "text", nullable: false),
                    available_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_event", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "provider_process",
                columns: table => new
                {
                    process_id = table.Column<string>(type: "text", nullable: false),
                    provider_code = table.Column<string>(type: "text", nullable: false),
                    provider_process_id = table.Column<string>(type: "text", nullable: true),
                    external_reference = table.Column<string>(type: "text", nullable: false),
                    normalized_status = table.Column<string>(type: "text", nullable: false),
                    metadata_json = table.Column<string>(type: "jsonb", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_process", x => x.process_id);
                });

            migrationBuilder.CreateTable(
                name: "signature_process",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    business_status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    operational_status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    signature_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    request_json = table.Column<string>(type: "jsonb", nullable: false),
                    callback_json = table.Column<string>(type: "jsonb", nullable: true),
                    identity_validations_json = table.Column<string>(type: "jsonb", nullable: true),
                    correlation_id = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_signature_process", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "operation",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    process_id = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    next_retry_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    input_json = table.Column<string>(type: "jsonb", nullable: true),
                    output_json = table.Column<string>(type: "jsonb", nullable: true),
                    error_json = table.Column<string>(type: "jsonb", nullable: true),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_operation", x => x.id);
                    table.ForeignKey(
                        name: "fk_operation_signature_process_process_id",
                        column: x => x.process_id,
                        principalTable: "signature_process",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "signer",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    process_id = table.Column<string>(type: "text", nullable: false),
                    external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    document = table.Column<string>(type: "character varying(11)", maxLength: 11, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    signed = table.Column<bool>(type: "boolean", nullable: false),
                    signed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_signer", x => x.id);
                    table.ForeignKey(
                        name: "fk_signer_signature_process_process_id",
                        column: x => x.process_id,
                        principalTable: "signature_process",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_journal_event_process_id_seq",
                table: "journal_event",
                columns: new[] { "process_id", "seq" });

            migrationBuilder.CreateIndex(
                name: "ix_operation_process_id_type_sequence",
                table: "operation",
                columns: new[] { "process_id", "type", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_unpublished",
                table: "outbox_event",
                column: "available_at",
                filter: "published_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_provider_process_external_reference",
                table: "provider_process",
                column: "external_reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_signature_process_created_at",
                table: "signature_process",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_signature_process_external_id",
                table: "signature_process",
                column: "external_id");

            migrationBuilder.CreateIndex(
                name: "ix_signer_process_id",
                table: "signer",
                column: "process_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "idempotency_record");

            migrationBuilder.DropTable(
                name: "inbox_message");

            migrationBuilder.DropTable(
                name: "journal_event");

            migrationBuilder.DropTable(
                name: "operation");

            migrationBuilder.DropTable(
                name: "outbox_event");

            migrationBuilder.DropTable(
                name: "provider_process");

            migrationBuilder.DropTable(
                name: "signer");

            migrationBuilder.DropTable(
                name: "signature_process");
        }
    }
}
