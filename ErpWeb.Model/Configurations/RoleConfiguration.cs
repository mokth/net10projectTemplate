using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("Role");
        builder.HasKey(e => e.RoleId);

        builder.Property(e => e.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.RoleCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.RoleName).HasMaxLength(100).IsRequired();
        builder.Property(e => e.CreatedBy).HasMaxLength(10);
        builder.Property(e => e.ModifiedBy).HasMaxLength(10);
        builder.Property(e => e.IsActive).HasDefaultValue(true);

        builder.HasIndex(e => new { e.CompanyCode, e.RoleCode })
            .IsUnique()
            .HasDatabaseName("UQ_Role_Company_RoleCode");

        builder.HasIndex(e => e.CompanyCode);
    }
}
