using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Production;

public sealed class ProductionWorkOrderTransferConfiguration : IEntityTypeConfiguration<ProductionWorkOrderTransfer>
{
    public void Configure(EntityTypeBuilder<ProductionWorkOrderTransfer> b)
    {
        b.ToTable("PrWorkOrderTransfer", table =>
        {
            table.UseSqlOutputClause(false);
            table.HasCheckConstraint("CK_PrWorkOrderTransfer_Status", "[Status] IN (N'NEW',N'POSTED')");
            table.HasCheckConstraint("CK_PrWorkOrderTransfer_Quantities",
                "[Qty] > 0 AND [BaseQty] > 0 AND [ConversionFactorToBase] > 0 AND [TransferTotalCost] >= 0");
            table.HasCheckConstraint("CK_PrWorkOrderTransfer_Contexts",
                "[SourceWorkOrderId] <> [TargetWorkOrderId] AND [SourceProducingRouteStepId] > 0 AND [TargetProducingRouteStepId] > 0");
        });

        b.HasKey(x => x.Uid);
        b.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        b.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        b.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        b.Property(x => x.TransferNo).HasMaxLength(30).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.TransferDateTime).HasColumnType("datetime2(7)");
        b.Property(x => x.Remark).HasMaxLength(250);
        b.Property(x => x.SourceProductionBalLotId).HasColumnName("SourceProductionBalLotID");
        b.Property(x => x.SourceWorkOrderId).HasColumnName("SourceWorkOrderID");
        b.Property(x => x.SourceWorkOrderNo).HasMaxLength(30).IsRequired();
        b.Property(x => x.SourceProducingRouteStepId).HasColumnName("SourceProducingRouteStepID");
        b.Property(x => x.SourceProducerOperationId).HasColumnName("SourceProducerOperationID");
        b.Property(x => x.SourceWorkCentreCode).HasMaxLength(20).IsRequired();
        b.Property(x => x.SourceProcessCode).HasMaxLength(30).IsRequired();
        b.Property(x => x.SourceProductionLocationId).HasColumnName("SourceProductionLocationID");
        b.Property(x => x.ItemCode).HasMaxLength(30).IsRequired();
        b.Property(x => x.LotNo).HasMaxLength(50).IsRequired();
        b.Property(x => x.PhysicalLotNo).HasMaxLength(50);
        b.Property(x => x.PoolCode).HasMaxLength(64);
        b.Property(x => x.LotIdentityKind).HasMaxLength(20);
        b.Property(x => x.Qty).HasPrecision(18, 4);
        b.Property(x => x.Uom).HasMaxLength(10).IsRequired();
        b.Property(x => x.BaseQty).HasPrecision(18, 4);
        b.Property(x => x.BaseUom).HasMaxLength(10).IsRequired();
        b.Property(x => x.ConversionFactorToBase).HasPrecision(18, 8);
        b.Property(x => x.TransferTotalCost).HasPrecision(19, 6);
        b.Property(x => x.SourceLastMovementDate).HasColumnType("datetime2(7)");
        b.Property(x => x.SourceLastStockEventEffectiveAt).HasColumnType("datetime2(7)");
        b.Property(x => x.SourceBalanceRowVersion).HasColumnType("varbinary(8)").IsRequired();
        b.Property(x => x.TargetWorkOrderId).HasColumnName("TargetWorkOrderID");
        b.Property(x => x.TargetWorkOrderNo).HasMaxLength(30).IsRequired();
        b.Property(x => x.TargetProducingRouteStepId).HasColumnName("TargetProducingRouteStepID");
        b.Property(x => x.TargetProducerOperationId).HasColumnName("TargetProducerOperationID");
        b.Property(x => x.TargetProducerWorkCentreCode).HasMaxLength(20).IsRequired();
        b.Property(x => x.TargetProducerProcessCode).HasMaxLength(30).IsRequired();
        b.Property(x => x.TargetProducerProductionLocationId).HasColumnName("TargetProducerProductionLocationID");
        b.Property(x => x.TargetWorkOrderMaterialId).HasColumnName("TargetWorkOrderMaterialID");
        b.Property(x => x.TargetConsumingRouteStepId).HasColumnName("TargetConsumingRouteStepID");
        b.Property(x => x.TargetConsumingOperationId).HasColumnName("TargetConsumingOperationID");
        b.Property(x => x.TargetConsumingWorkCentreCode).HasMaxLength(20).IsRequired();
        b.Property(x => x.TargetConsumingProcessCode).HasMaxLength(30).IsRequired();
        b.Property(x => x.TargetConsumerProductionLocationId).HasColumnName("TargetConsumerProductionLocationID");
        b.Property(x => x.TargetSnapshotHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.PostingRequestId).HasMaxLength(32).IsRequired();
        b.Property(x => x.ProductionPostingLinkId).HasColumnName("ProductionPostingLinkID");
        b.Property(x => x.StockPostingId).HasColumnName("StockPostingID");
        b.Property(x => x.DestinationProductionBalLotId).HasColumnName("DestinationProductionBalLotID");
        b.Property(x => x.TransferOutMovementId).HasColumnName("TransferOutMovementID");
        b.Property(x => x.TransferInMovementId).HasColumnName("TransferInMovementID");
        b.Property(x => x.MovementGroupId).HasColumnName("MovementGroupID");
        b.Property(x => x.CreatedBy).HasMaxLength(10).IsRequired();
        b.Property(x => x.ModifiedBy).HasMaxLength(10);
        b.Property(x => x.PostedBy).HasMaxLength(10);
        b.Property(x => x.DeletedBy).HasMaxLength(10);
        b.Property(x => x.DeleteReason).HasMaxLength(250);
        b.Property(x => x.DeletedAtUtc).HasColumnType("datetime2(7)");
        b.Property(x => x.RowVersion).IsRowVersion();

        b.HasOne<ProductionBalLot>().WithMany().HasForeignKey(x => x.SourceProductionBalLotId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionBalLot>().WithMany().HasForeignKey(x => x.DestinationProductionBalLotId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionWorkOrder>().WithMany().HasForeignKey(x => x.SourceWorkOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionWorkOrder>().WithMany().HasForeignKey(x => x.TargetWorkOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionPostingLink>().WithMany().HasForeignKey(x => x.ProductionPostingLinkId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StockPosting>().WithMany()
            .HasForeignKey(x => x.StockPostingId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionBalLotMovement>().WithMany().HasForeignKey(x => x.TransferOutMovementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionBalLotMovement>().WithMany().HasForeignKey(x => x.TransferInMovementId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.TransferNo }).IsUnique().HasDatabaseName("UQ_PrWorkOrderTransfer_Tenant_No");
        b.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.SourceProductionBalLotId })
            .IsUnique().HasFilter("[Status] = N'NEW' AND [DeletedAtUtc] IS NULL")
            .HasDatabaseName("UQ_PrWorkOrderTransfer_NewSource");
        b.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.Status, x.TransferDateTime, x.Uid })
            .HasDatabaseName("IX_PrWorkOrderTransfer_Status_Date");
        b.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.TargetWorkOrderId, x.TargetWorkOrderMaterialId })
            .HasDatabaseName("IX_PrWorkOrderTransfer_Target");
    }
}
