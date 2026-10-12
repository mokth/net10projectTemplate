using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Production;

public sealed class ProductionStockAdjustmentConfiguration :
    IEntityTypeConfiguration<ProductionStockAdjustment>, IEntityTypeConfiguration<ProductionStockAdjustmentLine>,
    IEntityTypeConfiguration<ProductionStockAdjustmentFact>
{
    public void Configure(EntityTypeBuilder<ProductionStockAdjustment> b)
    {
        b.ToTable("PrProductionStockAdjustment", t => t.HasCheckConstraint("CK_PrProductionStockAdjustment_Header", ProductionStockTransactionMapping.HeaderCheck));
        ProductionStockTransactionMapping.Header(b);
        b.HasOne(x => x.Batch).WithOne().HasForeignKey<ProductionStockAdjustment>(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionWorkOrder>().WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StockPosting>().WithMany().HasForeignKey(x => x.PostingId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StockPosting>().WithMany().HasForeignKey(x => x.ReversalPostingId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionStockAdjustment>().WithMany().HasForeignKey(x => x.CorrectedBatchId).OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<ProductionStockAdjustmentLine> b)
    {
        b.ToTable("PrProductionStockAdjustmentLine", t =>
        {
            t.HasCheckConstraint("CK_PrProductionStockAdjustmentLine_Qty", "[AdjustmentQty] <> 0 AND [BaseQty] > 0 AND [SourceFactor] > 0");
            t.HasCheckConstraint("CK_PrProductionStockAdjustmentLine_Cost", "[ProposedUnitCost] IS NULL OR [ProposedUnitCost] >= 0");
        });
        ProductionStockTransactionMapping.Line(b);
        ProductionStockTransactionMapping.Approval(b);
        b.Property(x => x.AdjustmentQty).HasPrecision(18, 4);
        b.Property(x => x.ProposedUnitCost).HasPrecision(19, 6);
        b.HasOne(x => x.Adjustment).WithMany(x => x.Lines).HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Detail).WithOne().HasForeignKey<ProductionStockAdjustmentLine>(x => x.DetailId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionBalLot>().WithMany().HasForeignKey(x => x.ProductionBalLotId).OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<ProductionStockAdjustmentFact> b)
    {
        b.ToTable("PrProductionStockAdjustmentFact", t =>
        {
            t.UseSqlOutputClause(false);
            t.HasCheckConstraint("CK_PrProductionStockAdjustmentFact_Value", "[BaseQty] > 0 AND [TotalValue] >= 0 AND [UnitCost] >= 0");
            t.HasCheckConstraint("CK_PrProductionStockAdjustmentFact_Reversal", "[ReversesFactId] IS NULL OR [ReversesFactId] <> [Id]");
            t.HasCheckConstraint("CK_PrProductionStockAdjustmentFact_Authority", AuthorityCheck);
        });
        ProductionStockTransactionMapping.Fact(b, nameof(ProductionStockAdjustmentFact.AdjustmentLineId));
        ProductionStockTransactionMapping.Approval(b);
        b.Property(x => x.UnitCost).HasPrecision(19, 6);
        b.Property(x => x.CostBasisCode).HasMaxLength(40).IsRequired();
        b.HasOne<ProductionStockAdjustment>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionStockAdjustmentLine>().WithMany().HasForeignKey(x => x.AdjustmentLineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StockPosting>().WithMany().HasForeignKey(x => x.StockPostingId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionBalLotMovement>().WithMany().HasForeignKey(x => x.ProductionMovementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionMaterialMovement>().WithMany().HasForeignKey(x => x.OriginalIssueMovementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ItemStandardCostRevision>().WithMany().HasForeignKey(x => x.StandardCostRevisionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionStockAdjustmentFact>().WithMany().HasForeignKey(x => x.ReversesFactId).OnDelete(DeleteBehavior.Restrict);
    }

    private const string NoApproval = "[CostEvidenceType] IS NULL AND [CostOverrideReason] IS NULL AND [CostApprovedBy] IS NULL AND [CostApprovedAtUtc] IS NULL";
    private const string AuthorityCheck =
        "([CostBasisCode] = 'REVERSAL' AND [ReversesFactId] IS NOT NULL AND [OriginalIssueMovementId] IS NULL AND [StandardCostRevisionId] IS NULL AND " + NoApproval + ") OR "
        + "([ReversesFactId] IS NULL AND ("
        + "([CostBasisCode] = 'CURRENT_PRODUCTION_POOL_COST' AND [OriginalIssueMovementId] IS NULL AND [StandardCostRevisionId] IS NULL AND " + NoApproval + ") OR "
        + "([CostBasisCode] = 'ORIGINAL_ISSUE_COST' AND [OriginalIssueMovementId] IS NOT NULL AND [StandardCostRevisionId] IS NULL AND " + NoApproval + ") OR "
        + "([CostBasisCode] = 'STANDARD_COST' AND [StandardCostRevisionId] IS NOT NULL AND [OriginalIssueMovementId] IS NULL AND " + NoApproval + ") OR "
        + "([CostBasisCode] IN ('MANUAL_APPROVED','ZERO_COST_APPROVED') AND [OriginalIssueMovementId] IS NULL AND [StandardCostRevisionId] IS NULL "
        + "AND [CostEvidenceType] IS NOT NULL AND [CostEvidenceType] = [CostBasisCode] "
        + "AND [CostOverrideReason] IS NOT NULL AND LEN(LTRIM(RTRIM([CostOverrideReason]))) > 0 "
        + "AND [CostApprovedBy] IS NOT NULL AND LEN(LTRIM(RTRIM([CostApprovedBy]))) > 0 AND [CostApprovedAtUtc] IS NOT NULL "
        + "AND (([CostBasisCode] = 'MANUAL_APPROVED' AND [UnitCost] > 0) OR ([CostBasisCode] = 'ZERO_COST_APPROVED' AND [UnitCost] = 0 AND [TotalValue] = 0)))))";
}
