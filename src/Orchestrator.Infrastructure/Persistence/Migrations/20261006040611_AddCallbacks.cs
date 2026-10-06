using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCallbacks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "callback_delivery",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    process_id = table.Column<string>(type: "text", nullable: false),
                    operation_id = table.Column<string>(type: "text", nullable: false),
                    event_id = table.Column<string>(type: "text", nullable: false),
                    event_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    process_status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    destination = table.Column<string>(type: "character varying(2100)", maxLength: 2100, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_status_code = table.Column<int>(type: "integer", nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    delivered_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_callback_delivery", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "callback_registration",
                columns: table => new
                {
                    callback_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    secret = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_callback_registration", x => x.callback_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_callback_delivery_event_id",
                table: "callback_delivery",
                column: "event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_callback_delivery_operation_id",
                table: "callback_delivery",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_callback_delivery_process_id_created_at",
                table: "callback_delivery",
                columns: new[] { "process_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "callback_delivery");

            migrationBuilder.DropTable(
                name: "callback_registration");
        }
    }
}
