using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class PasswordResetTokenConfiguration : IEntityTypeConfiguration<PasswordResetToken>
{
    public void Configure(EntityTypeBuilder<PasswordResetToken> builder)
    {
        builder.ToTable("PasswordResetToken");
        builder.HasKey(e => e.PasswordResetTokenId);

        builder.Property(e => e.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.TokenHash).HasMaxLength(64).IsRequired();
        builder.Property(e => e.RequestedIp).HasMaxLength(45);

        // Lookup is always "find the row for this hash", so the hash itself is the natural key.
        // Unique so a (statistically impossible) collision can never authenticate two accounts.
        builder.HasIndex(e => e.TokenHash)
            .IsUnique()
            .HasDatabaseName("UQ_PasswordResetToken_TokenHash");

        // Supports "invalidate every outstanding token for this user" on re-issue.
        builder.HasIndex(e => new { e.UserUid, e.UsedAtUtc })
            .HasDatabaseName("IX_PasswordResetToken_User_Used");

        builder.HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserUid)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
