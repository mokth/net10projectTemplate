using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Production;

public sealed class ProductionBalLotConfiguration : IEntityTypeConfiguration<ProductionBalLot>
{
    public void Configure(EntityTypeBuilder<ProductionBalLot> builder)
    {
        builder.ToTable("PrProductionBalLot", table =>
        {
            table.UseSqlOutputClause(false);
            table.HasCheckConstraint("CK_PrProductionBalLot_Kind",
                "[Kind] IN ('MATERIAL_IN', 'WIP')");
            table.HasCheckConstraint("CK_PrProductionBalLot_Qty",
                "[Qty] >= 0 AND [BaseQty] >= 0 AND [ConversionFactorToBase] > 0");
            table.HasCheckConstraint("CK_PrProductionBalLot_Cost",
                "[TotalCost] >= 0 AND [AverageUnitCost] >= 0");
            table.HasCheckConstraint("CK_PrProductionBalLot_V2",
                "[BalanceStage] IS NULL OR ([ProductionLocationID] IS NOT NULL AND [StockStatusCode] IS NOT NULL AND [OriginType] IS NOT NULL)");
        });

        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.Kind).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ItemCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(200);
        builder.Property(x => x.Qty).HasPrecision(18, 4);
        builder.Property(x => x.Uom).HasColumnName("UOM").HasMaxLength(10).IsRequired();
        builder.Property(x => x.BaseQty).HasPrecision(18, 4);
        builder.Property(x => x.BaseUom).HasColumnName("BaseUOM").HasMaxLength(10).IsRequired();
        builder.Property(x => x.ConversionFactorToBase).HasPrecision(18, 8);
        builder.Property(x => x.TotalCost).HasPrecision(19, 6);
        builder.Property(x => x.AverageUnitCost).HasPrecision(19, 6);
        builder.Property(x => x.WorkOrderId).HasColumnName("WorkOrderID");
        builder.Property(x => x.WorkOrderNo).HasMaxLength(30).IsRequired();
        builder.Property(x => x.WorkOrderMaterialId).HasColumnName("WorkOrderMaterialID");
        builder.Property(x => x.OriginalIssueMovementId).HasColumnName("OriginalIssueMovementID");
        builder.Property(x => x.SourceIvBalLocId).HasColumnName("SourceIvBalLocID");
        builder.Property(x => x.WarehouseCode).HasMaxLength(20).IsRequired();
        builder.Property(x => x.LocationCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.LotNo).HasMaxLength(50).IsRequired();
        builder.Property(x => x.BalanceStage).HasMaxLength(20);
        builder.Property(x => x.ProductionLocationId).HasColumnName("ProductionLocationID");
        builder.Property(x => x.StockStatusCode).HasMaxLength(20);
        builder.Property(x => x.PoolCode).HasMaxLength(64);
        builder.Property(x => x.PhysicalLotNo).HasMaxLength(50);
        builder.Property(x => x.LotIdentityKind).HasMaxLength(20);
        builder.Property(x => x.FirstReceiptEffectiveAt).HasColumnType("datetime2(7)");
        builder.Property(x => x.LastStockEventEffectiveAt).HasColumnType("datetime2(7)");
        builder.Property(x => x.OriginType).HasMaxLength(20);
        builder.Property(x => x.ProducingRouteStepId).HasColumnName("ProducingRouteStepID");
        builder.Property(x => x.WorkOrderOperationId).HasColumnName("WorkOrderOperationID");
        builder.Property(x => x.OutputType).HasMaxLength(20);
        builder.Property(x => x.WorkCentreCode).HasMaxLength(20);
        builder.Property(x => x.ProcessCode).HasMaxLength(30);
        builder.Property(x => x.LastMovementDate).HasColumnType("datetime2");
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasOne(x => x.WorkOrder).WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.WorkOrderMaterial).WithMany().HasForeignKey(x => x.WorkOrderMaterialId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.OriginalIssueMovement).WithMany().HasForeignKey(x => x.OriginalIssueMovementId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ProducingRouteStep).WithMany().HasForeignKey(x => x.ProducingRouteStepId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.WorkOrderOperation).WithMany().HasForeignKey(x => x.WorkOrderOperationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ProductionLocation).WithMany()
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.ProductionLocationId })
            .HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.Kind, x.WorkOrderId, x.WorkOrderMaterialId, x.OriginalIssueMovementId })
            .IsUnique()
            .HasFilter("[Kind] = N'MATERIAL_IN'")
            .HasDatabaseName("UQ_PrProductionBalLot_MaterialIn");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.Kind, x.WorkOrderId, x.ProducingRouteStepId, x.ItemCode, x.LotNo })
            .IsUnique()
            .HasFilter("[Kind] = N'WIP'")
            .HasDatabaseName("UQ_PrProductionBalLot_Wip");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.ItemCode, x.LotNo })
            .HasDatabaseName("IX_PrProductionBalLot_Item_Lot");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.ContributionKey, x.ProductionLocationId, x.StockStatusCode })
            .IsUnique()
            .HasFilter("[BalanceStage] = N'MATERIAL' AND [ContributionKey] IS NOT NULL")
            .HasDatabaseName("UQ_PrProductionBalLot_V2_Material");
        builder.HasIndex(x => new
            {
                x.CompanyCode, x.BranchCode, x.WorkOrderId, x.ProducingRouteStepId,
                x.BalanceStage, x.ItemCode, x.PoolCode, x.PhysicalLotNo,
                x.ProductionLocationId, x.StockStatusCode
            })
            .IsUnique()
            .HasFilter("[BalanceStage] IN (N'PROCESS_WIP',N'ROUTE_WIP',N'FG_STAGING')")
            .HasDatabaseName("UQ_PrProductionBalLot_V2_Wip");
    }
}
