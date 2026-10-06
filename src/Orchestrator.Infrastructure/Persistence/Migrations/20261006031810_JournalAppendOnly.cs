using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class JournalAppendOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION journal_event_immutable() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'journal_event is append-only (% not allowed)', TG_OP USING ERRCODE = 'integrity_constraint_violation';
END;
$$ LANGUAGE plpgsql;
CREATE TRIGGER trg_journal_event_immutable
    BEFORE UPDATE OR DELETE ON journal_event
    FOR EACH ROW EXECUTE FUNCTION journal_event_immutable();
CREATE TRIGGER trg_journal_event_no_truncate
    BEFORE TRUNCATE ON journal_event
    FOR EACH STATEMENT EXECUTE FUNCTION journal_event_immutable();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TRIGGER IF EXISTS trg_journal_event_no_truncate ON journal_event;
DROP TRIGGER IF EXISTS trg_journal_event_immutable ON journal_event;
DROP FUNCTION IF EXISTS journal_event_immutable();");
        }
    }
}
