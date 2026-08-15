using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook2Stream.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableEncryptedUploadParts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "upload_parts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    upload_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    part_number = table.Column<int>(type: "integer", nullable: false),
                    plaintext_length = table.Column<long>(type: "bigint", nullable: false),
                    plaintext_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    storage_e_tag = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    object_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_upload_parts", x => x.id);
                    table.ForeignKey(
                        name: "fk_upload_parts_upload_sessions_upload_session_id",
                        column: x => x.upload_session_id,
                        principalTable: "upload_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_upload_parts_upload_session_id_part_number",
                table: "upload_parts",
                columns: new[] { "upload_session_id", "part_number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "upload_parts");
        }
    }
}
