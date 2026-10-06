using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.StockLedger;

public sealed class StockFifoLayerConfiguration : IEntityTypeConfiguration<StockFifoLayer>
{
    public void Configure(EntityTypeBuilder<StockFifoLayer> builder)
    {
        builder.ToTable("StockFifoLayer", table =>
        {
            table.HasCheckConstraint("CK_StockFifoLayer_Quantities",
                "[OriginalQty] >= 0 AND [RemainingQty] >= 0 AND [RemainingQty] <= [OriginalQty]");
            table.HasCheckConstraint("CK_StockFifoLayer_Values",
                "[OriginalValue] >= 0 AND [RemainingValue] >= 0");
            table.HasCheckConstraint("CK_StockFifoLayer_Status",
                "[Status] IN ('OPEN','CLOSED')");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.ItemCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.BaseUom).HasMaxLength(10).IsRequired();
        builder.Property(x => x.ReceiptEffectiveAt).HasColumnType("datetime2(7)");
        builder.Property(x => x.OriginalQty).HasPrecision(19, 6);
        builder.Property(x => x.RemainingQty).HasPrecision(19, 6);
        builder.Property(x => x.OriginalValue).HasPrecision(19, 6);
        builder.Property(x => x.AccumulatedAdjustment).HasPrecision(19, 6);
        builder.Property(x => x.RemainingValue).HasPrecision(19, 6);
        builder.Property(x => x.CurrentUnitCost).HasPrecision(19, 6);
        builder.Property(x => x.SourceDocumentType).HasMaxLength(40).IsRequired();
        builder.Property(x => x.SourceDocumentNo).HasMaxLength(50).IsRequired();
        builder.Property(x => x.SourceDocumentLine).HasMaxLength(50);
        builder.Property(x => x.WarehouseCode).HasMaxLength(20);
        builder.Property(x => x.LotNo).HasMaxLength(50);
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasOne(x => x.OriginValuationFact).WithMany()
            .HasForeignKey(x => x.OriginValuationFactId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.OriginStockPosting).WithMany()
            .HasForeignKey(x => x.OriginStockPostingId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.ItemCode, x.Status, x.ReceiptEffectiveAt, x.Id })
            .HasDatabaseName("IX_StockFifoLayer_OpenOrder");
        builder.HasIndex(x => x.OriginValuationFactId)
            .HasDatabaseName("IX_StockFifoLayer_OriginFact");
    }
}

public sealed class StockFifoLayerConsumptionConfiguration : IEntityTypeConfiguration<StockFifoLayerConsumption>
{
    public void Configure(EntityTypeBuilder<StockFifoLayerConsumption> builder)
    {
        builder.ToTable("StockFifoLayerConsumption", table =>
        {
            table.HasCheckConstraint("CK_StockFifoLayerConsumption_Qty", "[ConsumedQty] > 0");
            table.HasCheckConstraint("CK_StockFifoLayerConsumption_Value", "[ConsumedValue] >= 0");
            table.HasCheckConstraint("CK_StockFifoLayerConsumption_Split", "[SplitOrdinal] >= 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.ConsumedQty).HasPrecision(19, 6);
        builder.Property(x => x.ConsumedValue).HasPrecision(19, 6);
        builder.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(7)");
        builder.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
        builder.HasOne(x => x.IssueValuationFact).WithMany()
            .HasForeignKey(x => x.IssueValuationFactId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.FifoLayer).WithMany(x => x.Consumptions)
            .HasForeignKey(x => x.FifoLayerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ReversesConsumption).WithMany()
            .HasForeignKey(x => x.ReversesConsumptionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.IssueValuationFactId, x.SplitOrdinal })
            .IsUnique().HasDatabaseName("UQ_StockFifoLayerConsumption_IssueSplit");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.FifoLayerId })
            .HasDatabaseName("IX_StockFifoLayerConsumption_Layer");
        builder.HasIndex(x => x.ReversesConsumptionId)
            .IsUnique().HasFilter("[ReversesConsumptionId] IS NOT NULL")
            .HasDatabaseName("UQ_StockFifoLayerConsumption_Reversal");
    }
}
