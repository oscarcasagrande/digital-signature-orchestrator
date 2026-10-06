using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIdentityProofing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_operation_signature_process_process_id",
                table: "operation");

            migrationBuilder.CreateTable(
                name: "proofing_session",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    result = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    subject_json = table.Column<string>(type: "jsonb", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    evidence_deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_proofing_session", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "identity_validation",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    session_id = table.Column<string>(type: "text", nullable: false),
                    capability = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    required = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    score = table.Column<double>(type: "double precision", nullable: true),
                    details_json = table.Column<string>(type: "jsonb", nullable: false),
                    provider_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    operation_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_identity_validation", x => x.id);
                    table.ForeignKey(
                        name: "fk_identity_validation_proofing_session_session_id",
                        column: x => x.session_id,
                        principalTable: "proofing_session",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_identity_validation_session_id_capability",
                table: "identity_validation",
                columns: new[] { "session_id", "capability" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_proofing_session_created_at",
                table: "proofing_session",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_proofing_session_status_completed_at",
                table: "proofing_session",
                columns: new[] { "status", "completed_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "identity_validation");

            migrationBuilder.DropTable(
                name: "proofing_session");

            migrationBuilder.AddForeignKey(
                name: "fk_operation_signature_process_process_id",
                table: "operation",
                column: "process_id",
                principalTable: "signature_process",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
