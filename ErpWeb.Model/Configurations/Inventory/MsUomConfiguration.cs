using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class MsUomConfiguration : IEntityTypeConfiguration<MsUom>
{
    public void Configure(EntityTypeBuilder<MsUom> builder)
    {
        builder.ToTable("MsUOM");
        builder.HasKey(e => new { e.CompanyCode, e.UomCode });

        builder.Property(e => e.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.UomCode).HasColumnName("UOMCode").HasMaxLength(10).IsRequired();
        builder.Property(e => e.UomDesc).HasColumnName("UOMDesc").HasMaxLength(100);
        builder.Property(e => e.UneceUom).HasColumnName("UNECE_UOM").HasMaxLength(10);
        builder.Property(e => e.BranchCode).HasMaxLength(5);
        builder.Property(e => e.LocationCode).HasMaxLength(10);
        builder.Property(e => e.IsActive).HasColumnName("Active").HasDefaultValue(true).ValueGeneratedNever();
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(10);
        builder.Property(e => e.RowVersion).IsRowVersion();

        builder.HasIndex(e => e.IsActive)
            .HasDatabaseName("IX_MsUOM_IsActive");
    }
}
