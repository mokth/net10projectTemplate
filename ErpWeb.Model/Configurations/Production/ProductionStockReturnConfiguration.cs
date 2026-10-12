using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Production;

public sealed class ProductionStockReturnConfiguration :
    IEntityTypeConfiguration<ProductionStockReturn>, IEntityTypeConfiguration<ProductionStockReturnLine>,
    IEntityTypeConfiguration<ProductionStockReturnFact>, IEntityTypeConfiguration<ProductionStockReturnPriceSnapshot>
{
    public void Configure(EntityTypeBuilder<ProductionStockReturn> b)
    {
        b.ToTable("PrProductionStockReturn", t => t.HasCheckConstraint("CK_PrProductionStockReturn_Header", ProductionStockTransactionMapping.HeaderCheck));
        ProductionStockTransactionMapping.Header(b);
        b.HasOne(x => x.Batch).WithOne().HasForeignKey<ProductionStockReturn>(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionWorkOrder>().WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StockPosting>().WithMany().HasForeignKey(x => x.PostingId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StockPosting>().WithMany().HasForeignKey(x => x.ReversalPostingId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionStockReturn>().WithMany().HasForeignKey(x => x.CorrectedBatchId).OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<ProductionStockReturnLine> b)
    {
        b.ToTable("PrProductionStockReturnLine", t => t.HasCheckConstraint("CK_PrProductionStockReturnLine_Qty",
            "[RequestedQty] > 0 AND [BaseQty] > 0 AND [SourceFactor] > 0"));
        ProductionStockTransactionMapping.Line(b);
        b.Property(x => x.RequestedQty).HasPrecision(18, 4);
        b.HasOne(x => x.Return).WithMany(x => x.Lines).HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Detail).WithOne().HasForeignKey<ProductionStockReturnLine>(x => x.DetailId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionBalLot>().WithMany().HasForeignKey(x => x.ProductionBalLotId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionMaterialMovement>().WithMany().HasForeignKey(x => x.SourceIssueMovementId).OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<ProductionStockReturnFact> b)
    {
        b.ToTable("PrProductionStockReturnFact", t =>
        {
            t.UseSqlOutputClause(false);
            t.HasCheckConstraint("CK_PrProductionStockReturnFact_Value", "[BaseQty] > 0 AND [TotalValue] >= 0");
            t.HasCheckConstraint("CK_PrProductionStockReturnFact_Reversal", "[ReversesFactId] IS NULL OR [ReversesFactId] <> [Id]");
        });
        ProductionStockTransactionMapping.Fact(b, nameof(ProductionStockReturnFact.ReturnLineId));
        b.HasOne<ProductionStockReturn>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionStockReturnLine>().WithMany().HasForeignKey(x => x.ReturnLineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StockPosting>().WithMany().HasForeignKey(x => x.StockPostingId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionBalLotMovement>().WithMany().HasForeignKey(x => x.ProductionMovementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionMaterialMovement>().WithMany().HasForeignKey(x => x.ProductionMaterialMovementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionMaterialMovement>().WithMany().HasForeignKey(x => x.SourceIssueMovementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<IvTrxHistory>().WithMany().HasForeignKey(x => x.InventoryHistoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<IvBalLoc>().WithMany().HasForeignKey(x => x.DestinationBalanceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionStockReturnFact>().WithMany().HasForeignKey(x => x.ReversesFactId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ProductionMaterialMovementId).IsUnique();
        b.HasIndex(x => x.InventoryHistoryId).IsUnique();
    }

    public void Configure(EntityTypeBuilder<ProductionStockReturnPriceSnapshot> b)
    {
        b.ToTable("PrProductionStockReturnPriceSnapshot", t => t.UseSqlOutputClause(false));
        b.HasKey(x => x.Id);
        b.HasOne<StockPosting>().WithMany().HasForeignKey(x => x.StockPostingId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<IvBalLoc>().WithMany().HasForeignKey(x => x.DestinationBalanceId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.StockPostingId, x.DestinationBalanceId }).IsUnique();
        b.Property(x => x.PreviousUnitPrice).HasPrecision(18, 4);
        b.Property(x => x.PreviousCost).HasPrecision(18, 4);
        b.Property(x => x.PostedUnitPrice).HasPrecision(18, 4);
        b.Property(x => x.PostedCost).HasPrecision(18, 4);
        b.Property(x => x.PreviousPriceEvidence).HasMaxLength(200);
        b.Property(x => x.PostedPriceEvidence).HasMaxLength(200);
    }
}
