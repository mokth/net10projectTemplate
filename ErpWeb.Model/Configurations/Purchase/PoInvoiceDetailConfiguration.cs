using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Purchase;

public class PoInvoiceDetailConfiguration : IEntityTypeConfiguration<PoInvoiceDetail>
{
    public void Configure(EntityTypeBuilder<PoInvoiceDetail> builder)
    {
        builder.ToTable("POCDNDetail");
        builder.HasKey(e => new { e.CompanyCode, e.BranchCode, e.DocNo, e.Line });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.DocNo).HasMaxLength(30).IsRequired();
        builder.Property(e => e.ICode).HasColumnName("ICode").HasMaxLength(30).IsRequired();
        builder.Property(e => e.IDesc).HasColumnName("IDesc").HasMaxLength(200);
        builder.Property(e => e.CustICode).HasColumnName("CustICode").HasMaxLength(30);
        builder.Property(e => e.Qty).HasPrecision(18, 4);
        builder.Property(e => e.UnitPrice).HasPrecision(18, 4);
        builder.Property(e => e.SellingUom).HasColumnName("SellingUOM").HasMaxLength(10);
        builder.Property(e => e.StdUom).HasColumnName("StdUOM").HasMaxLength(10);
        builder.Property(e => e.WtUom).HasColumnName("WtUOM").HasMaxLength(10);
        builder.Property(e => e.StdQty).HasPrecision(18, 4);
        builder.Property(e => e.WtQty).HasPrecision(18, 4);
        builder.Property(e => e.StdCustPsize).HasColumnName("StdCustPSize").HasPrecision(18, 4);
        builder.Property(e => e.TaxAmt).HasPrecision(18, 2);
        builder.Property(e => e.Amount).HasPrecision(18, 2);
        builder.Property(e => e.CustPo).HasColumnName("CustPO").HasMaxLength(50);
        builder.Property(e => e.Remarks).HasMaxLength(250);
        builder.Property(e => e.ItemGlCode).HasColumnName("ItemGLCode").HasMaxLength(20);
        builder.Property(e => e.TaxGroup).HasMaxLength(20);
        builder.Property(e => e.Discount).HasPrecision(18, 6);
        builder.Property(e => e.ItemDiscount).HasPrecision(18, 6);
        builder.Property(e => e.ItemDiscount1).HasPrecision(18, 6);
        builder.Property(e => e.ItemDiscount2).HasPrecision(18, 6);
        builder.Property(e => e.ItemDiscount3).HasPrecision(18, 6);
        builder.Property(e => e.IDiscountType).HasColumnName("IDiscountType").HasMaxLength(20);
        builder.Property(e => e.IDiscountType1).HasColumnName("IDiscountType1").HasMaxLength(20);
        builder.Property(e => e.NetAmount).HasPrecision(18, 2);
        builder.Property(e => e.PoNo).HasColumnName("PONo").HasMaxLength(30);
        builder.Property(e => e.PoRelNo).HasColumnName("PORelNo");
        builder.Property(e => e.PoLineNo).HasColumnName("POLineNo");
    }
}
