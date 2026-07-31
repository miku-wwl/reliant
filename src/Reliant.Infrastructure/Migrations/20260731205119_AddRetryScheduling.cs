using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reliant.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRetryScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LastErrorCategory",
                table: "contributions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastErrorMessage",
                table: "contributions",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextRetryAt",
                table: "contributions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetryCount",
                table: "contributions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_contributions_State_NextRetryAt",
                table: "contributions",
                columns: new[] { "State", "NextRetryAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_contributions_State_NextRetryAt",
                table: "contributions");

            migrationBuilder.DropColumn(
                name: "LastErrorCategory",
                table: "contributions");

            migrationBuilder.DropColumn(
                name: "LastErrorMessage",
                table: "contributions");

            migrationBuilder.DropColumn(
                name: "NextRetryAt",
                table: "contributions");

            migrationBuilder.DropColumn(
                name: "RetryCount",
                table: "contributions");
        }
    }
}
