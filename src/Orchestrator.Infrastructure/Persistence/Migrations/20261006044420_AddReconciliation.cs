using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "last_reconciled_at",
                table: "signature_process",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "reconciliation_record",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    process_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    trigger = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    internal_status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    provider_status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    resulting_status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    details_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reconciliation_record", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_record_outcome_created_at",
                table: "reconciliation_record",
                columns: new[] { "outcome", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_record_process_id_created_at",
                table: "reconciliation_record",
                columns: new[] { "process_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reconciliation_record");

            migrationBuilder.DropColumn(
                name: "last_reconciled_at",
                table: "signature_process");
        }
    }
}
