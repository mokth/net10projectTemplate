using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Purchase;

public class PoOrderDetailConfiguration : IEntityTypeConfiguration<PoOrderDetail>
{
    public void Configure(EntityTypeBuilder<PoOrderDetail> builder)
    {
        builder.ToTable("PODetail");
        builder.HasKey(e => new { e.CompanyCode, e.BranchCode, e.PoNo, e.PoRelNo, e.Line });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.PoNo).HasColumnName("PONo").HasMaxLength(30).IsRequired();
        builder.Property(e => e.PoRelNo).HasColumnName("PORelNo").IsRequired();
        builder.Property(e => e.PrNo).HasColumnName("PRNo").HasMaxLength(30);
        builder.Property(e => e.PrLineNo).HasColumnName("PRLineNo");
        builder.Property(e => e.ICode).HasColumnName("ICode").HasMaxLength(30);
        builder.Property(e => e.IDesc).HasColumnName("IDes").HasMaxLength(200);
        builder.Property(e => e.PoUnitPrice).HasColumnName("POUnitPrice").HasPrecision(18, 4);
        builder.Property(e => e.PoQty).HasColumnName("POQty").HasPrecision(18, 4);
        builder.Property(e => e.PoPurQty).HasColumnName("POPurQty").HasPrecision(18, 4);
        builder.Property(e => e.WtQty).HasPrecision(18, 4);
        builder.Property(e => e.Amount).HasPrecision(18, 2);
        builder.Property(e => e.RecvQty).HasPrecision(18, 4);
        builder.Property(e => e.ReturnQty).HasPrecision(18, 4);
        builder.Property(e => e.ReturnQtyCn).HasColumnName("ReturnQtyCN").HasPrecision(18, 4);
        builder.Property(e => e.BalanceQty).HasPrecision(18, 4);
        builder.Property(e => e.OverRecvQty).HasPrecision(18, 4);
        builder.Property(e => e.InvoicedQty).HasPrecision(18, 4);
        builder.Property(e => e.PackSz).HasPrecision(18, 4);
        builder.Property(e => e.StdUom).HasColumnName("StdUOM").HasMaxLength(10);
        builder.Property(e => e.WtUom).HasColumnName("WtUOM").HasMaxLength(10);
        builder.Property(e => e.PurchaseUom).HasColumnName("PurchaseUOM").HasMaxLength(10);
        builder.Property(e => e.EtaDate).HasColumnName("ETADate").HasColumnType("datetime2");
        builder.Property(e => e.CurCode).HasMaxLength(20);
        builder.Property(e => e.Remarks).HasMaxLength(250);
        builder.Property(e => e.PoDesc).HasColumnName("PODesc").HasMaxLength(200);
        builder.Property(e => e.RecvDate).HasColumnType("datetime2");
        builder.Property(e => e.RepairType).HasMaxLength(20);
        builder.Property(e => e.Discount).HasPrecision(18, 6);
        builder.Property(e => e.ItemDiscount).HasPrecision(18, 6);
        builder.Property(e => e.DiscountType).HasMaxLength(20);
        builder.Property(e => e.NetAmount).HasPrecision(18, 2);
        builder.Property(e => e.ItemDiscount1).HasPrecision(18, 6);
        builder.Property(e => e.DiscountType1).HasMaxLength(20);
        builder.Property(e => e.CjNo).HasColumnName("CJNo").HasMaxLength(30);
        builder.Property(e => e.CjRelNo).HasColumnName("CJRelNo");
        builder.Property(e => e.ProjId).HasColumnName("ProjID").HasMaxLength(20);
        builder.Property(e => e.VendorPartNo).HasMaxLength(50);
        builder.Property(e => e.TaxGroup).HasMaxLength(20);
        builder.Property(e => e.TaxAmount).HasPrecision(18, 2);
        builder.Property(e => e.M2UnitPrice).HasColumnName("M2UnitPrice").HasPrecision(18, 4);
        builder.Property(e => e.ToWarehouse).HasMaxLength(20);
        builder.Property(e => e.Requester).HasMaxLength(50);

        // Match FK_PODetail_POOrder in scripts/create-po-order.sql.
        // Without this mapping the Order navigation resolves through a convention
        // relationship whose composite key EF cannot translate to SQL.
        builder.HasOne(e => e.Order)
            .WithMany(o => o.Details)
            .HasForeignKey(e => new { e.CompanyCode, e.BranchCode, e.PoNo, e.PoRelNo })
            .HasPrincipalKey(o => new { o.CompanyCode, o.BranchCode, o.PoNo, o.PoRelNo })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
