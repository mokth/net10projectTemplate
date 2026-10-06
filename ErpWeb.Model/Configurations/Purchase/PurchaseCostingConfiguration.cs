using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Purchase;

public sealed class PurchaseReceiptCostSettlementConfiguration
    : IEntityTypeConfiguration<PurchaseReceiptCostSettlement>
{
    public void Configure(EntityTypeBuilder<PurchaseReceiptCostSettlement> builder)
    {
        builder.ToTable("PurchaseReceiptCostSettlement", table =>
        {
            table.HasCheckConstraint("CK_PurchaseReceiptCostSettlement_Qty", "[SettledBaseQty] > 0");
            table.HasCheckConstraint("CK_PurchaseReceiptCostSettlement_Amounts",
                "[ReceiptCommercialBaseAmount] >= 0 AND [ReceiptValuationBaseAmount] >= 0 AND [AllocatedActualBaseAmount] >= 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.PiDocNo).HasMaxLength(30).IsRequired();
        builder.Property(x => x.PoNo).HasMaxLength(50).IsRequired();
        builder.Property(x => x.ItemCode).HasMaxLength(30).IsRequired();
        foreach (var property in new[]
                 {
                     nameof(PurchaseReceiptCostSettlement.SettledBaseQty),
                     nameof(PurchaseReceiptCostSettlement.ReceiptCommercialUnitCost),
                     nameof(PurchaseReceiptCostSettlement.ReceiptCommercialBaseAmount),
                     nameof(PurchaseReceiptCostSettlement.ReceiptValuationUnitCost),
                     nameof(PurchaseReceiptCostSettlement.ReceiptValuationBaseAmount),
                     nameof(PurchaseReceiptCostSettlement.AllocatedActualBaseAmount),
                     nameof(PurchaseReceiptCostSettlement.CommercialVarianceAmount),
                     nameof(PurchaseReceiptCostSettlement.ValuationVarianceAmount)
                 })
            builder.Property<decimal>(property).HasPrecision(19, 6);
        builder.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(7)");
        builder.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
        builder.HasOne(x => x.ReceiptValuationFact).WithMany()
            .HasForeignKey(x => x.ReceiptValuationFactId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.StockPosting).WithMany()
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.StockPostingId })
            .HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ReversesSettlement).WithMany()
            .HasForeignKey(x => x.ReversesSettlementId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.PoNo, x.PoRelNo, x.PoLineNo, x.ReceiptValuationFactId })
            .HasDatabaseName("IX_PurchaseReceiptCostSettlement_Receipt");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.PiDocNo, x.PiCostingRevision, x.PiLineNo })
            .HasDatabaseName("IX_PurchaseReceiptCostSettlement_Invoice");
        builder.HasIndex(x => x.ReversesSettlementId)
            .IsUnique().HasFilter("[ReversesSettlementId] IS NOT NULL")
            .HasDatabaseName("UX_PurchaseReceiptCostSettlement_Reversal");
    }
}

public sealed class PurchaseCostAdjustmentConfiguration : IEntityTypeConfiguration<PurchaseCostAdjustment>
{
    public void Configure(EntityTypeBuilder<PurchaseCostAdjustment> builder)
    {
        builder.ToTable("PurchaseCostAdjustment", table =>
        {
            table.HasCheckConstraint("CK_PurchaseCostAdjustment_Qty", "[BaseQty] >= 0");
            table.HasCheckConstraint("CK_PurchaseCostAdjustment_Amounts",
                "[ActualBaseAmount] >= 0 AND [ReferenceBaseAmount] >= 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.AdjustmentType).HasMaxLength(30).IsRequired();
        builder.Property(x => x.SourceDocumentType).HasMaxLength(30).IsRequired();
        builder.Property(x => x.SourceDocumentNo).HasMaxLength(50).IsRequired();
        builder.Property(x => x.ItemCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.CostMethod).HasMaxLength(30).IsRequired();
        builder.Property(x => x.PoNo).HasMaxLength(50);
        foreach (var property in new[]
                 {
                     nameof(PurchaseCostAdjustment.BaseQty),
                     nameof(PurchaseCostAdjustment.ActualBaseAmount),
                     nameof(PurchaseCostAdjustment.ReferenceBaseAmount),
                     nameof(PurchaseCostAdjustment.TotalAdjustmentAmount),
                     nameof(PurchaseCostAdjustment.InventoryAdjustmentAmount),
                     nameof(PurchaseCostAdjustment.ConsumedVarianceAmount)
                 })
            builder.Property<decimal>(property).HasPrecision(19, 6);
        builder.Property(x => x.CommercialReferenceAmount).HasPrecision(19, 6);
        builder.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(7)");
        builder.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
        builder.HasOne(x => x.StockPosting).WithMany()
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.StockPostingId })
            .HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.InventoryAdjustmentFact).WithMany()
            .HasForeignKey(x => x.InventoryAdjustmentFactId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ReversesAdjustment).WithMany()
            .HasForeignKey(x => x.ReversesAdjustmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.SourceDocumentType, x.SourceDocumentNo, x.SourceCostingRevision })
            .HasDatabaseName("IX_PurchaseCostAdjustment_Source");
        builder.HasIndex(x => x.ReversesAdjustmentId)
            .IsUnique().HasFilter("[ReversesAdjustmentId] IS NOT NULL")
            .HasDatabaseName("UX_PurchaseCostAdjustment_Reversal");
    }
}
