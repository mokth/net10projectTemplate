using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Production;

public sealed class ProductionWorkOrderConfiguration : IEntityTypeConfiguration<ProductionWorkOrder>
{
    public void Configure(EntityTypeBuilder<ProductionWorkOrder> builder)
    {
        builder.ToTable("PrWorkOrder");
        builder.HasKey(x => x.Uid);

        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.LocationCode).HasMaxLength(10);
        builder.Property(x => x.WorkOrderNo).HasMaxLength(30).IsRequired();
        builder.Property(x => x.SnapshotRevision).HasDefaultValue(1).ValueGeneratedNever();
        builder.Property(x => x.SnapshotHash).HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();

        // Physically the legacy SnapshotAsOfDate column (plan §6.6 compatibility mapping).
        builder.Property(x => x.DefinitionEffectiveDate).HasColumnName("SnapshotAsOfDate").HasColumnType("date");

        builder.Property(x => x.ProductCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.ProductDescription).HasMaxLength(200);
        builder.Property(x => x.OutputUom).HasColumnName("OutputUOM").HasMaxLength(10);
        builder.Property(x => x.SourceDefinitionCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.SourceDefinitionName).HasMaxLength(100);
        builder.Property(x => x.SourceBomHdrId).HasColumnName("SourceBomHdrID");
        builder.Property(x => x.BomBaseQty).HasPrecision(18, 4);
        builder.Property(x => x.BomBaseUom).HasColumnName("BomBaseUOM").HasMaxLength(10);
        ConfigureQty(builder.Property(x => x.PlannedQty));
        ConfigureQty(builder.Property(x => x.GoodQty));
        ConfigureQty(builder.Property(x => x.ScrapQty));
        ConfigureQty(builder.Property(x => x.RejectQty));
        ConfigureQty(builder.Property(x => x.HoldQty));
        ConfigureQty(builder.Property(x => x.ApprovedVarianceQty));
        ConfigureQty(builder.Property(x => x.RemainingQty));

        // Plant-local scheduling uses datetime2 so time-of-day survives (plan §6.5).
        builder.Property(x => x.PlannedStartDateTime).HasColumnType("datetime2");
        builder.Property(x => x.PlannedCompletionDateTime).HasColumnType("datetime2");

        builder.Property(x => x.SchedulingDirection).HasMaxLength(10).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.SourceType).HasMaxLength(30).IsRequired();
        builder.Property(x => x.SourceReference).HasMaxLength(80);
        builder.Property(x => x.Remark).HasMaxLength(1000);

        // Snapshot provenance (plan §6.6).
        builder.Property(x => x.SourceEffectiveFrom).HasColumnType("datetime2");
        builder.Property(x => x.SourceProductDefinitionRevisionId).HasColumnName("SourceProductDefinitionRevisionID");
        builder.Property(x => x.DefinitionSourceHash).HasMaxLength(64).IsFixedLength().IsUnicode(false);
        builder.Property(x => x.DefinitionSourceHashVersion);
        builder.Property(x => x.SnapshotFormatVersion)
            .HasDefaultValue(ProductionSnapshotFormatVersions.Legacy).ValueGeneratedNever();
        builder.Property(x => x.SnapshotHashVersion)
            .HasDefaultValue(ProductionSnapshotHashVersions.Current).ValueGeneratedNever();
        builder.Property(x => x.IsLegacySnapshot).HasDefaultValue(true).ValueGeneratedNever();
        builder.Property(x => x.LegacySnapshotReason).HasMaxLength(500);
        builder.Property(x => x.ScheduleAnchorDateTime).HasColumnType("datetime2");
        builder.Property(x => x.ScheduleCalculationTrace);

        builder.Property(x => x.ReleasedBy).HasMaxLength(10);
        builder.Property(x => x.CancelledBy).HasMaxLength(10);
        builder.Property(x => x.CancellationReason).HasMaxLength(500);
        builder.Property(x => x.CreatedBy).HasMaxLength(10);
        builder.Property(x => x.ModifiedBy).HasMaxLength(10);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => new { x.CompanyCode, x.WorkOrderNo })
            .IsUnique()
            .HasDatabaseName("UQ_PrWorkOrder_Company_WorkOrderNo");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.Status, x.PlannedStartDateTime })
            .HasDatabaseName("IX_PrWorkOrder_Company_Branch_Status_Start");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.ProductCode })
            .HasDatabaseName("IX_PrWorkOrder_Company_Branch_Product");
        builder.HasIndex(x => new { x.CompanyCode, x.ProductCode, x.SourceDefinitionCode })
            .HasDatabaseName("IX_PrWorkOrder_Company_Product_Definition");
        builder.HasIndex(x => x.SourceBomHdrId)
            .HasDatabaseName("IX_PrWorkOrder_SourceBomHdrID");
        builder.HasIndex(x => new { x.CompanyCode, x.SnapshotFormatVersion })
            .HasDatabaseName("IX_PrWorkOrder_Company_SnapshotFormat");

        builder.HasOne(x => x.SourceBomHeader)
            .WithMany()
            .HasForeignKey(x => x.SourceBomHdrId)
            .OnDelete(DeleteBehavior.Restrict);

        // Single cascade ownership path: WorkOrder -> RouteStep -> Operation -> (Material | Machine
        // -> Labour | Labour) (plan §6.6).
        builder.HasMany(x => x.RouteSteps)
            .WithOne(x => x.WorkOrder!)
            .HasForeignKey(x => x.WorkOrderId)
            .OnDelete(DeleteBehavior.Cascade);

        // Descendant tables keep a WorkOrderID pointer for querying and legacy rows, but it must
        // NOT cascade: the owning path already reaches them through RouteStep -> Operation. A second
        // structural cascade path from PrWorkOrder would make SQL Server reject the schema with a
        // multiple-cascade-path error (plan §6.6). Services delete legacy rows explicitly.
        builder.HasMany(x => x.Materials)
            .WithOne(x => x.WorkOrder!)
            .HasForeignKey(x => x.WorkOrderId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasMany(x => x.Operations)
            .WithOne(x => x.WorkOrder!)
            .HasForeignKey(x => x.WorkOrderId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasMany(x => x.AuditEvents)
            .WithOne(x => x.WorkOrder!)
            .HasForeignKey(x => x.WorkOrderId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.DemandAllocations)
            .WithOne(x => x.WorkOrder)
            .HasForeignKey(x => x.WorkOrderId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.ChangeOrders)
            .WithOne(x => x.WorkOrder!)
            .HasForeignKey(x => x.WorkOrderId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.PostingLinks)
            .WithOne(x => x.WorkOrder!)
            .HasForeignKey(x => x.WorkOrderId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureQty(PropertyBuilder<decimal> property) =>
        property.HasPrecision(18, 4).HasDefaultValue(0m).ValueGeneratedNever();
}

public sealed class ProductionWorkOrderRouteStepConfiguration : IEntityTypeConfiguration<ProductionWorkOrderRouteStep>
{
    public void Configure(EntityTypeBuilder<ProductionWorkOrderRouteStep> builder)
    {
        builder.ToTable("PrWorkOrderRouteStep");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.WorkOrderId).HasColumnName("WorkOrderID");
        builder.Property(x => x.SourceRouteStepId).HasColumnName("SourceRouteStepID");
        builder.Property(x => x.WorkCentreCode).HasMaxLength(20).IsRequired();
        builder.Property(x => x.WorkCentreDescription).HasMaxLength(100);
        builder.Property(x => x.OutputItemCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.OutputItemDescription).HasMaxLength(200);
        builder.Property(x => x.OutputType).HasMaxLength(20);
        builder.Property(x => x.YieldPercent).HasPrecision(9, 4);
        ConfigureQty(builder.Property(x => x.OutputBaseQty));
        builder.Property(x => x.OutputUom).HasColumnName("OutputUOM").HasMaxLength(10);
        builder.Property(x => x.OutputBaseUom).HasColumnName("OutputBaseUOM").HasMaxLength(10);
        builder.Property(x => x.OutputConversionFactorToBase).HasPrecision(18, 8);
        ConfigureQty(builder.Property(x => x.PlannedQty));
        builder.Property(x => x.PlannedStartDateTime).HasColumnType("datetime2");
        builder.Property(x => x.PlannedCompletionDateTime).HasColumnType("datetime2");
        builder.Property(x => x.CreatedBy).HasMaxLength(10);
        builder.Property(x => x.ModifiedBy).HasMaxLength(10);
        builder.Property(x => x.RowVersion).IsRowVersion();

        // StageSequence is deliberately NOT unique: equal values are parallel route steps.
        builder.HasIndex(x => new { x.WorkOrderId, x.StageSequence })
            .HasDatabaseName("IX_PrWorkOrderRouteStep_Order_Stage");

        // Stable source identity when a version-2 snapshot was built from a routed definition.
        builder.HasIndex(x => new { x.WorkOrderId, x.SourceRouteStepKey })
            .IsUnique()
            .HasFilter("[SourceRouteStepKey] IS NOT NULL")
            .HasDatabaseName("UQ_PrWorkOrderRouteStep_Order_SourceKey");

        builder.HasIndex(x => x.OutputItemCode)
            .HasDatabaseName("IX_PrWorkOrderRouteStep_OutputItem");

        builder.HasMany(x => x.Operations)
            .WithOne(x => x.RouteStep!)
            .HasForeignKey(x => x.RouteStepId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureQty(PropertyBuilder<decimal> property) =>
        property.HasPrecision(18, 4).HasDefaultValue(0m).ValueGeneratedNever();
}
