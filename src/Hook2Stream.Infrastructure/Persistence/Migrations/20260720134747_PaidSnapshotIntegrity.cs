using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook2Stream.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PaidSnapshotIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "artist_name_snapshot",
                table: "entitlements",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "campaign_plan_revision_id",
                table: "entitlements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "track_title_snapshot",
                table: "entitlements",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "artist_name_snapshot",
                table: "billing_checkouts",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "campaign_plan_revision_id",
                table: "billing_checkouts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "track_title_snapshot",
                table: "billing_checkouts",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "artist_name_snapshot",
                table: "entitlements");

            migrationBuilder.DropColumn(
                name: "campaign_plan_revision_id",
                table: "entitlements");

            migrationBuilder.DropColumn(
                name: "track_title_snapshot",
                table: "entitlements");

            migrationBuilder.DropColumn(
                name: "artist_name_snapshot",
                table: "billing_checkouts");

            migrationBuilder.DropColumn(
                name: "campaign_plan_revision_id",
                table: "billing_checkouts");

            migrationBuilder.DropColumn(
                name: "track_title_snapshot",
                table: "billing_checkouts");
        }
    }
}
