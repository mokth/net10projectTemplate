using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class PasswordResetRequestConfiguration : IEntityTypeConfiguration<PasswordResetRequest>
{
    public void Configure(EntityTypeBuilder<PasswordResetRequest> builder)
    {
        builder.ToTable("PasswordResetRequest");
        builder.HasKey(e => e.PasswordResetRequestId);

        builder.Property(e => e.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.Status).HasMaxLength(20).IsRequired();
        builder.Property(e => e.ResolvedBy).HasMaxLength(10);

        builder.HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserUid)
            .OnDelete(DeleteBehavior.Restrict);

        // One pending request per user, enforced at the database level so duplicate submissions race
        // safely. SQLite (tests) strips the filtered index; uniqueness is then enforced in code.
        builder.HasIndex(e => e.UserUid)
            .HasFilter("[Status] = N'PENDING'")
            .IsUnique();
    }
}
