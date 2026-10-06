using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddArtifacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "artifact",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    process_id = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    content_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    storage_key = table.Column<string>(type: "text", nullable: false),
                    file_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    metadata_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_artifact", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_artifact_process_id_type",
                table: "artifact",
                columns: new[] { "process_id", "type" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "artifact");
        }
    }
}
