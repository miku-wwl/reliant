using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reliant.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "processing_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContributionId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    ProviderIdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ProviderReference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ErrorCategory = table.Column<int>(type: "integer", nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RequestPayload = table.Column<string>(type: "text", nullable: false),
                    ResponsePayload = table.Column<string>(type: "text", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_processing_attempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "provider_references",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContributionId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ProviderName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_references", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "reconciliation_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContributionId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocalState = table.Column<int>(type: "integer", nullable: false),
                    ProviderState = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Difference = table.Column<int>(type: "integer", nullable: false),
                    Resolution = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reconciliation_records", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_processing_attempts_ContributionId",
                table: "processing_attempts",
                column: "ContributionId");

            migrationBuilder.CreateIndex(
                name: "IX_provider_references_ContributionId",
                table: "provider_references",
                column: "ContributionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reconciliation_records_ContributionId",
                table: "reconciliation_records",
                column: "ContributionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "processing_attempts");

            migrationBuilder.DropTable(
                name: "provider_references");

            migrationBuilder.DropTable(
                name: "reconciliation_records");
        }
    }
}
