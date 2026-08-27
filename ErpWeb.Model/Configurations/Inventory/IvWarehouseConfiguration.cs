using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class IvWarehouseConfiguration : IEntityTypeConfiguration<IvWarehouse>
{
    public void Configure(EntityTypeBuilder<IvWarehouse> builder)
    {
        builder.ToTable("IvWarehouse");
        builder.HasKey(e => new { e.CompanyCode, e.BranchCode, e.WarehouseCode });

        builder.Property(e => e.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.WarehouseCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.WarehouseDesc).HasMaxLength(100);
        builder.Property(e => e.WarehouseType).HasMaxLength(20);
        builder.Property(e => e.WarehouseRemark).HasMaxLength(250);
        builder.Property(e => e.LocationCode).HasMaxLength(10);
        builder.Property(e => e.IsActive).HasColumnName("Active").HasDefaultValue(true).ValueGeneratedNever();
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(10);
        builder.Property(e => e.RowVersion).IsRowVersion();

        builder.HasIndex(e => e.IsActive)
            .HasDatabaseName("IX_IvWarehouse_IsActive");
    }
}
