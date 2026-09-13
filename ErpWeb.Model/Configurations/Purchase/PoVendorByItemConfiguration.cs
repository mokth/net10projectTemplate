using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Purchase;

public class PoVendorByItemConfiguration : IEntityTypeConfiguration<PoVendorByItem>
{
    public void Configure(EntityTypeBuilder<PoVendorByItem> builder)
    {
        builder.ToTable("POVendorByItem");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("ID").ValueGeneratedOnAdd();
        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(10);
        builder.Property(e => e.LocationCode).HasMaxLength(10);
        builder.Property(e => e.Vendor).HasMaxLength(60);
        builder.Property(e => e.VendorPartNo).HasMaxLength(50);
        builder.Property(e => e.Country).HasMaxLength(50);
        builder.Property(e => e.Currency).HasMaxLength(20);
        builder.Property(e => e.UnitPrice).HasPrecision(18, 4);
        builder.Property(e => e.PurUom).HasColumnName("PurUOM").HasMaxLength(10);
        builder.Property(e => e.PackSize).HasPrecision(18, 4);
        builder.Property(e => e.Tolerance).HasPrecision(18, 4);
        builder.Property(e => e.PriceTolerance).HasPrecision(18, 4);
        builder.Property(e => e.ICode).HasColumnName("ICode").HasMaxLength(30);
        builder.Property(e => e.IDesc).HasColumnName("IDesc").HasMaxLength(200);
        builder.Property(e => e.Buyer).HasMaxLength(20);
        builder.Property(e => e.OrdLevel).HasPrecision(18, 4);
        builder.Property(e => e.SafetyStock).HasPrecision(18, 4);
        builder.Property(e => e.Status).HasMaxLength(20);
        builder.Property(e => e.PayCode).HasMaxLength(20);
        builder.Property(e => e.MaterialType).HasMaxLength(50);
        builder.Property(e => e.PoDesc).HasColumnName("PODesc").HasMaxLength(200);

        builder.Property(e => e.CreatedDate).HasColumnName("Created").HasColumnType("datetime2");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(20);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated").HasColumnType("datetime2");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(20);
        builder.Property(e => e.RowVersion).IsRowVersion();

        builder.HasIndex(e => new { e.CompanyCode, e.ICode, e.Vendor })
            .HasDatabaseName("IX_POVendorByItem_Company_ICode_Vendor");
    }
}
