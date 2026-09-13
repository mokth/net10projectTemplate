using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Purchase;

public class PoPrDetailConfiguration : IEntityTypeConfiguration<PoPrDetail>
{
    public void Configure(EntityTypeBuilder<PoPrDetail> builder)
    {
        builder.ToTable("POPRDtl");
        builder.HasKey(e => new { e.CompanyCode, e.BranchCode, e.PrNo, e.Line });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.PrNo).HasColumnName("PRNo").HasMaxLength(30).IsRequired();
        builder.Property(e => e.EtaDt).HasColumnName("ETADt").HasColumnType("datetime2");
        builder.Property(e => e.OneTimeItemYn).HasColumnName("OneTime_ItemYN");
        builder.Property(e => e.ICode).HasColumnName("ICd").HasMaxLength(30);
        builder.Property(e => e.IDesc).HasColumnName("IDes").HasMaxLength(200);
        builder.Property(e => e.Category).HasMaxLength(20);
        builder.Property(e => e.Qty).HasPrecision(18, 4);
        builder.Property(e => e.PackSz).HasPrecision(18, 4);
        builder.Property(e => e.StdUom).HasColumnName("StdUOM").HasMaxLength(10);
        builder.Property(e => e.PurchaseQty).HasPrecision(18, 4);
        builder.Property(e => e.PurchaseUom).HasColumnName("PurchaseUOM").HasMaxLength(10);
        builder.Property(e => e.Currency).HasMaxLength(20);
        builder.Property(e => e.UnitPrice).HasPrecision(18, 4);
        builder.Property(e => e.VendorCd).HasMaxLength(60);
        builder.Property(e => e.VendNm).HasMaxLength(200);
        builder.Property(e => e.Purpose).HasMaxLength(250);
        builder.Property(e => e.Status).HasColumnName("PRStat").HasMaxLength(20);
        builder.Property(e => e.StdQty).HasPrecision(18, 4);
        builder.Property(e => e.WtQty).HasPrecision(18, 4);
        builder.Property(e => e.WtUom).HasColumnName("WtUOM").HasMaxLength(10);
        builder.Property(e => e.PaymentTerm).HasMaxLength(20);
        builder.Property(e => e.BuyingTerm).HasMaxLength(20);
        builder.Property(e => e.RepairType).HasMaxLength(20);
        builder.Property(e => e.Amount).HasPrecision(18, 2);
        builder.Property(e => e.PoNo).HasColumnName("PONo").HasMaxLength(30);
        builder.Property(e => e.TaxGroup).HasMaxLength(20);
        builder.Property(e => e.TaxAmount).HasPrecision(18, 2);
        builder.Property(e => e.ToWarehouse).HasMaxLength(20);
        builder.Property(e => e.SoNo).HasColumnName("SONo").HasMaxLength(30);
        builder.Property(e => e.SoLine).HasColumnName("SOLine");
    }
}
