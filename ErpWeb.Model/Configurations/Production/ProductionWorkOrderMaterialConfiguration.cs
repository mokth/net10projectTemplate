using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Production;

public sealed class ProductionWorkOrderMaterialConfiguration : IEntityTypeConfiguration<ProductionWorkOrderMaterial>
{
    public void Configure(EntityTypeBuilder<ProductionWorkOrderMaterial> builder)
    {
        builder.ToTable("PrWorkOrderMaterial", table =>
        {
            table.HasCheckConstraint(
                "CK_PrWorkOrderMaterial_Qty",
                "[RequiredQty] >= 0 AND [ScrapPercent] >= 0 AND [RequiredBaseQty] >= 0 "
                + "AND [ConversionFactorToBase] > 0");

            // NULL-safe equivalence between the supply source and the internal producer (plan §6.3).
            // SupplySource is NOT NULL, so neither comparison can evaluate to UNKNOWN and a NULL
            // source cannot slip a producer-less INTERNAL_ROUTE_WIP row past the constraint.
            // Legacy version-1 rows all carry PURCHASED with a null producer and therefore pass.
            table.HasCheckConstraint(
                "CK_PrWorkOrderMaterial_InternalWipProducer",
                "([SupplySource] = 'INTERNAL_ROUTE_WIP' AND [ProducingRouteStepID] IS NOT NULL) "
                + "OR ([SupplySource] <> 'INTERNAL_ROUTE_WIP' AND [ProducingRouteStepID] IS NULL)");
        });

        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.WorkOrderId).HasColumnName("WorkOrderID");
        builder.Property(x => x.WorkOrderOperationId).HasColumnName("WorkOrderOperationID");
        builder.Property(x => x.SourceOperationId).HasColumnName("SourceOperationID");
        builder.Property(x => x.AlternateGroupCode).HasMaxLength(30);
        builder.Property(x => x.LineNo);
        builder.Property(x => x.SourceBomHdrId).HasColumnName("SourceBomHdrID");
        builder.Property(x => x.SourceBomLineId).HasColumnName("SourceBomLineID");
        builder.Property(x => x.ProducingRouteStepId).HasColumnName("ProducingRouteStepID");
        builder.Property(x => x.ParentProductCode).HasMaxLength(30);
        builder.Property(x => x.BomPath).HasMaxLength(2000).IsRequired();
        builder.Property(x => x.ComponentCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.ComponentDescription).HasMaxLength(200);
        builder.Property(x => x.MfgType).HasMaxLength(20).IsRequired();
        ConfigureQty(builder.Property(x => x.ComponentQtyPerParent));
        builder.Property(x => x.StandardUom).HasColumnName("StandardUOM").HasMaxLength(10);
        ConfigureQty(builder.Property(x => x.BomOutputQty));
        builder.Property(x => x.BomOutputUom).HasColumnName("BomOutputUOM").HasMaxLength(10);
        ConfigureQty(builder.Property(x => x.ScrapPercent));
        ConfigureQty(builder.Property(x => x.Tolerance));
        builder.Property(x => x.IssueMethod).HasMaxLength(20).IsRequired();
        builder.Property(x => x.SupplySource).HasMaxLength(40).IsRequired();
        builder.Property(x => x.ComponentDefinitionCode).HasMaxLength(30);
        ConfigureQty(builder.Property(x => x.RequiredQty));
        builder.Property(x => x.RequiredUom).HasColumnName("RequiredUOM").HasMaxLength(10);
        ConfigureQty(builder.Property(x => x.RequiredBaseQty));
        builder.Property(x => x.BaseUom).HasColumnName("BaseUOM").HasMaxLength(10);
        builder.Property(x => x.ConversionFactorToBase)
            .HasPrecision(18, 8).HasDefaultValue(1m).ValueGeneratedNever();
        builder.Property(x => x.WarehouseCode).HasMaxLength(20);
        builder.Property(x => x.LocationCode).HasMaxLength(20);
        ConfigureQty(builder.Property(x => x.ReservedQty));
        ConfigureQty(builder.Property(x => x.PickedQty));
        ConfigureQty(builder.Property(x => x.IssuedQty));
        ConfigureQty(builder.Property(x => x.ReturnedQty));
        ConfigureQty(builder.Property(x => x.ConsumedQty));
        ConfigureQty(builder.Property(x => x.VarianceQty));
        builder.Property(x => x.CreatedBy).HasMaxLength(10);
        builder.Property(x => x.ModifiedBy).HasMaxLength(10);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => new { x.WorkOrderId, x.LineNo })
            .IsUnique()
            .HasDatabaseName("UQ_PrWorkOrderMaterial_Order_Line");

        // Stable source identity within the consuming operation (plan §6.6).
        builder.HasIndex(x => new { x.WorkOrderOperationId, x.SourceMaterialKey })
            .IsUnique()
            .HasFilter("[SourceMaterialKey] IS NOT NULL")
            .HasDatabaseName("UQ_PrWorkOrderMaterial_Operation_SourceKey");

        builder.HasIndex(x => new { x.ComponentCode, x.WarehouseCode })
            .HasDatabaseName("IX_PrWorkOrderMaterial_Component_Warehouse");
        builder.HasIndex(x => x.SourceBomHdrId)
            .HasDatabaseName("IX_PrWorkOrderMaterial_SourceBomHdrID");
        builder.HasIndex(x => x.SourceBomLineId)
            .HasDatabaseName("IX_PrWorkOrderMaterial_SourceBomLineID");
        builder.HasIndex(x => x.WorkOrderOperationId)
            .HasDatabaseName("IX_PrWorkOrderMaterial_WorkOrderOperationID");
        builder.HasIndex(x => x.ProducingRouteStepId)
            .HasDatabaseName("IX_PrWorkOrderMaterial_ProducingRouteStepID");

        builder.HasOne(x => x.SourceBomHeader)
            .WithMany()
            .HasForeignKey(x => x.SourceBomHdrId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.SourceBomLine)
            .WithMany()
            .HasForeignKey(x => x.SourceBomLineId)
            .OnDelete(DeleteBehavior.Restrict);

        // Operation -> Material is part of the single cascade ownership path.
        builder.HasOne(x => x.WorkOrderOperation)
            .WithMany(x => x.Materials)
            .HasForeignKey(x => x.WorkOrderOperationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Producer reference is a structural pointer only: NO ACTION keeps the single cascade path
        // intact (plan §6.6).
        builder.HasOne(x => x.ProducingRouteStep)
            .WithMany()
            .HasForeignKey(x => x.ProducingRouteStepId)
            .OnDelete(DeleteBehavior.NoAction);
    }

    private static void ConfigureQty(PropertyBuilder<decimal> property) =>
        property.HasPrecision(18, 4).HasDefaultValue(0m).ValueGeneratedNever();
}
