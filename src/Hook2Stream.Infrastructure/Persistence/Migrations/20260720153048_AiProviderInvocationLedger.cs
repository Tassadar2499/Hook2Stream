using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook2Stream.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AiProviderInvocationLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_provider_invocations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_number = table.Column<int>(type: "integer", nullable: false),
                    stage = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    failure_code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    requested_provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    resolved_provider = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    requested_model = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    resolved_model = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    request_id = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    generation_id = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    input_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    parameter_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: true),
                    output_tokens = table.Column<long>(type: "bigint", nullable: true),
                    total_tokens = table.Column<long>(type: "bigint", nullable: true),
                    audio_seconds = table.Column<double>(type: "double precision", nullable: true),
                    generated_images = table.Column<int>(type: "integer", nullable: true),
                    cost_usd = table.Column<decimal>(type: "numeric(20,10)", precision: 20, scale: 10, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_provider_invocations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_provider_invocations_job_id_attempt_number_stage",
                table: "ai_provider_invocations",
                columns: new[] { "job_id", "attempt_number", "stage" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ai_provider_invocations_operation_id",
                table: "ai_provider_invocations",
                column: "operation_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_provider_invocations_project_id_created_at",
                table: "ai_provider_invocations",
                columns: new[] { "project_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_provider_invocations_requested_provider_requested_model_",
                table: "ai_provider_invocations",
                columns: new[] { "requested_provider", "requested_model", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_provider_invocations_workspace_id_created_at",
                table: "ai_provider_invocations",
                columns: new[] { "workspace_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_provider_invocations");
        }
    }
}
