using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Production;

public sealed class ProductionMaterialMovementConfiguration : IEntityTypeConfiguration<ProductionMaterialMovement>
{
    public void Configure(EntityTypeBuilder<ProductionMaterialMovement> builder)
    {
        builder.ToTable("PrMaterialMovement", table =>
        {
            table.HasCheckConstraint("CK_PrMaterialMovement_Qty", "[Qty] > 0 AND [BaseQty] > 0");
            table.HasCheckConstraint("CK_PrMaterialMovement_Conversion", "[ConversionFactorToBase] > 0");
            table.HasCheckConstraint("CK_PrMaterialMovement_Cost", "[UnitCost] >= 0 AND [TotalCost] >= 0");
            table.HasCheckConstraint(
                "CK_PrMaterialMovement_Type",
                "[MovementType] IN ('ISSUE', 'ISSUE_REVERSAL', 'RETURN', 'CONSUME', 'CONSUME_REVERSAL', 'ADJUST')");
        });

        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.WorkOrderId).HasColumnName("WorkOrderID");
        builder.Property(x => x.WorkOrderMaterialId).HasColumnName("WorkOrderMaterialID");
        builder.Property(x => x.WorkOrderOperationId).HasColumnName("WorkOrderOperationID");
        builder.Property(x => x.MovementType).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ItemCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Qty).HasPrecision(18, 4);
        builder.Property(x => x.Uom).HasColumnName("UOM").HasMaxLength(10).IsRequired();
        builder.Property(x => x.BaseQty).HasPrecision(18, 4);
        builder.Property(x => x.BaseUom).HasColumnName("BaseUOM").HasMaxLength(10).IsRequired();
        builder.Property(x => x.ConversionFactorToBase).HasPrecision(18, 8);
        builder.Property(x => x.WarehouseCode).HasMaxLength(20).IsRequired();
        builder.Property(x => x.LocationCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.LotNo).HasMaxLength(50).IsRequired();
        builder.Property(x => x.LotId).HasColumnName("LotID");
        builder.Property(x => x.FromBalLocId).HasColumnName("FromBalLocID");
        builder.Property(x => x.ItemStatus).HasMaxLength(10).IsRequired();
        builder.Property(x => x.InventoryBatchId).HasColumnName("InventoryBatchID");
        builder.Property(x => x.InventoryBatchDetailId).HasColumnName("InventoryBatchDetailID");
        builder.Property(x => x.InventoryHistoryId).HasColumnName("InventoryHistoryID");
        builder.Property(x => x.InventoryPostingOperationId).HasColumnName("InventoryPostingOperationID").HasMaxLength(64);
        builder.Property(x => x.ProductionBalLotId).HasColumnName("ProductionBalLotID");
        builder.Property(x => x.ProductionBalLotMovementId).HasColumnName("ProductionBalLotMovementID");
        builder.Property(x => x.ProductionOutputId).HasColumnName("ProductionOutputID");
        builder.Property(x => x.UnitCost).HasPrecision(18, 4);
        builder.Property(x => x.TotalCost).HasPrecision(18, 4);
        builder.Property(x => x.PostingLinkId).HasColumnName("PostingLinkID");
        builder.Property(x => x.OriginalMovementId).HasColumnName("OriginalMovementID");
        builder.Property(x => x.StockPostingId).HasColumnName("StockPostingID");
        builder.Property(x => x.SourceLineId).HasMaxLength(64);
        builder.Property(x => x.SourceIssueMovementId).HasColumnName("SourceIssueMovementID");
        builder.Property(x => x.ReversesMaterialMovementId).HasColumnName("ReversesMaterialMovementID");
        builder.Property(x => x.Reason).HasMaxLength(50);
        builder.Property(x => x.Remarks).HasMaxLength(250);
        builder.Property(x => x.CreatedBy).HasMaxLength(10).IsRequired();

        builder.HasOne(x => x.WorkOrder).WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.WorkOrderMaterial).WithMany().HasForeignKey(x => x.WorkOrderMaterialId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.WorkOrderOperation).WithMany().HasForeignKey(x => x.WorkOrderOperationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.PostingLink).WithMany().HasForeignKey(x => x.PostingLinkId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.OriginalMovement).WithMany().HasForeignKey(x => x.OriginalMovementId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.SourceIssueMovement).WithMany().HasForeignKey(x => x.SourceIssueMovementId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ReversesMaterialMovement).WithMany().HasForeignKey(x => x.ReversesMaterialMovementId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ErpWeb.Model.Entities.StockLedger.StockPosting>().WithMany()
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.StockPostingId })
            .HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.InventoryBatch).WithMany().HasForeignKey(x => x.InventoryBatchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.InventoryBatchDetail).WithMany().HasForeignKey(x => x.InventoryBatchDetailId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.FromBalLoc).WithMany().HasForeignKey(x => x.FromBalLocId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Lot).WithMany().HasForeignKey(x => x.LotId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ProductionBalLot).WithMany().HasForeignKey(x => x.ProductionBalLotId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ProductionBalLotMovement).WithMany().HasForeignKey(x => x.ProductionBalLotMovementId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ProductionOutput).WithMany().HasForeignKey(x => x.ProductionOutputId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.WorkOrderId, x.MovementDate }).HasDatabaseName("IX_PrMaterialMovement_WorkOrder_Date");
        builder.HasIndex(x => new { x.WorkOrderMaterialId, x.MovementType }).HasDatabaseName("IX_PrMaterialMovement_Material_Type");
        builder.HasIndex(x => new { x.WorkOrderOperationId, x.MovementDate }).HasDatabaseName("IX_PrMaterialMovement_Operation_Date");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.InventoryBatchNo }).HasDatabaseName("IX_PrMaterialMovement_InventoryBatch");
        builder.HasIndex(x => x.OriginalMovementId).HasDatabaseName("IX_PrMaterialMovement_OriginalMovement");
        builder.HasIndex(x => new { x.PostingLinkId, x.InventoryBatchDetailId, x.MovementType })
            .IsUnique()
            .HasFilter("[InventoryBatchDetailID] IS NOT NULL")
            .HasDatabaseName("UQ_PrMaterialMovement_PostingInventoryLine");
        builder.HasIndex(x => new { x.PostingLinkId, x.ProductionBalLotMovementId, x.MovementType })
            .IsUnique()
            .HasFilter("[ProductionBalLotMovementID] IS NOT NULL")
            .HasDatabaseName("UQ_PrMaterialMovement_PostingBalLotLine");
        builder.HasIndex(x => new { x.StockPostingId, x.SourceLineId, x.SplitOrdinal, x.MovementType })
            .IsUnique()
            .HasFilter("[StockPostingID] IS NOT NULL")
            .HasDatabaseName("UQ_PrMaterialMovement_V2_SourceLine");
        builder.HasIndex(x => x.ReversesMaterialMovementId)
            .IsUnique()
            .HasFilter("[ReversesMaterialMovementID] IS NOT NULL")
            .HasDatabaseName("UQ_PrMaterialMovement_V2_Reversal");
    }
}
