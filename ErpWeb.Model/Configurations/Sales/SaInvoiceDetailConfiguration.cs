using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public class SaInvoiceDetailConfiguration : IEntityTypeConfiguration<SaInvoiceDetail>
{
    public void Configure(EntityTypeBuilder<SaInvoiceDetail> builder)
    {
        builder.ToTable("SaInvoiceDetail");
        builder.HasKey(e => e.Id);

        // Pricing provenance (plan Phase 2): WHICH source priced the line. Explanatory only.
        builder.Property(e => e.PricingSource).HasMaxLength(40);
        builder.Property(e => e.PricingRef).HasMaxLength(60);

        // Price override (plan Phase 4): the engine price is retained only when an operator changed it.
        builder.Property(e => e.OriginalUnitPrice).HasPrecision(18, 4);
        builder.Property(e => e.OverrideReason).HasMaxLength(100);

        builder.Property(e => e.Id).HasColumnName("ID").ValueGeneratedOnAdd();
        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.InvNo).HasMaxLength(30).IsRequired();
        builder.Property(e => e.ICode).HasMaxLength(30);
        builder.Property(e => e.IDesc).HasMaxLength(200);
        builder.Property(e => e.Qty).HasPrecision(18, 4);
        builder.Property(e => e.StdQty).HasPrecision(18, 4);
        builder.Property(e => e.StdUom).HasMaxLength(10);
        builder.Property(e => e.FrWarehouse).HasMaxLength(20);
        builder.Property(e => e.UnitPrice).HasPrecision(18, 4);
        builder.Property(e => e.Amount).HasPrecision(18, 2);
        builder.Property(e => e.ItemDiscount).HasPrecision(18, 6);
        builder.Property(e => e.ItemDiscount2).HasPrecision(18, 6);
        builder.Property(e => e.ItemDiscount3).HasPrecision(18, 6);
        builder.Property(e => e.ItemDiscount4).HasPrecision(18, 6);
        builder.Property(e => e.ItemDiscount5).HasPrecision(18, 6);
        builder.Property(e => e.ItemDiscount6).HasPrecision(18, 6);
        builder.Property(e => e.ItemDiscAmount).HasPrecision(18, 2);
        builder.Property(e => e.ItemDiscAmount1).HasPrecision(18, 2);
        builder.Property(e => e.TaxGrCode).HasMaxLength(20);
        builder.Property(e => e.TaxAmt).HasPrecision(18, 2);
        builder.Property(e => e.NetAmount).HasPrecision(18, 2);
        builder.Property(e => e.LocalAmount).HasPrecision(18, 2);
        builder.Property(e => e.OrderType).HasMaxLength(20);
        builder.Property(e => e.SellingGlCode).HasMaxLength(20);
        builder.Property(e => e.Classification).HasMaxLength(50);
        builder.Property(e => e.Remarks).HasMaxLength(250);
        builder.Property(e => e.SoNo).HasColumnName("SONo").HasMaxLength(30).IsRequired();
        builder.Property(e => e.SoLine).HasColumnName("SOLine");
        builder.Property(e => e.CustRel);
        builder.Property(e => e.LinkDo).IsRequired();
        builder.Property(e => e.DoNo).HasColumnName("DONo").HasMaxLength(30).IsRequired();
        builder.Property(e => e.DoLine).HasColumnName("DOLine");
        builder.Property(e => e.SoConsumedQty).HasPrecision(18, 4);

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.InvNo, e.Line })
            .IsUnique()
            .HasDatabaseName("UQ_SaInvoiceDetail_Company_Branch_InvNo_Line");

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.SoNo, e.SoLine })
            .HasDatabaseName("IX_SaInvoiceDetail_Company_Branch_SoNo_SoLine");
    }
}
