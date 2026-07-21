using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook2Stream.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductionCoreHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE jobs
                SET required_capability = CASE type
                    WHEN 1 THEN 'media'
                    WHEN 2 THEN 'control'
                    WHEN 3 THEN 'analysis'
                    WHEN 4 THEN 'control'
                    WHEN 5 THEN 'control'
                    WHEN 6 THEN 'control'
                    WHEN 7 THEN 'render'
                    WHEN 8 THEN 'render'
                    WHEN 9 THEN 'export'
                    WHEN 10 THEN 'render'
                END
                WHERE deleted_at IS NULL
                  AND type IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
                """);

            migrationBuilder.AddColumn<Guid>(
                name: "pipeline_run_id",
                table: "render_batches",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "state_before_archive",
                table: "release_projects",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "current_render_batch_id",
                table: "pipeline_stages",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE render_batches AS batch
                SET pipeline_run_id = (
                    SELECT job.pipeline_run_id
                    FROM jobs AS job
                    WHERE job.project_id = batch.project_id
                      AND job.pipeline_run_id IS NOT NULL
                      AND job.payload_json ->> 'renderBatchId' = batch.id::text
                    ORDER BY job.created_at DESC
                    LIMIT 1
                )
                WHERE batch.pipeline_run_id IS NULL
                  AND EXISTS (
                      SELECT 1
                      FROM jobs AS job
                      WHERE job.project_id = batch.project_id
                        AND job.pipeline_run_id IS NOT NULL
                        AND job.payload_json ->> 'renderBatchId' = batch.id::text
                  );

                UPDATE pipeline_stages AS stage
                SET current_render_batch_id = (
                    SELECT batch.id
                    FROM pipeline_runs AS run
                    JOIN render_batches AS batch ON batch.pipeline_run_id = run.id
                    WHERE run.id = stage.pipeline_run_id
                    ORDER BY
                        CASE WHEN batch.state IN (1, 2) THEN 0 ELSE 1 END,
                        batch.created_at DESC
                    LIMIT 1
                )
                WHERE stage.lane = 8
                  AND stage.current_render_batch_id IS NULL
                  AND EXISTS (
                      SELECT 1
                      FROM pipeline_runs AS run
                      JOIN render_batches AS batch ON batch.pipeline_run_id = run.id
                      WHERE run.id = stage.pipeline_run_id
                  );
                """);

            migrationBuilder.CreateTable(
                name: "auth_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    csrf_token_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_auth_sessions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "oauth_login_states",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    state_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    return_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_oauth_login_states", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "project_deletion_tombstones",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_subject = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    policy_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    purge_due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_purged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_deletion_tombstones", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_render_batches_pipeline_run_id",
                table: "render_batches",
                column: "pipeline_run_id",
                filter: "pipeline_run_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_pipeline_stages_current_render_batch_id",
                table: "pipeline_stages",
                column: "current_render_batch_id",
                filter: "current_render_batch_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_auth_sessions_token_hash",
                table: "auth_sessions",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_auth_sessions_user_id_expires_at",
                table: "auth_sessions",
                columns: new[] { "user_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_oauth_login_states_expires_at_consumed_at",
                table: "oauth_login_states",
                columns: new[] { "expires_at", "consumed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_oauth_login_states_state_hash",
                table: "oauth_login_states",
                column: "state_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_project_deletion_tombstones_state_purge_due_at",
                table: "project_deletion_tombstones",
                columns: new[] { "state", "purge_due_at" });

            migrationBuilder.CreateIndex(
                name: "ix_project_deletion_tombstones_workspace_id_project_id",
                table: "project_deletion_tombstones",
                columns: new[] { "workspace_id", "project_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auth_sessions");

            migrationBuilder.DropTable(
                name: "oauth_login_states");

            migrationBuilder.DropTable(
                name: "project_deletion_tombstones");

            migrationBuilder.DropIndex(
                name: "ix_render_batches_pipeline_run_id",
                table: "render_batches");

            migrationBuilder.DropIndex(
                name: "ix_pipeline_stages_current_render_batch_id",
                table: "pipeline_stages");

            migrationBuilder.DropColumn(
                name: "pipeline_run_id",
                table: "render_batches");

            migrationBuilder.DropColumn(
                name: "state_before_archive",
                table: "release_projects");

            migrationBuilder.DropColumn(
                name: "current_render_batch_id",
                table: "pipeline_stages");
        }
    }
}
