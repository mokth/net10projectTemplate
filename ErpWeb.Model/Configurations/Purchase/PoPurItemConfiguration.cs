using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Purchase;

public class PoPurItemConfiguration : IEntityTypeConfiguration<PoPurItem>
{
    public void Configure(EntityTypeBuilder<PoPurItem> builder)
    {
        builder.ToTable("POPurItem");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("ID").ValueGeneratedOnAdd();
        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(10);
        builder.Property(e => e.LocationCode).HasMaxLength(10);
        builder.Property(e => e.ICode).HasColumnName("ICode").HasMaxLength(30).IsRequired();
        builder.Property(e => e.IDesc).HasColumnName("IDesc").HasMaxLength(200);
        builder.Property(e => e.Category).HasMaxLength(20);
        builder.Property(e => e.SubCategory).HasMaxLength(20);
        builder.Property(e => e.Dept).HasMaxLength(20);
        builder.Property(e => e.PurQty).HasPrecision(18, 4);
        builder.Property(e => e.PurUom).HasColumnName("PurUOM").HasMaxLength(10);
        builder.Property(e => e.Vendor).HasMaxLength(60).IsRequired();
        builder.Property(e => e.VendName).HasMaxLength(200);
        builder.Property(e => e.VendorPartNo).HasMaxLength(50);
        builder.Property(e => e.Currency).HasMaxLength(20);
        builder.Property(e => e.UnitPrice).HasPrecision(18, 4);
        builder.Property(e => e.Moq).HasColumnName("MOQ").HasPrecision(18, 4);
        builder.Property(e => e.Status).HasMaxLength(20);
        builder.Property(e => e.Remarks).HasMaxLength(250);
        builder.Property(e => e.PurchaseGlCode).HasColumnName("PurchaseGLCode").HasMaxLength(20);

        builder.Property(e => e.CreatedDate).HasColumnName("Created").HasColumnType("datetime2");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(20);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated").HasColumnType("datetime2");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(20);
        builder.Property(e => e.RowVersion).IsRowVersion();

        builder.HasIndex(e => new { e.CompanyCode, e.ICode, e.Vendor })
            .IsUnique()
            .HasDatabaseName("UX_POPurItem_Company_ICode_Vendor");
    }
}
