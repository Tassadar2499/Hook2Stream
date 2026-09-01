using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook2Stream.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StripeLifecycleState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_billing_checkouts_external_subscription_id",
                table: "billing_checkouts");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "provider_event_occurred_at",
                table: "entitlements",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "provider_access_revoked_at",
                table: "billing_checkouts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_billing_checkouts_external_subscription_id",
                table: "billing_checkouts",
                column: "external_subscription_id",
                unique: true,
                filter: "external_subscription_id IS NOT NULL");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "subscription_access_ended_at",
                table: "billing_checkouts",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_billing_checkouts_external_subscription_id",
                table: "billing_checkouts");

            migrationBuilder.DropColumn(
                name: "provider_event_occurred_at",
                table: "entitlements");

            migrationBuilder.DropColumn(
                name: "provider_access_revoked_at",
                table: "billing_checkouts");

            migrationBuilder.DropColumn(
                name: "subscription_access_ended_at",
                table: "billing_checkouts");

            migrationBuilder.CreateIndex(
                name: "ix_billing_checkouts_external_subscription_id",
                table: "billing_checkouts",
                column: "external_subscription_id",
                filter: "external_subscription_id IS NOT NULL");
        }
    }
}
