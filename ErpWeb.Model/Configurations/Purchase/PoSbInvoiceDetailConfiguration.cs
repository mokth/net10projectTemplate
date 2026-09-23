using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Purchase;

public class PoSbInvoiceDetailConfiguration : IEntityTypeConfiguration<PoSbInvoiceDetail>
{
    public void Configure(EntityTypeBuilder<PoSbInvoiceDetail> builder)
    {
        builder.ToTable("POSbInvoiceDetail");
        builder.HasKey(e => new { e.CompanyCode, e.BranchCode, e.DocNo, e.Line });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.DocNo).HasMaxLength(30).IsRequired();

        builder.Property(e => e.ICode).HasColumnName("ICode").HasMaxLength(30);
        builder.Property(e => e.IDesc).HasColumnName("IDesc").HasMaxLength(200);
        builder.Property(e => e.Qty).HasPrecision(18, 4);
        builder.Property(e => e.UnitPrice).HasPrecision(18, 4);
        builder.Property(e => e.SellingUom).HasColumnName("SellingUOM").HasMaxLength(10);
        builder.Property(e => e.StdUom).HasColumnName("StdUOM").HasMaxLength(10);
        builder.Property(e => e.Amount).HasPrecision(18, 2);
        builder.Property(e => e.TaxAmt).HasPrecision(18, 2);
        builder.Property(e => e.NetAmount).HasPrecision(18, 2);
        builder.Property(e => e.TaxGroup).HasMaxLength(20);
        builder.Property(e => e.IsInclusive).IsRequired();

        builder.Property(e => e.Discount).HasPrecision(18, 6);
        builder.Property(e => e.ItemDiscount).HasPrecision(18, 6);
        builder.Property(e => e.ItemDiscount1).HasPrecision(18, 6);
        builder.Property(e => e.IDiscountType).HasColumnName("IDiscountType").HasMaxLength(20);
        builder.Property(e => e.IDiscountType1).HasColumnName("IDiscountType1").HasMaxLength(20);

        // LHDN item classification code — the validator refuses a blank one on every line.
        builder.Property(e => e.Classification).HasMaxLength(50);
        builder.Property(e => e.Remarks).HasMaxLength(250);

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.ICode })
            .HasDatabaseName("IX_POSbInvoiceDetail_Company_Branch_ICode");
    }
}
