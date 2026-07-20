using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook2Stream.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Mp3FirstWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "current_artwork_pack_revision_id",
                table: "release_projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "current_campaign_plan_revision_id",
                table: "release_projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "current_hook_set_revision_id",
                table: "release_projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "current_transcript_revision_id",
                table: "release_projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "flow_kind",
                table: "release_projects",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<bool>(
                name: "is_instrumental_confirmed",
                table: "release_projects",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "setup_completed_at",
                table: "release_projects",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "origin",
                table: "media_assets",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "provenance_json",
                table: "media_assets",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "purpose",
                table: "media_assets",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "handler_version",
                table: "jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "v1");

            migrationBuilder.AddColumn<string>(
                name: "input_fingerprint",
                table: "jobs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "lease_token",
                table: "jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "payload_schema_version",
                table: "jobs",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<Guid>(
                name: "pipeline_run_id",
                table: "jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pipeline_stage",
                table: "jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "required_capability",
                table: "jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "media");

            migrationBuilder.CreateTable(
                name: "api_idempotency_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    secondary_resource_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_idempotency_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "artwork_pack_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    operation_number = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    prompt = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    candidate_asset_ids_json = table.Column<string>(type: "jsonb", nullable: false),
                    selected_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    composition_json = table.Column<string>(type: "jsonb", nullable: false),
                    source_fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    approved_by_subject = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_artwork_pack_revisions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "campaign_plan_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    transcript_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artwork_pack_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hook_set_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    items_json = table.Column<string>(type: "jsonb", nullable: false),
                    source_fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_campaign_plan_revisions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "hook_set_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    transcript_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hooks_json = table.Column<string>(type: "jsonb", nullable: false),
                    source_fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_hook_set_revisions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "inbox_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    message_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    aggregate_id = table.Column<Guid>(type: "uuid", nullable: true),
                    destination = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    message_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    dedupe_key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pipeline_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    trigger = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    input_fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pipeline_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_pipeline_runs_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "release_projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "track_analysis_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    source_fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    analysis_json = table.Column<string>(type: "jsonb", nullable: false),
                    processor_versions_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_track_analysis_revisions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "transcript_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    phrases_json = table.Column<string>(type: "jsonb", nullable: false),
                    source_fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    supersedes_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_by_subject = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transcript_revisions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pipeline_stages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    pipeline_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lane = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    progress_percent = table.Column<int>(type: "integer", nullable: false),
                    blocker_code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    error_code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    current_job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pipeline_stages", x => x.id);
                    table.ForeignKey(
                        name: "fk_pipeline_stages_pipeline_runs_pipeline_run_id",
                        column: x => x.pipeline_run_id,
                        principalTable: "pipeline_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_api_idempotency_records_workspace_id_scope_key",
                table: "api_idempotency_records",
                columns: new[] { "workspace_id", "scope", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_artwork_pack_revisions_project_id_number",
                table: "artwork_pack_revisions",
                columns: new[] { "project_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_artwork_pack_revisions_project_id_operation_number",
                table: "artwork_pack_revisions",
                columns: new[] { "project_id", "operation_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_campaign_plan_revisions_project_id_number",
                table: "campaign_plan_revisions",
                columns: new[] { "project_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_hook_set_revisions_project_id_number",
                table: "hook_set_revisions",
                columns: new[] { "project_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_inbox_messages_source_message_id",
                table: "inbox_messages",
                columns: new[] { "source", "message_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_dedupe_key",
                table: "outbox_messages",
                column: "dedupe_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_processed_at_created_at",
                table: "outbox_messages",
                columns: new[] { "processed_at", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_pipeline_runs_project_id_number",
                table: "pipeline_runs",
                columns: new[] { "project_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pipeline_stages_pipeline_run_id_lane",
                table: "pipeline_stages",
                columns: new[] { "pipeline_run_id", "lane" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_track_analysis_revisions_project_id_number",
                table: "track_analysis_revisions",
                columns: new[] { "project_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transcript_revisions_project_id_number",
                table: "transcript_revisions",
                columns: new[] { "project_id", "number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_idempotency_records");

            migrationBuilder.DropTable(
                name: "artwork_pack_revisions");

            migrationBuilder.DropTable(
                name: "campaign_plan_revisions");

            migrationBuilder.DropTable(
                name: "hook_set_revisions");

            migrationBuilder.DropTable(
                name: "inbox_messages");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "pipeline_stages");

            migrationBuilder.DropTable(
                name: "track_analysis_revisions");

            migrationBuilder.DropTable(
                name: "transcript_revisions");

            migrationBuilder.DropTable(
                name: "pipeline_runs");

            migrationBuilder.DropColumn(
                name: "current_artwork_pack_revision_id",
                table: "release_projects");

            migrationBuilder.DropColumn(
                name: "current_campaign_plan_revision_id",
                table: "release_projects");

            migrationBuilder.DropColumn(
                name: "current_hook_set_revision_id",
                table: "release_projects");

            migrationBuilder.DropColumn(
                name: "current_transcript_revision_id",
                table: "release_projects");

            migrationBuilder.DropColumn(
                name: "flow_kind",
                table: "release_projects");

            migrationBuilder.DropColumn(
                name: "is_instrumental_confirmed",
                table: "release_projects");

            migrationBuilder.DropColumn(
                name: "setup_completed_at",
                table: "release_projects");

            migrationBuilder.DropColumn(
                name: "origin",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "provenance_json",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "purpose",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "handler_version",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "input_fingerprint",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "lease_token",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "payload_schema_version",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "pipeline_run_id",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "pipeline_stage",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "required_capability",
                table: "jobs");
        }
    }
}
