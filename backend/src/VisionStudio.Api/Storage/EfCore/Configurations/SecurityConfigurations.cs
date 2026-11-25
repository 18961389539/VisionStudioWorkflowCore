using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VisionStudio.Api.Storage.EfCore.Entities;

namespace VisionStudio.Api.Storage.EfCore.Configurations;

internal sealed class SecurityUserConfiguration : IEntityTypeConfiguration<SecurityUserEntity>
{
    public void Configure(EntityTypeBuilder<SecurityUserEntity> b)
    {
        b.ToTable("security_users");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.Username).HasColumnName("username");
        b.Property(x => x.NormalizedUsername).HasColumnName("normalized_username");
        b.Property(x => x.DisplayName).HasColumnName("display_name");
        b.Property(x => x.Role).HasColumnName("role");
        b.Property(x => x.Enabled).HasColumnName("enabled").HasConversion<int>();
        b.Property(x => x.PasswordSalt).HasColumnName("password_salt");
        b.Property(x => x.PasswordHash).HasColumnName("password_hash");
        b.Property(x => x.PasswordIterations).HasColumnName("password_iterations");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.LastLoginAt).HasColumnName("last_login_at");
        b.Property(x => x.PasswordChangedAt).HasColumnName("password_changed_at");
        b.Property(x => x.FailedAttempts).HasColumnName("failed_attempts").HasDefaultValue(0);
        b.Property(x => x.LockedUntil).HasColumnName("locked_until");

        b.HasIndex(x => x.NormalizedUsername).IsUnique();
        b.HasIndex(x => new { x.Role, x.Enabled }).HasDatabaseName("ix_security_users_role");
    }
}

internal sealed class SecuritySessionConfiguration : IEntityTypeConfiguration<SecuritySessionEntity>
{
    public void Configure(EntityTypeBuilder<SecuritySessionEntity> b)
    {
        b.ToTable("security_sessions");
        b.HasKey(x => x.TokenHash);
        b.Property(x => x.TokenHash).HasColumnName("token_hash");
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        b.Property(x => x.LastSeenAt).HasColumnName("last_seen_at");
        b.Property(x => x.RevokedAt).HasColumnName("revoked_at");

        b.HasOne<SecurityUserEntity>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.UserId, x.ExpiresAt }).HasDatabaseName("ix_security_sessions_user").IsDescending(false, true);
        b.HasIndex(x => new { x.ExpiresAt, x.RevokedAt }).HasDatabaseName("ix_security_sessions_expiry");
    }
}

internal sealed class SecurityServiceTokenConfiguration : IEntityTypeConfiguration<SecurityServiceTokenEntity>
{
    public void Configure(EntityTypeBuilder<SecurityServiceTokenEntity> b)
    {
        b.ToTable("security_service_tokens");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.Name).HasColumnName("name").UseCollation("NOCASE");
        b.Property(x => x.TokenHash).HasColumnName("token_hash");
        b.Property(x => x.Prefix).HasColumnName("prefix");
        b.Property(x => x.Scopes).HasColumnName("scopes");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        b.Property(x => x.LastUsedAt).HasColumnName("last_used_at");
        b.Property(x => x.RevokedAt).HasColumnName("revoked_at");

        b.HasIndex(x => x.Name).IsUnique();
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_security_service_tokens_created").IsDescending();
        b.HasIndex(x => x.Prefix).HasDatabaseName("ix_security_service_tokens_prefix");
    }
}

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEventEntity>
{
    public void Configure(EntityTypeBuilder<AuditEventEntity> b)
    {
        b.ToTable("audit_events");
        b.HasKey(x => x.Seq);
        b.Property(x => x.Seq).HasColumnName("seq").ValueGeneratedOnAdd();
        b.Property(x => x.At).HasColumnName("at");
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.Username).HasColumnName("username");
        b.Property(x => x.Role).HasColumnName("role");
        b.Property(x => x.Action).HasColumnName("action");
        b.Property(x => x.Resource).HasColumnName("resource");
        b.Property(x => x.Method).HasColumnName("method");
        b.Property(x => x.Path).HasColumnName("path");
        b.Property(x => x.StatusCode).HasColumnName("status_code");
        b.Property(x => x.Success).HasColumnName("success").HasConversion<int>();
        b.Property(x => x.Target).HasColumnName("target");
        b.Property(x => x.CorrelationId).HasColumnName("correlation_id");
        b.Property(x => x.ClientIp).HasColumnName("client_ip");
        b.Property(x => x.PrevHash).HasColumnName("prev_hash");
        b.Property(x => x.Hash).HasColumnName("hash");

        b.HasIndex(x => x.At).HasDatabaseName("ix_audit_events_at").IsDescending();
        b.HasIndex(x => new { x.Username, x.At }).HasDatabaseName("ix_audit_events_user_at").IsDescending(false, true);
        b.HasIndex(x => new { x.Action, x.At }).HasDatabaseName("ix_audit_events_action_at").IsDescending(false, true);
    }
}
