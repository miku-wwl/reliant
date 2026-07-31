using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reliant.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderIdempotencyConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_processing_attempts_ContributionId",
                table: "processing_attempts");

            migrationBuilder.AddColumn<string>(
                name: "ProviderName",
                table: "processing_attempts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_provider_references_ProviderName_Reference",
                table: "provider_references",
                columns: new[] { "ProviderName", "Reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_processing_attempts_ContributionId_AttemptNumber",
                table: "processing_attempts",
                columns: new[] { "ContributionId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_processing_attempts_ProviderName_ProviderIdempotencyKey",
                table: "processing_attempts",
                columns: new[] { "ProviderName", "ProviderIdempotencyKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_provider_references_ProviderName_Reference",
                table: "provider_references");

            migrationBuilder.DropIndex(
                name: "IX_processing_attempts_ContributionId_AttemptNumber",
                table: "processing_attempts");

            migrationBuilder.DropIndex(
                name: "IX_processing_attempts_ProviderName_ProviderIdempotencyKey",
                table: "processing_attempts");

            migrationBuilder.DropColumn(
                name: "ProviderName",
                table: "processing_attempts");

            migrationBuilder.CreateIndex(
                name: "IX_processing_attempts_ContributionId",
                table: "processing_attempts",
                column: "ContributionId");
        }
    }
}
