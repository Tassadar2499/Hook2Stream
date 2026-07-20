using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook2Stream.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BillingAndRenders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_media_assets_project_id",
                table: "media_assets");

            migrationBuilder.AddColumn<Guid>(
                name: "artwork_pack_revision_id",
                table: "media_assets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "campaign_item_id",
                table: "media_assets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "render_batch_id",
                table: "media_assets",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "artwork_credit_grants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    checkout_id = table.Column<Guid>(type: "uuid", nullable: false),
                    granted = table.Column<int>(type: "integer", nullable: false),
                    remaining = table.Column<int>(type: "integer", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_artwork_credit_grants", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "artwork_credit_transactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    grant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    delta = table.Column<int>(type: "integer", nullable: false),
                    balance_after = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reference = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_artwork_credit_transactions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "billing_checkouts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    product_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    amount_cents = table.Column<int>(type: "integer", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    item_ids_json = table.Column<string>(type: "jsonb", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    external_session_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    checkout_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    external_customer_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    external_subscription_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    external_payment_intent_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    artwork_pack_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    artwork_composition_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    refunded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_billing_checkouts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "entitlements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    checkout_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    product_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    item_ids_json = table.Column<string>(type: "jsonb", nullable: false),
                    included_item_count = table.Column<int>(type: "integer", nullable: false),
                    remaining_content_rerenders = table.Column<int>(type: "integer", nullable: false),
                    provider_period_key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    external_subscription_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    external_payment_intent_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    external_invoice_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    artwork_pack_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    artwork_composition_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    period_starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    valid_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entitlements", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "render_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entitlement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    item_ids_json = table.Column<string>(type: "jsonb", nullable: false),
                    job_ids_json = table.Column<string>(type: "jsonb", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_render_batches", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "render_item_usages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entitlement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    initial_render_count = table.Column<int>(type: "integer", nullable: false),
                    content_rerender_count = table.Column<int>(type: "integer", nullable: false),
                    technical_retry_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_render_item_usages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workspace_artwork_credits",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    balance = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workspace_artwork_credits", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_media_assets_project_id_artwork_pack_revision_id_purpose",
                table: "media_assets",
                columns: new[] { "project_id", "artwork_pack_revision_id", "purpose" });

            migrationBuilder.CreateIndex(
                name: "ix_media_assets_project_id_campaign_item_id_purpose",
                table: "media_assets",
                columns: new[] { "project_id", "campaign_item_id", "purpose" });

            migrationBuilder.CreateIndex(
                name: "ix_artwork_credit_grants_checkout_id",
                table: "artwork_credit_grants",
                column: "checkout_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_artwork_credit_grants_workspace_id_remaining",
                table: "artwork_credit_grants",
                columns: new[] { "workspace_id", "remaining" });

            migrationBuilder.CreateIndex(
                name: "ix_artwork_credit_transactions_workspace_id_reference",
                table: "artwork_credit_transactions",
                columns: new[] { "workspace_id", "reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_billing_checkouts_external_payment_intent_id",
                table: "billing_checkouts",
                column: "external_payment_intent_id",
                unique: true,
                filter: "external_payment_intent_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_billing_checkouts_external_session_id",
                table: "billing_checkouts",
                column: "external_session_id",
                unique: true,
                filter: "external_session_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_billing_checkouts_external_subscription_id",
                table: "billing_checkouts",
                column: "external_subscription_id",
                filter: "external_subscription_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_billing_checkouts_workspace_id_idempotency_key",
                table: "billing_checkouts",
                columns: new[] { "workspace_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_entitlements_checkout_id_provider_period_key",
                table: "entitlements",
                columns: new[] { "checkout_id", "provider_period_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_entitlements_external_invoice_id",
                table: "entitlements",
                column: "external_invoice_id",
                filter: "external_invoice_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_entitlements_external_payment_intent_id",
                table: "entitlements",
                column: "external_payment_intent_id",
                filter: "external_payment_intent_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_entitlements_workspace_id_project_id_state",
                table: "entitlements",
                columns: new[] { "workspace_id", "project_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_render_batches_project_id_created_at",
                table: "render_batches",
                columns: new[] { "project_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_render_batches_workspace_id_idempotency_key",
                table: "render_batches",
                columns: new[] { "workspace_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_render_item_usages_entitlement_id_campaign_item_id",
                table: "render_item_usages",
                columns: new[] { "entitlement_id", "campaign_item_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_render_item_usages_workspace_id_project_id",
                table: "render_item_usages",
                columns: new[] { "workspace_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "ix_workspace_artwork_credits_workspace_id",
                table: "workspace_artwork_credits",
                column: "workspace_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "artwork_credit_grants");

            migrationBuilder.DropTable(
                name: "artwork_credit_transactions");

            migrationBuilder.DropTable(
                name: "billing_checkouts");

            migrationBuilder.DropTable(
                name: "entitlements");

            migrationBuilder.DropTable(
                name: "render_batches");

            migrationBuilder.DropTable(
                name: "render_item_usages");

            migrationBuilder.DropTable(
                name: "workspace_artwork_credits");

            migrationBuilder.DropIndex(
                name: "ix_media_assets_project_id_artwork_pack_revision_id_purpose",
                table: "media_assets");

            migrationBuilder.DropIndex(
                name: "ix_media_assets_project_id_campaign_item_id_purpose",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "artwork_pack_revision_id",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "campaign_item_id",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "render_batch_id",
                table: "media_assets");

            migrationBuilder.CreateIndex(
                name: "ix_media_assets_project_id",
                table: "media_assets",
                column: "project_id");
        }
    }
}
