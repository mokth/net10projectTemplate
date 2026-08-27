using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class IvLocationConfiguration : IEntityTypeConfiguration<IvLocation>
{
    public void Configure(EntityTypeBuilder<IvLocation> builder)
    {
        builder.ToTable("IvLocation");
        builder.HasKey(e => new { e.CompanyCode, e.BranchCode, e.WarehouseCode, e.LocCode });

        builder.Property(e => e.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.WarehouseCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.LocCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.LocDesc).HasMaxLength(100);
        builder.Property(e => e.LocationCode).HasMaxLength(10);
        builder.Property(e => e.IsActive).HasColumnName("Active").HasDefaultValue(true).ValueGeneratedNever();
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(10);
        builder.Property(e => e.RowVersion).IsRowVersion();

        builder.HasOne(e => e.Warehouse)
            .WithMany(e => e.Locations)
            .HasForeignKey(e => new { e.CompanyCode, e.BranchCode, e.WarehouseCode })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.WarehouseCode })
            .HasDatabaseName("IX_IvLocation_Warehouse");

        builder.HasIndex(e => e.IsActive)
            .HasDatabaseName("IX_IvLocation_IsActive");
    }
}
