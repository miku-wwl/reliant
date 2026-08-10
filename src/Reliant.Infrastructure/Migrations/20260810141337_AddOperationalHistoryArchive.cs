using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reliant.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOperationalHistoryArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "operational_history_archives",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceOccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ArchivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operational_history_archives", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_inbox_messages_Status_ProcessedAt",
                table: "inbox_messages",
                columns: new[] { "Status", "ProcessedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_job_runs_Status_CompletedAt",
                table: "job_runs",
                columns: new[] { "Status", "CompletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_operational_history_archives_OrganizationId_ArchivedAt",
                table: "operational_history_archives",
                columns: new[] { "OrganizationId", "ArchivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_operational_history_archives_SourceType_SourceId",
                table: "operational_history_archives",
                columns: new[] { "SourceType", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_processing_attempts_Status_CompletedAt",
                table: "processing_attempts",
                columns: new[] { "Status", "CompletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_reconciliation_records_Resolution_ResolvedAt",
                table: "reconciliation_records",
                columns: new[] { "Resolution", "ResolvedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "operational_history_archives");

            migrationBuilder.DropIndex(
                name: "IX_inbox_messages_Status_ProcessedAt",
                table: "inbox_messages");

            migrationBuilder.DropIndex(
                name: "IX_job_runs_Status_CompletedAt",
                table: "job_runs");

            migrationBuilder.DropIndex(
                name: "IX_processing_attempts_Status_CompletedAt",
                table: "processing_attempts");

            migrationBuilder.DropIndex(
                name: "IX_reconciliation_records_Resolution_ResolvedAt",
                table: "reconciliation_records");
        }
    }
}
