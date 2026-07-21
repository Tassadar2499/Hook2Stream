using Hook2Stream.Domain;
using Microsoft.EntityFrameworkCore;

namespace Hook2Stream.Infrastructure.Persistence;

public static class AuthPersistenceModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuthSession>(entity =>
        {
            entity.ToTable("auth_sessions");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Version).IsConcurrencyToken();
            entity.HasIndex(value => value.TokenHash).IsUnique();
            entity.HasIndex(value => new { value.UserId, value.ExpiresAt });
            entity.Property(value => value.TokenHash).HasMaxLength(64).IsFixedLength();
            entity.Property(value => value.CsrfTokenHash).HasMaxLength(64).IsFixedLength();
            entity.HasOne(value => value.User)
                .WithMany()
                .HasForeignKey(value => value.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });

        modelBuilder.Entity<OAuthLoginState>(entity =>
        {
            entity.ToTable("oauth_login_states");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Version).IsConcurrencyToken();
            entity.HasIndex(value => value.StateHash).IsUnique();
            entity.HasIndex(value => new { value.ExpiresAt, value.ConsumedAt });
            entity.Property(value => value.StateHash).HasMaxLength(64).IsFixedLength();
            entity.Property(value => value.ReturnPath).HasMaxLength(512);
            entity.HasQueryFilter(value => value.DeletedAt == null);
        });
    }
}
