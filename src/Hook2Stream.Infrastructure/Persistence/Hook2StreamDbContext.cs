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

        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.HasIndex(value => value.ClerkSubject).IsUnique();
            entity.Property(value => value.ClerkSubject).HasMaxLength(255);
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
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<RightsAttestation>(entity =>
        {
            entity.HasIndex(value => value.ProjectId).IsUnique();
            entity.Property(value => value.ActorSubject).HasMaxLength(255);
            entity.Property(value => value.PolicyVersion).HasMaxLength(64);
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
