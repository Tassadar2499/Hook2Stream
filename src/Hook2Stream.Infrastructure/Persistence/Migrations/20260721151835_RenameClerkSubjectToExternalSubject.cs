using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook2Stream.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenameClerkSubjectToExternalSubject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "clerk_subject",
                table: "users",
                newName: "external_subject");

            migrationBuilder.RenameIndex(
                name: "ix_users_clerk_subject",
                table: "users",
                newName: "ix_users_external_subject");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "external_subject",
                table: "users",
                newName: "clerk_subject");

            migrationBuilder.RenameIndex(
                name: "ix_users_external_subject",
                table: "users",
                newName: "ix_users_clerk_subject");
        }
    }
}
