using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reliant.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CompleteDeadLetterReplay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CausationId",
                table: "dead_letter_records",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                table: "dead_letter_records",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ReplayMessageId",
                table: "dead_letter_records",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReplayRequestedBy",
                table: "dead_letter_records",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_dead_letter_records_OriginalMessageId_MessageType",
                table: "dead_letter_records",
                columns: new[] { "OriginalMessageId", "MessageType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_dead_letter_records_OriginalMessageId_MessageType",
                table: "dead_letter_records");

            migrationBuilder.DropColumn(
                name: "CausationId",
                table: "dead_letter_records");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "dead_letter_records");

            migrationBuilder.DropColumn(
                name: "ReplayMessageId",
                table: "dead_letter_records");

            migrationBuilder.DropColumn(
                name: "ReplayRequestedBy",
                table: "dead_letter_records");
        }
    }
}
