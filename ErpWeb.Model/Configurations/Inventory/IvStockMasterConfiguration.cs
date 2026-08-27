using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class IvStockMasterConfiguration : IEntityTypeConfiguration<IvStockMaster>
{
    public void Configure(EntityTypeBuilder<IvStockMaster> builder)
    {
        builder.ToTable("IvStockMaster");
        builder.HasKey(e => new { e.CompanyCode, e.ICode });

        builder.Property(e => e.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.ICode).HasColumnName("ICode").HasMaxLength(30).IsRequired();
        builder.Property(e => e.IDesc).HasColumnName("IDesc").HasMaxLength(200);
        builder.Property(e => e.IType).HasColumnName("IType").HasMaxLength(20);
        builder.Property(e => e.IClassCode).HasColumnName("IClass").HasMaxLength(30);
        builder.Property(e => e.ISubClassCode).HasColumnName("ISubclass").HasMaxLength(30);
        builder.Property(e => e.StdUom).HasColumnName("StdUOM").HasMaxLength(10);
        builder.Property(e => e.PurUom).HasColumnName("PurUOM").HasMaxLength(10);
        builder.Property(e => e.SellingUom).HasColumnName("SellingUOM").HasMaxLength(10);
        builder.Property(e => e.StockControl).HasDefaultValue(true).ValueGeneratedNever();
        builder.Property(e => e.LotControl).HasDefaultValue(false).ValueGeneratedNever();
        builder.Property(e => e.DefWarehouse).HasMaxLength(20);
        builder.Property(e => e.DefLocation).HasMaxLength(10);
        builder.Property(e => e.MinStock).HasPrecision(18, 4);
        builder.Property(e => e.MaxStock).HasPrecision(18, 4);
        builder.Property(e => e.StdPackSize).HasPrecision(18, 4);
        builder.Property(e => e.PurStdPackSize).HasPrecision(18, 4);
        builder.Property(e => e.SellingPrice).HasPrecision(18, 4);
        // Legacy SQL column is decimal(18,6); keep scale so materialization matches the DB.
        builder.Property(e => e.PurchasePrice).HasPrecision(18, 6);
        builder.Property(e => e.SellingGlCode).HasColumnName("SellingGLCode").HasMaxLength(20);
        builder.Property(e => e.PurchaseGlCode).HasColumnName("PurchaseGLCode").HasMaxLength(20);
        builder.Property(e => e.Size).HasMaxLength(50);
        builder.Property(e => e.Color).HasMaxLength(50);
        builder.Property(e => e.Brand).HasMaxLength(50);
        builder.Property(e => e.Barcode).HasMaxLength(50);
        builder.Property(e => e.ImagePath).HasMaxLength(500);
        builder.Property(e => e.TaxGroup).HasMaxLength(20);
        builder.Property(e => e.PurchaseTaxGroup).HasMaxLength(20);
        builder.Property(e => e.Classification).HasMaxLength(50);
        builder.Property(e => e.BranchCode).HasMaxLength(5);
        builder.Property(e => e.LocationCode).HasMaxLength(10);
        builder.Property(e => e.IsActive).HasColumnName("Active").HasDefaultValue(true).ValueGeneratedNever();
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(10);
        builder.Property(e => e.RowVersion).IsRowVersion();

        builder.HasIndex(e => new { e.CompanyCode, e.IType })
            .HasDatabaseName("IX_IvStockMaster_Company_IType");

        builder.HasIndex(e => new { e.CompanyCode, e.IClassCode })
            .HasDatabaseName("IX_IvStockMaster_Company_IClass");

        builder.HasIndex(e => e.IsActive)
            .HasDatabaseName("IX_IvStockMaster_IsActive");
    }
}
