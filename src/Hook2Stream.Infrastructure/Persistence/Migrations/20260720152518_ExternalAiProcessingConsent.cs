using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook2Stream.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExternalAiProcessingConsent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "allows_external_ai_processing",
                table: "rights_attestations",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "allows_external_ai_processing",
                table: "rights_attestations");
        }
    }
}
