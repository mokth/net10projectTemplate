using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class IvClassConfiguration : IEntityTypeConfiguration<IvClass>
{
    public void Configure(EntityTypeBuilder<IvClass> builder)
    {
        builder.ToTable("IvClass");
        builder.HasKey(e => new { e.CompanyCode, e.IClassCode });

        builder.Property(e => e.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.IClassCode).HasColumnName("IClassCode").HasMaxLength(30).IsRequired();
        builder.Property(e => e.IDesc).HasColumnName("IDesc").HasMaxLength(200);
        builder.Property(e => e.BranchCode).HasMaxLength(5);
        builder.Property(e => e.LocationCode).HasMaxLength(10);
        builder.Property(e => e.IsActive).HasColumnName("Active").HasDefaultValue(true).ValueGeneratedNever();
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(10);
        builder.Property(e => e.RowVersion).IsRowVersion();

        builder.HasIndex(e => e.IsActive)
            .HasDatabaseName("IX_IvClass_IsActive");
    }
}
