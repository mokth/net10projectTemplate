using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.StockLedger;

public sealed class StockValuationFactConfiguration : IEntityTypeConfiguration<StockValuationFact>
{
    public void Configure(EntityTypeBuilder<StockValuationFact> builder)
    {
        builder.ToTable("StockValuationFact", table =>
        {
            table.UseSqlOutputClause(false);
            table.HasCheckConstraint("CK_StockValuationFact_Direction", "[Direction] IN (-1, 1)");
            table.HasCheckConstraint("CK_StockValuationFact_Quantity", "[BaseQty] >= 0");
            table.HasCheckConstraint("CK_StockValuationFact_Amount", "[CostAmount] >= 0 AND [BaseCostAmount] >= 0");
            table.HasCheckConstraint("CK_StockValuationFact_Identity", "[PostingLineNo] > 0 AND [SplitOrdinal] >= 0");
            table.HasCheckConstraint("CK_StockValuationFact_NoSelfReverse",
                "([OriginalValuationFactId] IS NULL OR [OriginalValuationFactId] <> [Id]) AND ([ReversesValuationFactId] IS NULL OR [ReversesValuationFactId] <> [Id])");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.SourceLineId).HasMaxLength(120).IsRequired();
        builder.Property(x => x.SourceDocumentType).HasMaxLength(40).IsRequired();
        builder.Property(x => x.SourceDocumentId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.SourceDocumentNo).HasMaxLength(50).IsRequired();
        builder.Property(x => x.SourceDocumentLine).HasMaxLength(50);
        builder.Property(x => x.EffectiveAt).HasColumnType("datetime2(7)");
        builder.Property(x => x.BusinessDate).HasColumnType("date");
        builder.Property(x => x.PeriodKey).HasColumnType("char(7)").IsRequired();
        builder.Property(x => x.ItemCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.WarehouseCode).HasMaxLength(20);
        builder.Property(x => x.LocationCode).HasMaxLength(10);
        builder.Property(x => x.LotNo).HasMaxLength(50);
        builder.Property(x => x.ItemStatus).HasMaxLength(10);
        builder.Property(x => x.BaseUom).HasMaxLength(10).IsRequired();
        builder.Property(x => x.MovementCode).HasMaxLength(40).IsRequired();
        builder.Property(x => x.BaseQty).HasPrecision(19, 6);
        builder.Property(x => x.CostMethod).HasMaxLength(30).IsRequired();
        builder.Property(x => x.UnitCost).HasPrecision(19, 6);
        builder.Property(x => x.CostAmount).HasPrecision(19, 6);
        builder.Property(x => x.TransactionCurrency).HasMaxLength(3);
        builder.Property(x => x.TransactionCostAmount).HasPrecision(19, 6);
        builder.Property(x => x.ExchangeRate).HasPrecision(19, 8);
        builder.Property(x => x.BaseCurrency).HasMaxLength(3);
        builder.Property(x => x.BaseCostAmount).HasPrecision(19, 6);
        builder.Property(x => x.ValuationSource).HasMaxLength(40).IsRequired();
        builder.Property(x => x.ValuationStatus).HasMaxLength(20).IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(7)");
        builder.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();

        builder.HasOne(x => x.LedgerEpoch).WithMany()
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.LedgerEpochId })
            .HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.StockPosting).WithMany()
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.StockPostingId })
            .HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.InventoryHistory).WithMany()
            .HasForeignKey(x => x.InventoryHistoryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.FromBalLoc).WithMany()
            .HasForeignKey(x => x.FromBalLocId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ToBalLoc).WithMany()
            .HasForeignKey(x => x.ToBalLocId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Lot).WithMany()
            .HasForeignKey(x => x.LotId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.OriginalValuationFact).WithMany()
            .HasForeignKey(x => x.OriginalValuationFactId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ReversesValuationFact).WithMany()
            .HasForeignKey(x => x.ReversesValuationFactId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.StockPostingId, x.PostingLineNo, x.SplitOrdinal })
            .IsUnique().HasDatabaseName("UQ_StockValuationFact_PostingLineSplit");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.ItemCode, x.EffectiveAt })
            .HasDatabaseName("IX_StockValuationFact_ItemEffectiveAt");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.PeriodKey })
            .HasDatabaseName("IX_StockValuationFact_Period");
        builder.HasIndex(x => x.InventoryHistoryId)
            .HasDatabaseName("IX_StockValuationFact_History");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.SourceDocumentType, x.SourceDocumentId })
            .HasDatabaseName("IX_StockValuationFact_Source");
        builder.HasIndex(x => x.OriginalValuationFactId)
            .HasDatabaseName("IX_StockValuationFact_Original");
        builder.HasIndex(x => x.ReversesValuationFactId)
            .IsUnique().HasFilter("[ReversesValuationFactId] IS NOT NULL")
            .HasDatabaseName("UQ_StockValuationFact_Reversal");
        builder.HasIndex(x => new { x.WorkOrderId, x.WorkOrderOperationId })
            .HasDatabaseName("IX_StockValuationFact_WorkOrder");
    }
}

public sealed class StockCostStateConfiguration : IEntityTypeConfiguration<StockCostState>
{
    public void Configure(EntityTypeBuilder<StockCostState> builder)
    {
        builder.ToTable("StockCostState", table =>
        {
            table.UseSqlOutputClause(false);
            table.HasCheckConstraint("CK_StockCostState_Quantity", "[OnHandBaseQty] >= 0");
            table.HasCheckConstraint("CK_StockCostState_Value", "[InventoryValue] >= 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.ItemCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.CostMethod).HasMaxLength(30).IsRequired();
        builder.Property(x => x.OnHandBaseQty).HasPrecision(19, 6);
        builder.Property(x => x.InventoryValue).HasPrecision(19, 6);
        builder.Property(x => x.CurrentUnitCost).HasPrecision(19, 6);
        builder.Property(x => x.AverageUnitCost).HasPrecision(19, 6);
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasOne(x => x.LastValuationFact).WithMany()
            .HasForeignKey(x => x.LastValuationFactId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.ItemCode, x.CostMethod })
            .IsUnique().HasDatabaseName("UQ_StockCostState_Pool");
    }
}

public sealed class StockCostPolicyRevisionConfiguration : IEntityTypeConfiguration<StockCostPolicyRevision>
{
    public void Configure(EntityTypeBuilder<StockCostPolicyRevision> builder)
    {
        builder.ToTable("StockCostPolicyRevision", table =>
        {
            table.HasCheckConstraint("CK_StockCostPolicyRevision_Method",
                "[CostMethod] IN ('MOVING_AVERAGE','FIFO','STANDARD')");
            table.HasCheckConstraint("CK_StockCostPolicyRevision_Status",
                "[Status] IN ('ACTIVE','SUPERSEDED')");
            table.HasCheckConstraint("CK_StockCostPolicyRevision_Range",
                "[EffectiveTo] IS NULL OR [EffectiveTo] > [EffectiveFrom]");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.CostMethod).HasMaxLength(30).IsRequired();
        builder.Property(x => x.EffectiveFrom).HasColumnType("date");
        builder.Property(x => x.EffectiveTo).HasColumnType("date");
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(250);
        builder.Property(x => x.ApprovedBy).HasMaxLength(100).IsRequired();
        builder.Property(x => x.ApprovedAtUtc).HasColumnType("datetime2(7)");
        builder.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(7)");
        builder.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.EffectiveFrom })
            .HasDatabaseName("IX_StockCostPolicyRevision_EffectiveFrom");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.Status })
            .HasDatabaseName("IX_StockCostPolicyRevision_Status");
    }
}
