using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddClientSegregationProofingCallbacks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "client_id",
                table: "proofing_session",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "client_id",
                table: "callback_registration",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "client_id",
                table: "proofing_session");

            migrationBuilder.DropColumn(
                name: "client_id",
                table: "callback_registration");
        }
    }
}
