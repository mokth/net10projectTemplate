using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Production;

public sealed class ProductionMachineScheduleSegmentSetConfiguration
    : IEntityTypeConfiguration<ProductionMachineScheduleSegmentSet>
{
    public void Configure(EntityTypeBuilder<ProductionMachineScheduleSegmentSet> builder)
    {
        builder.ToTable("PrMachineScheduleSegmentSet", table =>
        {
            table.HasCheckConstraint("CK_PrMachineScheduleSegmentSet_Mode",
                "[Mode] IN ('FULL_PLAN','REMAINING_ONLY')");
            table.HasCheckConstraint("CK_PrMachineScheduleSegmentSet_PlanningState",
                "[PlanningState] IN ('SCHEDULED','HELD_UNSCHEDULED','MAINTENANCE_UNSCHEDULED','REPLAN_REQUIRED','COMPLETED')");
            table.HasCheckConstraint("CK_PrMachineScheduleSegmentSet_Qty", "[PlannedQtyBasis] >= 0");
            table.HasCheckConstraint("CK_PrMachineScheduleSegmentSet_Horizon",
                "[CalendarHorizonStart] IS NULL OR [CalendarHorizonEnd] IS NULL OR [CalendarHorizonStart] <= [CalendarHorizonEnd]");
        });

        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.WorkOrderId).HasColumnName("WorkOrderID");
        builder.Property(x => x.WorkOrderOperationId).HasColumnName("WorkOrderOperationID");
        builder.Property(x => x.WorkOrderMachineId).HasColumnName("WorkOrderMachineID");
        builder.Property(x => x.MachineCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Mode).HasMaxLength(20).IsRequired();
        builder.Property(x => x.PlanningState).HasMaxLength(30).IsRequired();
        builder.Property(x => x.PlannedQtyBasis).HasPrecision(18, 4);
        builder.Property(x => x.OutputUom).HasColumnName("OutputUOM").HasMaxLength(10);
        builder.Property(x => x.CalendarHorizonStart).HasColumnType("datetime2");
        builder.Property(x => x.CalendarHorizonEnd).HasColumnType("datetime2");
        builder.Property(x => x.ScheduleSourceHash).HasMaxLength(64).IsFixedLength().IsUnicode(false);
        builder.Property(x => x.Reason).HasMaxLength(500);
        builder.Property(x => x.CreatedDate).HasColumnType("datetime2");
        builder.Property(x => x.CreatedBy).HasMaxLength(10);
        builder.Property(x => x.ModifiedDate).HasColumnType("datetime2");
        builder.Property(x => x.ModifiedBy).HasMaxLength(10);
        builder.Property(x => x.SupersededDate).HasColumnType("datetime2");
        builder.Property(x => x.SupersededBy).HasMaxLength(10);
        builder.Property(x => x.RowVersion).IsRowVersion();

        // Snapshot child IDs are scalar: historical plans must survive Draft child replacement.
        builder.HasIndex(x => x.WorkOrderMachineId).IsUnique().HasFilter("[IsCurrent] = 1")
            .HasDatabaseName("UX_PrMachineScheduleSegmentSet_OneCurrent");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.WorkOrderId, x.WorkOrderOperationId })
            .HasDatabaseName("IX_PrMachineScheduleSegmentSet_Order_Operation");
    }
}

public sealed class ProductionMachineScheduleAllocationSegmentConfiguration
    : IEntityTypeConfiguration<ProductionMachineScheduleAllocationSegment>
{
    public void Configure(EntityTypeBuilder<ProductionMachineScheduleAllocationSegment> builder)
    {
        builder.ToTable("PrMachineScheduleAllocationSegment");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.AllocationId).HasColumnName("AllocationID");
        builder.Property(x => x.SegmentId).HasColumnName("SegmentID");
        builder.Property(x => x.CreatedDate).HasColumnType("datetime2");
        builder.Property(x => x.CreatedBy).HasMaxLength(10);
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => x.AllocationId).IsUnique()
            .HasDatabaseName("UX_PrMachineScheduleAllocationSegment_Allocation");
        builder.HasIndex(x => x.SegmentId)
            .HasDatabaseName("IX_PrMachineScheduleAllocationSegment_Segment");
    }
}
