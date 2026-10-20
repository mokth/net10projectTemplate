using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Production;

public sealed class ProductionMachineScheduleSegmentConfiguration
    : IEntityTypeConfiguration<ProductionMachineScheduleSegment>
{
    public void Configure(EntityTypeBuilder<ProductionMachineScheduleSegment> builder)
    {
        builder.ToTable("PrMachineScheduleSegment", table =>
        {
            table.HasCheckConstraint(
                "CK_PrMachineScheduleSegment_PositiveInterval",
                "[PlannedStartDateTime] < [PlannedCompletionDateTime]");
            table.HasCheckConstraint(
                "CK_PrMachineScheduleSegment_Sequence",
                "[SegmentSequence] > 0");
            table.HasCheckConstraint(
                "CK_PrMachineScheduleSegment_Qty",
                "[PlannedOutputQty] >= 0");
            table.HasCheckConstraint(
                "CK_PrMachineScheduleSegment_Kind",
                "[SegmentKind] IN ('ORIGINAL','MANUAL_SPLIT','URGENT_INSERT','RESUME','HOLD_RESUME','MAINTENANCE_RESUME','REMAINING_REPLAN')");
            table.HasCheckConstraint(
                "CK_PrMachineScheduleSegment_Status",
                "[SegmentStatus] IN ('PLANNED','EXECUTION_LOCKED','SUPERSEDED')");
        });

        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.WorkOrderId).HasColumnName("WorkOrderID");
        builder.Property(x => x.WorkOrderOperationId).HasColumnName("WorkOrderOperationID");
        builder.Property(x => x.WorkOrderMachineId).HasColumnName("WorkOrderMachineID");
        builder.Property(x => x.MachineCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.SegmentKind).HasMaxLength(30).IsRequired();
        builder.Property(x => x.SegmentStatus).HasMaxLength(20).IsRequired();
        builder.Property(x => x.SourceSegmentId).HasColumnName("SourceSegmentID");
        builder.Property(x => x.PlannedOutputQty).HasPrecision(18, 4);
        builder.Property(x => x.OutputUom).HasColumnName("OutputUOM").HasMaxLength(10);
        builder.Property(x => x.PlannedCycleCount).HasPrecision(18, 4);
        builder.Property(x => x.PlannedCycleSlots).HasPrecision(18, 4);
        builder.Property(x => x.ScheduledRunMinutes).HasPrecision(18, 4);
        builder.Property(x => x.ScheduledSetupSeconds).HasPrecision(18, 4);
        builder.Property(x => x.ScheduledConversionSeconds).HasPrecision(18, 4);
        builder.Property(x => x.ScheduledQueueSeconds).HasPrecision(18, 4);
        builder.Property(x => x.PlannedStartDateTime).HasColumnType("datetime2");
        builder.Property(x => x.PlannedCompletionDateTime).HasColumnType("datetime2");
        builder.Property(x => x.CalendarHorizonStart).HasColumnType("datetime2");
        builder.Property(x => x.CalendarHorizonEnd).HasColumnType("datetime2");
        builder.Property(x => x.SegmentScheduleSourceHash).HasMaxLength(64).IsFixedLength().IsUnicode(false);
        builder.Property(x => x.MachineScheduleRevision).HasDefaultValue(0).ValueGeneratedNever();
        builder.Property(x => x.ReasonCode).HasMaxLength(30);
        builder.Property(x => x.Reason).HasMaxLength(500);
        builder.Property(x => x.CreatedDate).HasColumnType("datetime2");
        builder.Property(x => x.CreatedBy).HasMaxLength(10);
        builder.Property(x => x.ModifiedDate).HasColumnType("datetime2");
        builder.Property(x => x.ModifiedBy).HasMaxLength(10);
        builder.Property(x => x.SupersededDate).HasColumnType("datetime2");
        builder.Property(x => x.SupersededBy).HasMaxLength(10);
        builder.Property(x => x.RowVersion).IsRowVersion();

        // No cascade/restrict FKs to replaceable Draft snapshot child rows. Historical segment rows
        // must survive Draft Refresh/Change Definition, which may recreate those rows.
        builder.HasIndex(x => new { x.CompanyCode, x.MachineCode, x.PlannedStartDateTime, x.PlannedCompletionDateTime })
            .HasDatabaseName("IX_PrMachineScheduleSegment_Machine_Range");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.WorkOrderId, x.WorkOrderOperationId, x.SegmentStatus })
            .HasDatabaseName("IX_PrMachineScheduleSegment_Order_Operation_Status");
        builder.HasIndex(x => new { x.WorkOrderMachineId, x.MachineScheduleRevision, x.SegmentSequence })
            .HasDatabaseName("IX_PrMachineScheduleSegment_Machine_Revision_Sequence");
    }
}

public sealed class ProductionWorkOrderScheduleHoldConfiguration
    : IEntityTypeConfiguration<ProductionWorkOrderScheduleHold>
{
    public void Configure(EntityTypeBuilder<ProductionWorkOrderScheduleHold> builder)
    {
        builder.ToTable("PrWorkOrderScheduleHold", table =>
        {
            table.HasCheckConstraint(
                "CK_PrWorkOrderScheduleHold_Type",
                "[HoldType] IN ('MATERIAL_SHORTAGE','QUALITY_HOLD','CUSTOMER_HOLD','OTHER')");
            table.HasCheckConstraint(
                "CK_PrWorkOrderScheduleHold_Status",
                "[Status] IN ('ACTIVE','RELEASED','CANCELLED')");
        });

        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.WorkOrderId).HasColumnName("WorkOrderID");
        builder.Property(x => x.WorkOrderOperationId).HasColumnName("WorkOrderOperationID");
        builder.Property(x => x.WorkOrderMachineId).HasColumnName("WorkOrderMachineID");
        builder.Property(x => x.HoldType).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.HoldFrom).HasColumnType("datetime2");
        builder.Property(x => x.ResumeNotBefore).HasColumnType("datetime2");
        builder.Property(x => x.Reason).HasMaxLength(500);
        builder.Property(x => x.CreatedDate).HasColumnType("datetime2");
        builder.Property(x => x.CreatedBy).HasMaxLength(10);
        builder.Property(x => x.ReleasedDate).HasColumnType("datetime2");
        builder.Property(x => x.ReleasedBy).HasMaxLength(10);
        builder.Property(x => x.CancelledDate).HasColumnType("datetime2");
        builder.Property(x => x.CancelledBy).HasMaxLength(10);
        builder.Property(x => x.ResolutionReason).HasMaxLength(500);
        builder.Property(x => x.RowVersion).IsRowVersion();

        // Scalar IDs only: holds must survive Draft snapshot-child replacement.
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.WorkOrderOperationId, x.Status })
            .HasDatabaseName("IX_PrWorkOrderScheduleHold_Operation_Status");
        builder.HasIndex(x => new { x.WorkOrderMachineId, x.Status })
            .HasDatabaseName("IX_PrWorkOrderScheduleHold_Machine_Status");
    }
}
