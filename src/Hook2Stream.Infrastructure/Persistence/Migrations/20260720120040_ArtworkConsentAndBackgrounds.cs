using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook2Stream.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ArtworkConsentAndBackgrounds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "allows_external_ai_artwork",
                table: "rights_attestations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "background_asset_ids_json",
                table: "artwork_pack_revisions",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "allows_external_ai_artwork",
                table: "rights_attestations");

            migrationBuilder.DropColumn(
                name: "background_asset_ids_json",
                table: "artwork_pack_revisions");
        }
    }
}
