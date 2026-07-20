using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook2Stream.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RightsAudioBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "audio_asset_id",
                table: "rights_attestations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "audio_fingerprint",
                table: "rights_attestations",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "audio_asset_id",
                table: "rights_attestations");

            migrationBuilder.DropColumn(
                name: "audio_fingerprint",
                table: "rights_attestations");
        }
    }
}
