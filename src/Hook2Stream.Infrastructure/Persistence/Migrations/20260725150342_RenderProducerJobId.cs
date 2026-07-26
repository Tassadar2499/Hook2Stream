using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook2Stream.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenderProducerJobId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "producer_job_id",
                table: "media_assets",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        WITH raw AS (
                            SELECT id, provenance_json ->> 'jobId' AS job_id
                            FROM media_assets
                            WHERE purpose IN (6, 7)
                              AND provenance_json IS NOT NULL
                        ),
                        parsed AS (
                            SELECT id,
                                CASE
                                    WHEN job_id ~ '^[0-9A-Fa-f]{32}$' THEN
                                        (
                                            substring(job_id FROM 1 FOR 8) || '-' ||
                                            substring(job_id FROM 9 FOR 4) || '-' ||
                                            substring(job_id FROM 13 FOR 4) || '-' ||
                                            substring(job_id FROM 17 FOR 4) || '-' ||
                                            substring(job_id FROM 21 FOR 12)
                                        )::uuid
                                    WHEN job_id ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
                                        THEN job_id::uuid
                                    ELSE NULL
                                END AS producer_job_id
                            FROM raw
                        )
                        SELECT 1
                        FROM parsed
                        WHERE producer_job_id IS NOT NULL
                        GROUP BY producer_job_id
                        HAVING count(*) > 1
                    ) THEN
                        RAISE EXCEPTION
                            'Cannot backfill media_assets.producer_job_id because duplicate render job ids exist.';
                    END IF;

                    WITH raw AS (
                        SELECT id, provenance_json ->> 'jobId' AS job_id
                        FROM media_assets
                        WHERE purpose IN (6, 7)
                          AND provenance_json IS NOT NULL
                    ),
                    parsed AS (
                        SELECT id,
                            CASE
                                WHEN job_id ~ '^[0-9A-Fa-f]{32}$' THEN
                                    (
                                        substring(job_id FROM 1 FOR 8) || '-' ||
                                        substring(job_id FROM 9 FOR 4) || '-' ||
                                        substring(job_id FROM 13 FOR 4) || '-' ||
                                        substring(job_id FROM 17 FOR 4) || '-' ||
                                        substring(job_id FROM 21 FOR 12)
                                    )::uuid
                                WHEN job_id ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
                                    THEN job_id::uuid
                                ELSE NULL
                            END AS producer_job_id
                        FROM raw
                    )
                    UPDATE media_assets AS asset
                    SET producer_job_id = parsed.producer_job_id
                    FROM parsed
                    WHERE asset.id = parsed.id
                      AND parsed.producer_job_id IS NOT NULL;
                END $$;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_media_assets_producer_job_id",
                table: "media_assets",
                column: "producer_job_id",
                unique: true,
                filter: "producer_job_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_media_assets_producer_job_id",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "producer_job_id",
                table: "media_assets");
        }
    }
}
