using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("Permission");
        builder.HasKey(e => e.PermissionId);

        builder.Property(e => e.PermissionCode).HasMaxLength(50).IsRequired();
        builder.Property(e => e.PermissionName).HasMaxLength(100).IsRequired();
        builder.Property(e => e.PermissionType).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Description).HasMaxLength(250);
        builder.Property(e => e.CreatedBy).HasMaxLength(10);
        builder.Property(e => e.ModifiedBy).HasMaxLength(10);
        builder.Property(e => e.IsActive).HasDefaultValue(true);

        builder.HasIndex(e => e.PermissionCode)
            .IsUnique()
            .HasDatabaseName("UQ_Permission_PermissionCode");
    }
}
