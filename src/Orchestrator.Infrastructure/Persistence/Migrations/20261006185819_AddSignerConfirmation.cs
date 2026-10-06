using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSignerConfirmation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "confirmation_channels",
                table: "signer",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "email",
                table: "signer",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "phone",
                table: "signer",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "released_at",
                table: "signer",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "sign_order",
                table: "signer",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "signature_type",
                table: "signer",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "document_upload",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    client_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    file_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    content_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_upload", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notification_sink",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    process_id = table.Column<string>(type: "text", nullable: false),
                    signer_id = table.Column<string>(type: "text", nullable: false),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    destination = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_sink", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "signer_confirmation",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    process_id = table.Column<string>(type: "text", nullable: false),
                    signer_id = table.Column<string>(type: "text", nullable: false),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    failed_attempts = table.Column<int>(type: "integer", nullable: false),
                    send_count = table.Column<int>(type: "integer", nullable: false),
                    last_sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    confirmed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_signer_confirmation", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notification_sink_process_id_created_at",
                table: "notification_sink",
                columns: new[] { "process_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_signer_confirmation_process_id",
                table: "signer_confirmation",
                column: "process_id");

            migrationBuilder.CreateIndex(
                name: "ix_signer_confirmation_signer_id_channel",
                table: "signer_confirmation",
                columns: new[] { "signer_id", "channel" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_upload");

            migrationBuilder.DropTable(
                name: "notification_sink");

            migrationBuilder.DropTable(
                name: "signer_confirmation");

            migrationBuilder.DropColumn(
                name: "confirmation_channels",
                table: "signer");

            migrationBuilder.DropColumn(
                name: "email",
                table: "signer");

            migrationBuilder.DropColumn(
                name: "phone",
                table: "signer");

            migrationBuilder.DropColumn(
                name: "released_at",
                table: "signer");

            migrationBuilder.DropColumn(
                name: "sign_order",
                table: "signer");

            migrationBuilder.DropColumn(
                name: "signature_type",
                table: "signer");
        }
    }
}
