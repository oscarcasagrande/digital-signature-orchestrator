using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPortalSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "document_file_name",
                table: "signature_process",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            // Backfill rows created before the column existed (the file name lives in the stored request).
            migrationBuilder.Sql(
                "UPDATE signature_process SET document_file_name = LEFT(request_json->'document'->>'fileName', 300) WHERE document_file_name IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "document_file_name",
                table: "signature_process");
        }
    }
}
