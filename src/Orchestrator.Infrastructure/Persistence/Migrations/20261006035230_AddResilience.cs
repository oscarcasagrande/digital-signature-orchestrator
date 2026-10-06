using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddResilience : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "error_class",
                table: "operation",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "dead_letter_entry",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    process_id = table.Column<string>(type: "text", nullable: false),
                    operation_id = table.Column<string>(type: "text", nullable: false),
                    operation_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    domain = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    queue = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    error_class = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    resolved_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dead_letter_entry", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_dead_letter_entry_domain_resolved_at",
                table: "dead_letter_entry",
                columns: new[] { "domain", "resolved_at" });

            migrationBuilder.CreateIndex(
                name: "ix_dead_letter_entry_operation_id",
                table: "dead_letter_entry",
                column: "operation_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dead_letter_entry");

            migrationBuilder.DropColumn(
                name: "error_class",
                table: "operation");
        }
    }
}
