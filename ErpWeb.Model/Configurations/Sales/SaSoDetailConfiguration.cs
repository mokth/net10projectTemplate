using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public class SaSoDetailConfiguration : IEntityTypeConfiguration<SaSoDetail>
{
    public void Configure(EntityTypeBuilder<SaSoDetail> builder)
    {
        builder.ToTable("SaSODetail");
        builder.HasKey(e => new { e.CompanyCode, e.BranchCode, e.SoNo, e.CustRel, e.Line });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.SoNo).HasColumnName("SONo").HasMaxLength(30).IsRequired();
        builder.Property(e => e.CustRel).IsRequired();
        builder.Property(e => e.ICode).HasColumnName("ICode").HasMaxLength(30);
        builder.Property(e => e.IDesc).HasColumnName("IDesc").HasMaxLength(200);
        builder.Property(e => e.CustICode).HasColumnName("CustICode").HasMaxLength(30);
        builder.Property(e => e.OrderQty).HasPrecision(18, 4);
        builder.Property(e => e.ShippedQty).HasPrecision(18, 4);
        builder.Property(e => e.BalanceQty).HasPrecision(18, 4);
        builder.Property(e => e.DeliveredQty).HasPrecision(18, 4);
        builder.Property(e => e.InvoicedQty).HasPrecision(18, 4);
        builder.Property(e => e.WrittenOffQty).HasPrecision(18, 4);
        builder.Property(e => e.UnitPrice).HasPrecision(18, 4);
        builder.Property(e => e.SellingUom).HasColumnName("SellingUOM").HasMaxLength(10);
        builder.Property(e => e.StdUom).HasColumnName("StdUOM").HasMaxLength(10);
        builder.Property(e => e.WtUom).HasColumnName("WtUOM").HasMaxLength(10);
        builder.Property(e => e.StdQty).HasPrecision(18, 4);
        builder.Property(e => e.WtQty).HasPrecision(18, 4);
        builder.Property(e => e.StdPsize).HasColumnName("StdPSize").HasPrecision(18, 4);
        builder.Property(e => e.TaxAmt).HasPrecision(18, 2);
        builder.Property(e => e.OrderType).HasMaxLength(20);
        builder.Property(e => e.Remarks).HasMaxLength(250);
        builder.Property(e => e.Amount).HasPrecision(18, 2);
        builder.Property(e => e.ItemGlCode).HasColumnName("ItemGLCode").HasMaxLength(20);
        builder.Property(e => e.Discount).HasPrecision(18, 6);
        builder.Property(e => e.NetAmount).HasPrecision(18, 2);
        builder.Property(e => e.ItemDiscount).HasPrecision(18, 6);
        builder.Property(e => e.ItemDiscount2).HasPrecision(18, 6);
        builder.Property(e => e.ItemDiscount3).HasPrecision(18, 6);
        builder.Property(e => e.ItemDiscount4).HasPrecision(18, 6);
        builder.Property(e => e.ItemDiscount5).HasPrecision(18, 6);
        builder.Property(e => e.ItemDiscount6).HasPrecision(18, 6);
        builder.Property(e => e.ItemDiscAmount).HasPrecision(18, 2);
        builder.Property(e => e.ItemDiscAmount1).HasPrecision(18, 2);
        builder.Property(e => e.IDiscountType).HasColumnName("IDiscountType").HasMaxLength(20);
        builder.Property(e => e.Warehouse).HasMaxLength(20);
        builder.Property(e => e.TaxGroup).HasMaxLength(20);
        builder.Property(e => e.LocalAmount).HasPrecision(18, 2);
        builder.Property(e => e.Classification).HasMaxLength(50);
        builder.Property(e => e.DeliveryDate).HasColumnType("datetime2");
        builder.Property(e => e.Eta).HasColumnName("ETA").HasColumnType("datetime2");
        builder.Property(e => e.Etd).HasColumnName("ETD").HasColumnType("datetime2");
    }
}
