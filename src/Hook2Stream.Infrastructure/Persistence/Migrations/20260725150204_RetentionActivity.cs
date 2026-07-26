using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook2Stream.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RetentionActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_activity_at",
                table: "release_projects",
                type: "timestamp with time zone",
                nullable: true);

            // Existing visible projects receive a complete grace period. A
            // historical UpdatedAt may have been written by a background
            // worker and therefore is not a trustworthy user-activity anchor.
            migrationBuilder.Sql(
                """
                UPDATE release_projects
                SET last_activity_at = CASE
                    WHEN deleted_at IS NULL THEN now()
                    ELSE greatest(created_at, updated_at)
                END
                """);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "last_activity_at",
                table: "release_projects",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_release_projects_last_activity_at_id",
                table: "release_projects",
                columns: new[] { "last_activity_at", "id" },
                filter: "deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_release_projects_last_activity_at_id",
                table: "release_projects");

            migrationBuilder.DropColumn(
                name: "last_activity_at",
                table: "release_projects");
        }
    }
}
