using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.StockLedger;

public sealed class StockCountCostBasisConfiguration : IEntityTypeConfiguration<StockCountCostBasis>
{
    public void Configure(EntityTypeBuilder<StockCountCostBasis> b)
    {
        b.ToTable("StockCountCostBasis", t =>
        {
            t.UseSqlOutputClause(false);
            t.HasCheckConstraint("CK_StockCountCostBasis_Cost", "[FrozenFallbackUnitCost] >= 0");
            t.HasCheckConstraint("CK_StockCountCostBasis_Kind", "[CostBasisKind] IN (N'LEDGER_WAC', N'LEDGER_FIFO', N'STANDARD_REVISION', N'MANUAL_APPROVED', N'ZERO_COST_APPROVED')");
            t.HasCheckConstraint("CK_StockCountCostBasis_Provenance", "([CostBasisKind] IN (N'LEDGER_WAC', N'LEDGER_FIFO') AND [CostBasisWatermark] IS NOT NULL AND [CostBasisHash] IS NOT NULL) OR ([CostBasisKind] = N'STANDARD_REVISION' AND [StandardCostRevisionId] IS NOT NULL) OR ([CostBasisKind] IN (N'MANUAL_APPROVED', N'ZERO_COST_APPROVED') AND [CostEvidenceType] IS NOT NULL AND [CostApprovedBy] IS NOT NULL AND [CostApprovedAtUtc] IS NOT NULL AND [CostOverrideReason] IS NOT NULL)");
            t.HasCheckConstraint("CK_StockCountCostBasis_Approval", "([CostBasisKind] = N'MANUAL_APPROVED' AND [CostEvidenceType] = N'MANUAL_APPROVED' AND [FrozenFallbackUnitCost] > 0 AND LEN(LTRIM(RTRIM([CostOverrideReason]))) > 0) OR ([CostBasisKind] = N'ZERO_COST_APPROVED' AND [CostEvidenceType] = N'ZERO_COST_APPROVED' AND [FrozenFallbackUnitCost] = 0 AND LEN(LTRIM(RTRIM([CostOverrideReason]))) > 0) OR [CostBasisKind] NOT IN (N'MANUAL_APPROVED', N'ZERO_COST_APPROVED')");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        b.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        b.Property(x => x.FrozenFallbackUnitCost).HasPrecision(19, 6);
        b.Property(x => x.CostBasisKind).HasMaxLength(30).IsRequired();
        b.Property(x => x.CostBasisHash).HasColumnType("char(64)");
        b.Property(x => x.CostEvidenceType).HasMaxLength(30);
        b.Property(x => x.CostOverrideReason).HasMaxLength(250);
        b.Property(x => x.CostApprovedBy).HasMaxLength(100);
        b.Property(x => x.CostApprovedAtUtc).HasColumnType("datetime2(7)");
        b.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(7)");
        b.HasOne<IvStockCountLine>().WithMany().HasForeignKey(x => x.StockCountLineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ItemStandardCostRevision>().WithMany().HasForeignKey(x => x.StandardCostRevisionId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.StockCountLineId).IsUnique();
    }
}

public sealed class StockCountReplayGenerationConfiguration : IEntityTypeConfiguration<StockCountReplayGeneration>
{
    public void Configure(EntityTypeBuilder<StockCountReplayGeneration> b)
    {
        b.ToTable("StockCountReplayGeneration", t =>
        {
            t.UseSqlOutputClause(false);
            t.HasCheckConstraint("CK_StockCountReplayGeneration_Removal", "[IsRemoval] = 0 OR ([SignedAdjustmentQty] = 0 AND [AdjustmentHistoryId] IS NULL)");
            t.HasCheckConstraint("CK_StockCountReplayGeneration_Adjustment", "([SignedAdjustmentQty] = 0 AND [AdjustmentHistoryId] IS NULL) OR ([SignedAdjustmentQty] <> 0 AND [AdjustmentHistoryId] IS NOT NULL)");
            t.HasCheckConstraint("CK_StockCountReplayGeneration_Batches", "([AdjustmentBatchNo] IS NULL AND [AdjustmentHistoryId] IS NULL OR [AdjustmentBatchNo] IS NOT NULL AND [AdjustmentHistoryId] IS NOT NULL) AND ([ReversalBatchNo] IS NULL AND [ReversalHistoryId] IS NULL OR [ReversalBatchNo] IS NOT NULL AND [ReversalHistoryId] IS NOT NULL)");
        });
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.CompanyCode, x.BranchCode, x.StockCountLineId, x.Id });
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        b.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        b.Property(x => x.EffectiveAt).HasColumnType("datetime2(7)");
        b.Property(x => x.SignedAdjustmentQty).HasPrecision(18, 4);
        b.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(7)");
        b.HasOne<IvStockCountLine>().WithMany().HasForeignKey(x => x.StockCountLineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StockPosting>().WithMany().HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.AnchorPostingId }).HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StockPosting>().WithMany().HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.WritePostingId }).HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StockCountReplayGeneration>().WithMany().HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.StockCountLineId, x.PreviousGenerationId }).HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.StockCountLineId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<IvTrxHistory>().WithMany().HasForeignKey(x => x.AdjustmentHistoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<IvTrxHistory>().WithMany().HasForeignKey(x => x.ReversalHistoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.StockCountLineId).IsUnique()
            .HasFilter("[PreviousGenerationId] IS NULL")
            .HasDatabaseName("UQ_StockCountReplayGeneration_Initial");
        b.HasIndex(x => new { x.StockCountLineId, x.Id })
            .HasDatabaseName("IX_StockCountReplayGeneration_Line");
        b.HasIndex(x => x.PreviousGenerationId).IsUnique().HasFilter("[PreviousGenerationId] IS NOT NULL");
        b.HasIndex(x => x.AdjustmentHistoryId).IsUnique().HasFilter("[AdjustmentHistoryId] IS NOT NULL");
        b.HasIndex(x => x.ReversalHistoryId).IsUnique().HasFilter("[ReversalHistoryId] IS NOT NULL");
        b.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.EffectiveAt, x.AnchorPostingSequence, x.StockCountLineId });
    }
}

public sealed class StockReplayRunConfiguration : IEntityTypeConfiguration<StockReplayRun>
{
    public void Configure(EntityTypeBuilder<StockReplayRun> b)
    {
        b.ToTable("StockReplayRun", t =>
        {
            t.UseSqlOutputClause(false);
            t.HasCheckConstraint("CK_StockReplayRun_Range", "[EarliestEffectiveAt] <= [LatestEffectiveAt]");
            t.HasCheckConstraint("CK_StockReplayRun_Counts", "[AffectedHistoryCount] >= 0 AND [AffectedItemCount] >= 0 AND [AffectedProductionMovementCount] >= 0");
        });
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.CompanyCode, x.BranchCode, x.Id });
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        b.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        b.Property(x => x.RootDocumentType).HasMaxLength(40).IsRequired();
        b.Property(x => x.RootDocumentId).HasMaxLength(64).IsRequired();
        b.Property(x => x.PlanHash).HasColumnType("char(64)").IsRequired();
        b.Property(x => x.EarliestEffectiveAt).HasColumnType("datetime2(7)");
        b.Property(x => x.LatestEffectiveAt).HasColumnType("datetime2(7)");
        b.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(7)");
        b.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
        b.HasOne<StockPosting>().WithMany().HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.RootStockPostingId }).HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.RootStockPostingId).IsUnique();
    }
}

public sealed class StockReplayEffectConfiguration : IEntityTypeConfiguration<StockReplayEffect>
{
    public void Configure(EntityTypeBuilder<StockReplayEffect> b)
    {
        b.ToTable("StockReplayEffect", t => t.UseSqlOutputClause(false));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        b.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        b.Property(x => x.EffectKind).HasMaxLength(40).IsRequired();
        b.Property(x => x.AffectedEntityType).HasMaxLength(40).IsRequired();
        b.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(7)");
        b.HasOne<StockReplayRun>().WithMany().HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.ReplayRunId }).HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StockPosting>().WithMany().HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.RootStockPostingId }).HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StockPosting>().WithMany().HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.BusinessOwnerPostingId }).HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ReplayRunId, x.EffectKind, x.AffectedEntityType, x.AffectedEntityId }).IsUnique();
        b.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.BusinessOwnerPostingId, x.EffectKind });
    }
}

public sealed class ProductionCostRestatementFactConfiguration : IEntityTypeConfiguration<ProductionCostRestatementFact>
{
    public void Configure(EntityTypeBuilder<ProductionCostRestatementFact> b)
    {
        b.ToTable("ProductionCostRestatementFact", t =>
        {
            t.UseSqlOutputClause(false);
            t.HasCheckConstraint("CK_ProductionCostRestatementFact_Delta", "[DeltaCost] = [CorrectedEffectiveTotalCost] - [PriorEffectiveTotalCost]");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        b.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        b.Property(x => x.EffectiveAt).HasColumnType("datetime2(7)");
        b.Property(x => x.PriorEffectiveTotalCost).HasPrecision(19, 6);
        b.Property(x => x.CorrectedEffectiveTotalCost).HasPrecision(19, 6);
        b.Property(x => x.CorrectedUnitCost).HasPrecision(19, 6);
        b.Property(x => x.DeltaCost).HasPrecision(19, 6);
        b.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(7)");
        b.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
        b.HasOne<StockPosting>().WithMany().HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.ReplayStockPostingId }).HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionBalLotMovement>().WithMany().HasForeignKey(x => x.ProductionBalLotMovementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionCostRestatementFact>().WithMany().HasForeignKey(x => x.ReversesRestatementFactId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ProductionBalLotMovementId);
        b.HasIndex(x => x.ReplayStockPostingId);
        b.HasIndex(x => x.ReversesRestatementFactId).IsUnique().HasFilter("[ReversesRestatementFactId] IS NOT NULL");
    }
}
