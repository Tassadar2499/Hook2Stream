using Hook2Stream.Domain;
using Microsoft.EntityFrameworkCore;

namespace Hook2Stream.Infrastructure.Persistence;

public sealed class Hook2StreamDbContext(DbContextOptions<Hook2StreamDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<BrandKit> BrandKits => Set<BrandKit>();
    public DbSet<ReleaseProject> Projects => Set<ReleaseProject>();
    public DbSet<RightsAttestation> RightsAttestations => Set<RightsAttestation>();
    public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();
    public DbSet<MediaDerivative> MediaDerivatives => Set<MediaDerivative>();
    public DbSet<UploadSession> UploadSessions => Set<UploadSession>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobAttempt> JobAttempts => Set<JobAttempt>();
    public DbSet<JobEvent> JobEvents => Set<JobEvent>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<AiProviderInvocation> AiProviderInvocations => Set<AiProviderInvocation>();
    public DbSet<PipelineRun> PipelineRuns => Set<PipelineRun>();
    public DbSet<PipelineStage> PipelineStages => Set<PipelineStage>();
    public DbSet<ProjectEvent> ProjectEvents => Set<ProjectEvent>();
    public DbSet<TrackAnalysisRevision> TrackAnalysisRevisions => Set<TrackAnalysisRevision>();
    public DbSet<TranscriptRevision> TranscriptRevisions => Set<TranscriptRevision>();
    public DbSet<ArtworkPackRevision> ArtworkPackRevisions => Set<ArtworkPackRevision>();
    public DbSet<HookSetRevision> HookSetRevisions => Set<HookSetRevision>();
    public DbSet<CampaignPlanRevision> CampaignPlanRevisions => Set<CampaignPlanRevision>();
    public DbSet<ApiIdempotencyRecord> ApiIdempotencyRecords => Set<ApiIdempotencyRecord>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<BillingCheckout> BillingCheckouts => Set<BillingCheckout>();
    public DbSet<Entitlement> Entitlements => Set<Entitlement>();
    public DbSet<WorkspaceArtworkCredit> WorkspaceArtworkCredits => Set<WorkspaceArtworkCredit>();
    public DbSet<ArtworkCreditGrant> ArtworkCreditGrants => Set<ArtworkCreditGrant>();
    public DbSet<ArtworkCreditTransaction> ArtworkCreditTransactions => Set<ArtworkCreditTransaction>();
    public DbSet<RenderBatch> RenderBatches => Set<RenderBatch>();
    public DbSet<RenderItemUsage> RenderItemUsages => Set<RenderItemUsage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureEntity<AppUser>(modelBuilder, "users");
        ConfigureEntity<Workspace>(modelBuilder, "workspaces");
        ConfigureEntity<BrandKit>(modelBuilder, "brand_kits");
        ConfigureEntity<ReleaseProject>(modelBuilder, "release_projects");
        ConfigureEntity<RightsAttestation>(modelBuilder, "rights_attestations");
        ConfigureEntity<MediaAsset>(modelBuilder, "media_assets");
        ConfigureEntity<MediaDerivative>(modelBuilder, "media_derivatives");
        ConfigureEntity<UploadSession>(modelBuilder, "upload_sessions");
        ConfigureEntity<Job>(modelBuilder, "jobs");
        ConfigureEntity<JobAttempt>(modelBuilder, "job_attempts");
        ConfigureEntity<JobEvent>(modelBuilder, "job_events");
        ConfigureEntity<AuditEvent>(modelBuilder, "audit_events");
        ConfigureEntity<AiProviderInvocation>(modelBuilder, "ai_provider_invocations");
        ConfigureEntity<PipelineRun>(modelBuilder, "pipeline_runs");
        ConfigureEntity<PipelineStage>(modelBuilder, "pipeline_stages");
        ConfigureEntity<ProjectEvent>(modelBuilder, "project_events");
        ConfigureEntity<TrackAnalysisRevision>(modelBuilder, "track_analysis_revisions");
        ConfigureEntity<TranscriptRevision>(modelBuilder, "transcript_revisions");
        ConfigureEntity<ArtworkPackRevision>(modelBuilder, "artwork_pack_revisions");
        ConfigureEntity<HookSetRevision>(modelBuilder, "hook_set_revisions");
        ConfigureEntity<CampaignPlanRevision>(modelBuilder, "campaign_plan_revisions");
        ConfigureEntity<ApiIdempotencyRecord>(modelBuilder, "api_idempotency_records");
        ConfigureEntity<OutboxMessage>(modelBuilder, "outbox_messages");
        ConfigureEntity<InboxMessage>(modelBuilder, "inbox_messages");
        ConfigureEntity<BillingCheckout>(modelBuilder, "billing_checkouts");
        ConfigureEntity<Entitlement>(modelBuilder, "entitlements");
        ConfigureEntity<WorkspaceArtworkCredit>(modelBuilder, "workspace_artwork_credits");
        ConfigureEntity<ArtworkCreditGrant>(modelBuilder, "artwork_credit_grants");
        ConfigureEntity<ArtworkCreditTransaction>(modelBuilder, "artwork_credit_transactions");
        ConfigureEntity<RenderBatch>(modelBuilder, "render_batches");
        ConfigureEntity<RenderItemUsage>(modelBuilder, "render_item_usages");

        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.HasIndex(value => value.ExternalSubject).IsUnique();
            entity.Property(value => value.ExternalSubject).HasMaxLength(255);
            entity.Property(value => value.Email).HasMaxLength(320);
            entity.Property(value => value.DisplayName).HasMaxLength(160);
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<Workspace>(entity =>
        {
            entity.HasIndex(value => value.OwnerUserId).IsUnique();
            entity.Property(value => value.Name).HasMaxLength(160);
            entity.Property(value => value.TermsVersion).HasMaxLength(64);
            entity.Property(value => value.PrivacyVersion).HasMaxLength(64);
            entity.HasOne(value => value.OwnerUser)
                .WithOne(value => value.Workspace)
                .HasForeignKey<Workspace>(value => value.OwnerUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<BrandKit>(entity =>
        {
            entity.HasIndex(value => value.WorkspaceId).IsUnique();
            entity.Property(value => value.DisplayName).HasMaxLength(120);
            entity.Property(value => value.PrimaryColor).HasMaxLength(7);
            entity.Property(value => value.SecondaryColor).HasMaxLength(7);
            entity.Property(value => value.AccentColor).HasMaxLength(7);
            entity.Property(value => value.HeadingFont).HasMaxLength(64);
            entity.Property(value => value.BodyFont).HasMaxLength(64);
            entity.Property(value => value.DefaultCta).HasMaxLength(160);
            entity.Property(value => value.SmartLink).HasMaxLength(2048);
            entity.Property(value => value.ToneRestrictions).HasMaxLength(1_000);
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<ReleaseProject>(entity =>
        {
            entity.HasIndex(value => new { value.WorkspaceId, value.CreatedAt });
            entity.Property(value => value.ProjectLabel).HasMaxLength(160);
            entity.Property(value => value.ArtistName).HasMaxLength(160);
            entity.Property(value => value.TrackTitle).HasMaxLength(160);
            entity.Property(value => value.Language).HasMaxLength(16);
            entity.Property(value => value.InternalNotes).HasMaxLength(4_000);
            entity.Property(value => value.LyricsText).HasMaxLength(100_000);
            entity.Property(value => value.FlowKind).HasDefaultValue(FlowKind.Legacy);
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<RightsAttestation>(entity =>
        {
            entity.HasIndex(value => value.ProjectId).IsUnique();
            entity.Property(value => value.ActorSubject).HasMaxLength(255);
            entity.Property(value => value.PolicyVersion).HasMaxLength(64);
            entity.Property(value => value.AudioFingerprint).HasMaxLength(128);
            entity.Property(value => value.AllowsExternalAiProcessing).HasDefaultValue(false);
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<MediaAsset>(entity =>
        {
            entity.HasIndex(value => new { value.WorkspaceId, value.ProjectId, value.Kind, value.IsActive });
            entity.HasIndex(value => value.ObjectKey).IsUnique();
            entity.Property(value => value.OriginalFileName).HasMaxLength(255);
            entity.Property(value => value.DeclaredContentType).HasMaxLength(128);
            entity.Property(value => value.DetectedContentType).HasMaxLength(128);
            entity.Property(value => value.ObjectKey).HasMaxLength(512);
            entity.Property(value => value.Sha256).HasMaxLength(64);
            entity.Property(value => value.VideoCodec).HasMaxLength(64);
            entity.Property(value => value.AudioCodec).HasMaxLength(64);
            entity.Property(value => value.FailureCode).HasMaxLength(128);
            entity.Property(value => value.FailureMessage).HasMaxLength(1_000);
            entity.Property(value => value.Origin).HasDefaultValue(AssetOrigin.Uploaded);
            entity.Property(value => value.Purpose).HasDefaultValue(AssetPurpose.Source);
            entity.Property(value => value.ProvenanceJson).HasColumnType("jsonb");
            entity.HasIndex(value => new { value.ProjectId, value.CampaignItemId, value.Purpose });
            entity.HasIndex(value => new { value.ProjectId, value.ArtworkPackRevisionId, value.Purpose });
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<MediaDerivative>(entity =>
        {
            entity.HasIndex(value => value.ObjectKey).IsUnique();
            entity.HasIndex(value => new { value.AssetId, value.Kind, value.ProcessorVersion }).IsUnique();
            entity.Property(value => value.ProcessorVersion).HasMaxLength(64);
            entity.Property(value => value.ObjectKey).HasMaxLength(512);
            entity.Property(value => value.ContentType).HasMaxLength(128);
            entity.Property(value => value.Sha256).HasMaxLength(64);
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<UploadSession>(entity =>
        {
            entity.HasIndex(value => new { value.WorkspaceId, value.ProjectId, value.State });
            entity.Property(value => value.ObjectKey).HasMaxLength(512);
            entity.Property(value => value.MultipartUploadId).HasMaxLength(512);
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<Job>(entity =>
        {
            entity.HasIndex(value => new { value.State, value.AvailableAt, value.CreatedAt });
            entity.HasIndex(value => new { value.WorkspaceId, value.ProjectId, value.CreatedAt });
            entity.HasIndex(value => value.IdempotencyKey)
                .IsUnique()
                .HasFilter("idempotency_key IS NOT NULL");
            entity.Property(value => value.PayloadJson).HasColumnType("jsonb");
            entity.Property(value => value.IdempotencyKey).HasMaxLength(255);
            entity.Property(value => value.RequiredCapability).HasMaxLength(64);
            entity.Property(value => value.HandlerVersion).HasMaxLength(64);
            entity.Property(value => value.RequiredCapability).HasDefaultValue("media");
            entity.Property(value => value.HandlerVersion).HasDefaultValue("v1");
            entity.Property(value => value.PayloadSchemaVersion).HasDefaultValue(1);
            entity.Property(value => value.InputFingerprint).HasMaxLength(128);
            entity.Property(value => value.PipelineStage).HasMaxLength(64);
            entity.Property(value => value.LeaseOwner).HasMaxLength(255);
            entity.Property(value => value.ProgressStage).HasMaxLength(128);
            entity.Property(value => value.ErrorCode).HasMaxLength(128);
            entity.Property(value => value.ErrorMessage).HasMaxLength(1_000);
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<JobAttempt>(entity =>
        {
            entity.HasIndex(value => new { value.JobId, value.Number }).IsUnique();
            entity.Property(value => value.WorkerId).HasMaxLength(255);
            entity.Property(value => value.ErrorCode).HasMaxLength(128);
            entity.Property(value => value.ErrorMessage).HasMaxLength(1_000);
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<JobEvent>(entity =>
        {
            entity.Property(value => value.Sequence).ValueGeneratedOnAdd();
            entity.HasIndex(value => value.Sequence).IsUnique();
            entity.HasIndex(value => new { value.JobId, value.Sequence });
            entity.Property(value => value.EventType).HasMaxLength(128);
            entity.Property(value => value.DataJson).HasColumnType("jsonb");
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<AuditEvent>(entity =>
        {
            entity.HasIndex(value => new { value.WorkspaceId, value.CreatedAt });
            entity.Property(value => value.ActorSubject).HasMaxLength(255);
            entity.Property(value => value.Action).HasMaxLength(128);
            entity.Property(value => value.ResourceType).HasMaxLength(128);
            entity.Property(value => value.DataJson).HasColumnType("jsonb");
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<AiProviderInvocation>(entity =>
        {
            entity.HasIndex(value => new { value.JobId, value.AttemptNumber, value.Stage }).IsUnique();
            entity.HasIndex(value => new { value.WorkspaceId, value.CreatedAt });
            entity.HasIndex(value => new { value.ProjectId, value.CreatedAt });
            entity.HasIndex(value => value.OperationId);
            entity.HasIndex(value => new { value.RequestedProvider, value.RequestedModel, value.StartedAt });
            entity.Property(value => value.Stage).HasMaxLength(64);
            entity.Property(value => value.Status).HasMaxLength(32);
            entity.Property(value => value.FailureCode).HasMaxLength(128);
            entity.Property(value => value.RequestedProvider).HasMaxLength(64);
            entity.Property(value => value.ResolvedProvider).HasMaxLength(128);
            entity.Property(value => value.RequestedModel).HasMaxLength(255);
            entity.Property(value => value.ResolvedModel).HasMaxLength(255);
            entity.Property(value => value.RequestId).HasMaxLength(2_048);
            entity.Property(value => value.GenerationId).HasMaxLength(2_048);
            entity.Property(value => value.InputHash).HasMaxLength(64).IsFixedLength();
            entity.Property(value => value.ParameterHash).HasMaxLength(64).IsFixedLength();
            entity.Property(value => value.CostUsd).HasPrecision(20, 10);
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<PipelineRun>(entity =>
        {
            entity.HasIndex(value => new { value.ProjectId, value.Number }).IsUnique();
            entity.Property(value => value.Trigger).HasMaxLength(64);
            entity.Property(value => value.InputFingerprint).HasMaxLength(128);
            entity.HasOne(value => value.Project)
                .WithMany(value => value.PipelineRuns)
                .HasForeignKey(value => value.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<PipelineStage>(entity =>
        {
            entity.HasIndex(value => new { value.PipelineRunId, value.Lane }).IsUnique();
            entity.Property(value => value.BlockerCode).HasMaxLength(128);
            entity.Property(value => value.ErrorCode).HasMaxLength(128);
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<ProjectEvent>(entity =>
        {
            entity.Property(value => value.Sequence).ValueGeneratedOnAdd();
            entity.HasIndex(value => value.Sequence).IsUnique();
            entity.HasIndex(value => new { value.WorkspaceId, value.ProjectId, value.Sequence });
            entity.Property(value => value.EventType).HasMaxLength(128);
            entity.Property(value => value.DataJson).HasColumnType("jsonb");
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<TrackAnalysisRevision>(entity =>
        {
            entity.HasIndex(value => new { value.ProjectId, value.Number }).IsUnique();
            entity.Property(value => value.SourceFingerprint).HasMaxLength(128);
            entity.Property(value => value.AnalysisJson).HasColumnType("jsonb");
            entity.Property(value => value.ProcessorVersionsJson).HasColumnType("jsonb");
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<TranscriptRevision>(entity =>
        {
            entity.HasIndex(value => new { value.ProjectId, value.Number }).IsUnique();
            entity.Property(value => value.Language).HasMaxLength(16);
            entity.Property(value => value.SourceFingerprint).HasMaxLength(128);
            entity.Property(value => value.ApprovedBySubject).HasMaxLength(255);
            entity.Property(value => value.PhrasesJson).HasColumnType("jsonb");
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<ArtworkPackRevision>(entity =>
        {
            entity.HasIndex(value => new { value.ProjectId, value.Number }).IsUnique();
            entity.HasIndex(value => new { value.ProjectId, value.OperationNumber }).IsUnique();
            entity.Property(value => value.Prompt).HasMaxLength(2_000);
            entity.Property(value => value.CandidateAssetIdsJson).HasColumnType("jsonb");
            entity.Property(value => value.BackgroundAssetIdsJson)
                .HasColumnType("jsonb")
                .HasDefaultValueSql("'[]'::jsonb");
            entity.Property(value => value.CompositionJson).HasColumnType("jsonb");
            entity.Property(value => value.SourceFingerprint).HasMaxLength(128);
            entity.Property(value => value.ApprovedBySubject).HasMaxLength(255);
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<HookSetRevision>(entity =>
        {
            entity.HasIndex(value => new { value.ProjectId, value.Number }).IsUnique();
            entity.Property(value => value.HooksJson).HasColumnType("jsonb");
            entity.Property(value => value.SourceFingerprint).HasMaxLength(128);
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<CampaignPlanRevision>(entity =>
        {
            entity.HasIndex(value => new { value.ProjectId, value.Number }).IsUnique();
            entity.Property(value => value.ItemsJson).HasColumnType("jsonb");
            entity.Property(value => value.SourceFingerprint).HasMaxLength(128);
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<ApiIdempotencyRecord>(entity =>
        {
            entity.HasIndex(value => new { value.WorkspaceId, value.Scope, value.Key }).IsUnique();
            entity.Property(value => value.Scope).HasMaxLength(128);
            entity.Property(value => value.Key).HasMaxLength(255);
            entity.Property(value => value.RequestHash).HasMaxLength(64);
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasIndex(value => value.DedupeKey).IsUnique();
            entity.HasIndex(value => new { value.ProcessedAt, value.CreatedAt });
            entity.Property(value => value.Destination).HasMaxLength(64);
            entity.Property(value => value.MessageType).HasMaxLength(128);
            entity.Property(value => value.DedupeKey).HasMaxLength(255);
            entity.Property(value => value.PayloadJson).HasColumnType("jsonb");
            entity.Property(value => value.LastError).HasMaxLength(1_000);
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<InboxMessage>(entity =>
        {
            entity.HasIndex(value => new { value.Source, value.MessageId }).IsUnique();
            entity.Property(value => value.Source).HasMaxLength(64);
            entity.Property(value => value.MessageId).HasMaxLength(255);
            entity.Property(value => value.PayloadHash).HasMaxLength(64);
            entity.Property(value => value.State).HasMaxLength(32);
            entity.Property(value => value.LastError).HasMaxLength(1_000);
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<BillingCheckout>(entity =>
        {
            entity.HasIndex(value => new { value.WorkspaceId, value.IdempotencyKey }).IsUnique();
            entity.HasIndex(value => value.ExternalSessionId).IsUnique().HasFilter("external_session_id IS NOT NULL");
            entity.Property(value => value.ProductCode).HasMaxLength(64);
            entity.Property(value => value.Currency).HasMaxLength(3);
            entity.Property(value => value.ItemIdsJson).HasColumnType("jsonb");
            entity.Property(value => value.IdempotencyKey).HasMaxLength(255);
            entity.Property(value => value.RequestHash).HasMaxLength(64);
            entity.Property(value => value.ExternalSessionId).HasMaxLength(255);
            entity.Property(value => value.CheckoutUrl).HasMaxLength(2_048);
            entity.Property(value => value.ExternalCustomerId).HasMaxLength(255);
            entity.Property(value => value.ExternalSubscriptionId).HasMaxLength(255);
            entity.Property(value => value.ExternalPaymentIntentId).HasMaxLength(255);
            entity.Property(value => value.ArtworkCompositionHash).HasMaxLength(64);
            entity.Property(value => value.ArtistNameSnapshot).HasMaxLength(160);
            entity.Property(value => value.TrackTitleSnapshot).HasMaxLength(160);
            entity.Property(value => value.AudioFingerprintSnapshot).HasMaxLength(128);
            entity.HasIndex(value => value.ExternalPaymentIntentId).IsUnique().HasFilter("external_payment_intent_id IS NOT NULL");
            entity.HasIndex(value => value.ExternalSubscriptionId).HasFilter("external_subscription_id IS NOT NULL");
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<Entitlement>(entity =>
        {
            entity.HasIndex(value => new { value.CheckoutId, value.ProviderPeriodKey }).IsUnique();
            entity.HasIndex(value => new { value.WorkspaceId, value.ProjectId, value.State });
            entity.Property(value => value.ProductCode).HasMaxLength(64);
            entity.Property(value => value.ItemIdsJson).HasColumnType("jsonb");
            entity.Property(value => value.ProviderPeriodKey).HasMaxLength(255);
            entity.Property(value => value.ExternalSubscriptionId).HasMaxLength(255);
            entity.Property(value => value.ExternalPaymentIntentId).HasMaxLength(255);
            entity.Property(value => value.ExternalInvoiceId).HasMaxLength(255);
            entity.Property(value => value.ArtworkCompositionHash).HasMaxLength(64);
            entity.Property(value => value.ArtistNameSnapshot).HasMaxLength(160);
            entity.Property(value => value.TrackTitleSnapshot).HasMaxLength(160);
            entity.Property(value => value.AudioFingerprintSnapshot).HasMaxLength(128);
            entity.HasIndex(value => value.ExternalInvoiceId).HasFilter("external_invoice_id IS NOT NULL");
            entity.HasIndex(value => value.ExternalPaymentIntentId).HasFilter("external_payment_intent_id IS NOT NULL");
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<WorkspaceArtworkCredit>(entity =>
        {
            entity.HasIndex(value => value.WorkspaceId).IsUnique();
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<ArtworkCreditGrant>(entity =>
        {
            entity.HasIndex(value => value.CheckoutId).IsUnique();
            entity.HasIndex(value => new { value.WorkspaceId, value.Remaining });
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<ArtworkCreditTransaction>(entity =>
        {
            entity.HasIndex(value => new { value.WorkspaceId, value.Reference }).IsUnique();
            entity.Property(value => value.Reason).HasMaxLength(64);
            entity.Property(value => value.Reference).HasMaxLength(255);
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<RenderBatch>(entity =>
        {
            entity.HasIndex(value => new { value.WorkspaceId, value.IdempotencyKey }).IsUnique();
            entity.HasIndex(value => new { value.ProjectId, value.CreatedAt });
            entity.Property(value => value.ItemIdsJson).HasColumnType("jsonb");
            entity.Property(value => value.JobIdsJson).HasColumnType("jsonb");
            entity.Property(value => value.IdempotencyKey).HasMaxLength(255);
            entity.Property(value => value.RequestHash).HasMaxLength(64);
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<RenderItemUsage>(entity =>
        {
            entity.HasIndex(value => new { value.EntitlementId, value.CampaignItemId }).IsUnique();
            entity.HasIndex(value => new { value.WorkspaceId, value.ProjectId });
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampEntities();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        StampEntities();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private static void ConfigureEntity<TEntity>(ModelBuilder modelBuilder, string tableName)
        where TEntity : Entity
    {
        modelBuilder.Entity<TEntity>(entity =>
        {
            entity.ToTable(tableName);
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Version).IsConcurrencyToken();
        });
    }

    private void StampEntities()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var entry in ChangeTracker.Entries<Entity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
                entry.Entity.Version = Math.Max(1, entry.Entity.Version);
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
                entry.Entity.Version++;
            }
        }
    }
}
