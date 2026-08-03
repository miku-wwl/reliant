using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Reliant.Infrastructure.Persistence;

#nullable disable

namespace Reliant.Infrastructure.Migrations;

[DbContext(typeof(ReliantDbContext))]
[Migration("20260803120000_AddJobFencingTokens")]
public partial class AddJobFencingTokens : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "FencingToken",
            table: "job_runs",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<long>(
            name: "FencingToken",
            table: "job_attempts",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<long>(
            name: "FencingToken",
            table: "leases",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "FencingToken",
            table: "job_runs");

        migrationBuilder.DropColumn(
            name: "FencingToken",
            table: "job_attempts");

        migrationBuilder.DropColumn(
            name: "FencingToken",
            table: "leases");
    }
}
