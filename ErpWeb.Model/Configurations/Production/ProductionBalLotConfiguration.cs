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
            table.HasCheckConstraint("CK_PrProductionBalLot_Kind",
                "[Kind] IN ('MATERIAL_IN', 'WIP')");
            table.HasCheckConstraint("CK_PrProductionBalLot_Qty",
                "[Qty] >= 0 AND [BaseQty] >= 0 AND [ConversionFactorToBase] > 0");
            table.HasCheckConstraint("CK_PrProductionBalLot_Cost",
                "[TotalCost] >= 0 AND [AverageUnitCost] >= 0");
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
        builder.Property(x => x.TotalCost).HasPrecision(18, 4);
        builder.Property(x => x.AverageUnitCost).HasPrecision(18, 4);
        builder.Property(x => x.WorkOrderId).HasColumnName("WorkOrderID");
        builder.Property(x => x.WorkOrderNo).HasMaxLength(30).IsRequired();
        builder.Property(x => x.WorkOrderMaterialId).HasColumnName("WorkOrderMaterialID");
        builder.Property(x => x.OriginalIssueMovementId).HasColumnName("OriginalIssueMovementID");
        builder.Property(x => x.SourceIvBalLocId).HasColumnName("SourceIvBalLocID");
        builder.Property(x => x.WarehouseCode).HasMaxLength(20).IsRequired();
        builder.Property(x => x.LocationCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.LotNo).HasMaxLength(50).IsRequired();
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
    }
}
