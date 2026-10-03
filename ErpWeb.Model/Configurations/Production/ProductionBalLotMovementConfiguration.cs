using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Production;

public sealed class ProductionBalLotMovementConfiguration : IEntityTypeConfiguration<ProductionBalLotMovement>
{
    public void Configure(EntityTypeBuilder<ProductionBalLotMovement> builder)
    {
        builder.ToTable("PrProductionBalLotMovement", table =>
        {
            table.UseSqlOutputClause(false);
            table.HasCheckConstraint("CK_PrProductionBalLotMovement_Qty", "[Qty] > 0 AND [BaseQty] > 0");
            table.HasCheckConstraint("CK_PrProductionBalLotMovement_Cost", "[UnitCost] >= 0 AND [TotalCost] >= 0");
            table.HasCheckConstraint(
                "CK_PrProductionBalLotMovement_Type",
                "[MovementType] IN ('OPENING_IN','ISSUE','ISSUE_REVERSAL','PRODUCE','PRODUCE_REVERSAL','CONSUME','CONSUME_REVERSAL','RETURN','RETURN_REVERSAL','TRANSFER_OUT','TRANSFER_IN','STATUS_OUT','STATUS_IN','ADJUST_IN','ADJUST_OUT','SCRAP_OUT','FG_RECEIPT_OUT')");
            table.HasCheckConstraint("CK_PrProductionBalLotMovement_V2",
                "[LedgerVersion] IS NULL OR ([LedgerVersion] = 2 AND [LedgerEpochId] IS NOT NULL AND [StockPostingId] IS NOT NULL AND [PostingLineNo] > 0 AND [CompanyCode] IS NOT NULL AND [BranchCode] IS NOT NULL AND [ConversionFactorToBase] > 0)");
        });

        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.ProductionBalLotId).HasColumnName("ProductionBalLotID");
        builder.Property(x => x.MovementType).HasMaxLength(32).IsRequired();
        builder.Property(x => x.Qty).HasPrecision(18, 4);
        builder.Property(x => x.Uom).HasColumnName("UOM").HasMaxLength(10).IsRequired();
        builder.Property(x => x.BaseQty).HasPrecision(18, 4);
        builder.Property(x => x.BaseUom).HasColumnName("BaseUOM").HasMaxLength(10).IsRequired();
        builder.Property(x => x.UnitCost).HasPrecision(18, 4);
        builder.Property(x => x.TotalCost).HasPrecision(18, 4);
        builder.Property(x => x.WorkOrderId).HasColumnName("WorkOrderID");
        builder.Property(x => x.WorkOrderMaterialId).HasColumnName("WorkOrderMaterialID");
        builder.Property(x => x.WorkOrderOperationId).HasColumnName("WorkOrderOperationID");
        builder.Property(x => x.RouteStepId).HasColumnName("RouteStepID");
        builder.Property(x => x.ProductionOutputId).HasColumnName("ProductionOutputID");
        builder.Property(x => x.PostingLinkId).HasColumnName("PostingLinkID");
        builder.Property(x => x.OriginalMovementId).HasColumnName("OriginalMovementID");
        builder.Property(x => x.LedgerEpochId).HasColumnName("LedgerEpochID");
        builder.Property(x => x.StockPostingId).HasColumnName("StockPostingID");
        builder.Property(x => x.CompanyCode).HasMaxLength(5);
        builder.Property(x => x.BranchCode).HasMaxLength(5);
        builder.Property(x => x.ItemCode).HasMaxLength(30);
        builder.Property(x => x.ItemDescription).HasMaxLength(200);
        builder.Property(x => x.BalanceStage).HasMaxLength(20);
        builder.Property(x => x.ProductionLocationId).HasColumnName("ProductionLocationID");
        builder.Property(x => x.ProductionLocationCode).HasMaxLength(20);
        builder.Property(x => x.WorkCentreCode).HasMaxLength(20);
        builder.Property(x => x.ProcessCode).HasMaxLength(30);
        builder.Property(x => x.LotIdentity).HasMaxLength(64);
        builder.Property(x => x.PhysicalLotNo).HasMaxLength(50);
        builder.Property(x => x.StockStatusCode).HasMaxLength(20);
        builder.Property(x => x.WorkOrderNo).HasMaxLength(30);
        builder.Property(x => x.ConversionFactorToBase).HasPrecision(18, 8);
        builder.Property(x => x.SourceLineId).HasMaxLength(64);
        builder.Property(x => x.ValuationStatus).HasMaxLength(20);
        builder.Property(x => x.DocumentType).HasMaxLength(30).IsRequired();
        builder.Property(x => x.DocumentNo).HasMaxLength(30).IsRequired();
        builder.Property(x => x.MovementDate).HasColumnType("datetime2");
        builder.Property(x => x.CreatedDate).HasColumnType("datetime2");
        builder.Property(x => x.CreatedBy).HasMaxLength(10).IsRequired();

        builder.HasOne(x => x.ProductionBalLot).WithMany(x => x.Movements).HasForeignKey(x => x.ProductionBalLotId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ProductionOutput).WithMany().HasForeignKey(x => x.ProductionOutputId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.PostingLink).WithMany().HasForeignKey(x => x.PostingLinkId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.OriginalMovement).WithMany().HasForeignKey(x => x.OriginalMovementId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ErpWeb.Model.Entities.StockLedger.StockPosting>().WithMany()
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.StockPostingId })
            .HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.ProductionBalLotId, x.MovementDate, x.Uid })
            .HasDatabaseName("IX_PrProductionBalLotMovement_Lot_Date");
        builder.HasIndex(x => x.OriginalMovementId).HasDatabaseName("IX_PrProductionBalLotMovement_Original");
        builder.HasIndex(x => new { x.PostingLinkId, x.MovementType }).HasDatabaseName("IX_PrProductionBalLotMovement_Posting");
        builder.HasIndex(x => new { x.StockPostingId, x.PostingLineNo })
            .IsUnique().HasFilter("[StockPostingID] IS NOT NULL")
            .HasDatabaseName("UQ_PrProductionBalLotMovement_V2_PostingLine");
        builder.HasIndex(x => x.OriginalMovementId)
            .IsUnique().HasFilter("[LedgerVersion] = 2 AND [OriginalMovementID] IS NOT NULL")
            .HasDatabaseName("UQ_PrProductionBalLotMovement_V2_Reversal");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.LedgerEpochId, x.ItemCode, x.MovementDate, x.StockPostingId, x.PostingLineNo })
            .HasDatabaseName("IX_PrProductionBalLotMovement_V2_Card");
    }
}
