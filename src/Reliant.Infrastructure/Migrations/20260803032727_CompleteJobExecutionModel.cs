using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reliant.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CompleteJobExecutionModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_job_attempts_JobRunId",
                table: "job_attempts");

            migrationBuilder.AddColumn<Guid>(
                name: "LeaseId",
                table: "job_attempts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "job_attempts",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "WorkerId",
                table: "job_attempts",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.InsertData(
                table: "job_definitions",
                columns: new[] { "Id", "HandlerName", "MaxAttempts", "Name", "RetryPolicy", "TimeoutSeconds" },
                values: new object[] { new Guid("7346d035-7e28-4dc8-b7b7-a982242df4ae"), "ProcessingHandler", 5, "Contribution Processing", "exponential", 30 });

            migrationBuilder.Sql("""
                UPDATE job_attempts
                SET "Status" = CASE
                    WHEN "CompletedAt" IS NULL THEN 1
                    WHEN "Succeeded" THEN 2
                    ELSE 3
                END
                """);

            migrationBuilder.Sql("""
                INSERT INTO job_runs (
                    "Id",
                    "OrganizationId",
                    "JobDefinitionId",
                    "QueueUrl",
                    "MessageId",
                    "Payload",
                    "Status",
                    "AttemptCount",
                    "StartedAt",
                    "CompletedAt",
                    "CreatedAt",
                    "Version")
                SELECT
                    legacy."JobRunId",
                    '00000000-0000-0000-0000-000000000000'::uuid,
                    '7346d035-7e28-4dc8-b7b7-a982242df4ae'::uuid,
                    'reliant-processing',
                    'legacy-' || legacy."JobRunId"::text,
                    '{}',
                    CASE COALESCE((
                        SELECT attempt."Status"
                        FROM job_attempts attempt
                        WHERE attempt."JobRunId" = legacy."JobRunId"
                        ORDER BY attempt."AttemptNumber" DESC
                        LIMIT 1), 0)
                        WHEN 1 THEN 2
                        WHEN 2 THEN 3
                        WHEN 3 THEN 4
                        ELSE 1
                    END,
                    COALESCE((
                        SELECT MAX(attempt."AttemptNumber")
                        FROM job_attempts attempt
                        WHERE attempt."JobRunId" = legacy."JobRunId"), 0),
                    (
                        SELECT MIN(attempt."StartedAt")
                        FROM job_attempts attempt
                        WHERE attempt."JobRunId" = legacy."JobRunId"
                    ),
                    (
                        SELECT MAX(attempt."CompletedAt")
                        FROM job_attempts attempt
                        WHERE attempt."JobRunId" = legacy."JobRunId"
                    ),
                    NOW(),
                    0
                FROM (
                    SELECT "JobRunId" FROM leases
                    UNION
                    SELECT "JobRunId" FROM job_attempts
                ) AS legacy
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM job_runs existing
                    WHERE existing."Id" = legacy."JobRunId")
                """);

            migrationBuilder.Sql("""
                UPDATE job_runs run
                SET "JobDefinitionId" =
                    '7346d035-7e28-4dc8-b7b7-a982242df4ae'::uuid
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM job_definitions definition
                    WHERE definition."Id" = run."JobDefinitionId")
                """);

            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT
                        "Id",
                        ROW_NUMBER() OVER (
                            PARTITION BY "JobRunId"
                            ORDER BY "AcquiredAt" DESC, "Id") AS owner_rank
                    FROM leases
                    WHERE "IsActive"
                )
                UPDATE leases lease
                SET "IsActive" = FALSE
                FROM ranked
                WHERE lease."Id" = ranked."Id"
                  AND ranked.owner_rank > 1
                """);

            migrationBuilder.DropColumn(
                name: "Succeeded",
                table: "job_attempts");

            migrationBuilder.CreateIndex(
                name: "IX_leases_JobRunId",
                table: "leases",
                column: "JobRunId",
                unique: true,
                filter: "\"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_job_runs_JobDefinitionId",
                table: "job_runs",
                column: "JobDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_job_runs_MessageId",
                table: "job_runs",
                column: "MessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_job_attempts_JobRunId_AttemptNumber",
                table: "job_attempts",
                columns: new[] { "JobRunId", "AttemptNumber" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_job_attempts_job_runs_JobRunId",
                table: "job_attempts",
                column: "JobRunId",
                principalTable: "job_runs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_job_runs_job_definitions_JobDefinitionId",
                table: "job_runs",
                column: "JobDefinitionId",
                principalTable: "job_definitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_leases_job_runs_JobRunId",
                table: "leases",
                column: "JobRunId",
                principalTable: "job_runs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_job_attempts_job_runs_JobRunId",
                table: "job_attempts");

            migrationBuilder.DropForeignKey(
                name: "FK_job_runs_job_definitions_JobDefinitionId",
                table: "job_runs");

            migrationBuilder.DropForeignKey(
                name: "FK_leases_job_runs_JobRunId",
                table: "leases");

            migrationBuilder.DropIndex(
                name: "IX_leases_JobRunId",
                table: "leases");

            migrationBuilder.DropIndex(
                name: "IX_job_runs_JobDefinitionId",
                table: "job_runs");

            migrationBuilder.DropIndex(
                name: "IX_job_runs_MessageId",
                table: "job_runs");

            migrationBuilder.DropIndex(
                name: "IX_job_attempts_JobRunId_AttemptNumber",
                table: "job_attempts");

            migrationBuilder.DeleteData(
                table: "job_definitions",
                keyColumn: "Id",
                keyValue: new Guid("7346d035-7e28-4dc8-b7b7-a982242df4ae"));

            migrationBuilder.AddColumn<bool>(
                name: "Succeeded",
                table: "job_attempts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""
                UPDATE job_attempts
                SET "Succeeded" = ("Status" = 2)
                """);

            migrationBuilder.DropColumn(
                name: "LeaseId",
                table: "job_attempts");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "job_attempts");

            migrationBuilder.DropColumn(
                name: "WorkerId",
                table: "job_attempts");

            migrationBuilder.CreateIndex(
                name: "IX_job_attempts_JobRunId",
                table: "job_attempts",
                column: "JobRunId");
        }
    }
}
