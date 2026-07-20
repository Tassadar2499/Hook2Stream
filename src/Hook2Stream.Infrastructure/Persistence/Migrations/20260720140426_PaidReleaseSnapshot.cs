using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook2Stream.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PaidReleaseSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "audio_asset_id_snapshot",
                table: "entitlements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "audio_fingerprint_snapshot",
                table: "entitlements",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "release_mode_snapshot",
                table: "entitlements",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "schedule_anchor_snapshot",
                table: "entitlements",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "audio_asset_id_snapshot",
                table: "billing_checkouts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "audio_fingerprint_snapshot",
                table: "billing_checkouts",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "release_mode_snapshot",
                table: "billing_checkouts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "schedule_anchor_snapshot",
                table: "billing_checkouts",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "audio_asset_id_snapshot",
                table: "entitlements");

            migrationBuilder.DropColumn(
                name: "audio_fingerprint_snapshot",
                table: "entitlements");

            migrationBuilder.DropColumn(
                name: "release_mode_snapshot",
                table: "entitlements");

            migrationBuilder.DropColumn(
                name: "schedule_anchor_snapshot",
                table: "entitlements");

            migrationBuilder.DropColumn(
                name: "audio_asset_id_snapshot",
                table: "billing_checkouts");

            migrationBuilder.DropColumn(
                name: "audio_fingerprint_snapshot",
                table: "billing_checkouts");

            migrationBuilder.DropColumn(
                name: "release_mode_snapshot",
                table: "billing_checkouts");

            migrationBuilder.DropColumn(
                name: "schedule_anchor_snapshot",
                table: "billing_checkouts");
        }
    }
}
