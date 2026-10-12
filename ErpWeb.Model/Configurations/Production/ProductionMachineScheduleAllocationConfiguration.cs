using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Production;

public sealed class ProductionMachineScheduleAllocationConfiguration
    : IEntityTypeConfiguration<ProductionMachineScheduleAllocation>
{
    public void Configure(EntityTypeBuilder<ProductionMachineScheduleAllocation> builder)
    {
        builder.ToTable("PrMachineScheduleAllocation", table => table.HasCheckConstraint(
            "CK_PrMachineScheduleAllocation_PositiveInterval",
            "[StartDateTime] < [EndDateTime]"));

        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.MachineCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.WorkOrderId).HasColumnName("WorkOrderID");
        builder.Property(x => x.WorkOrderOperationId).HasColumnName("WorkOrderOperationID");
        builder.Property(x => x.WorkOrderMachineId).HasColumnName("WorkOrderMachineID");
        builder.Property(x => x.ScheduleRevision).HasDefaultValue(0).ValueGeneratedNever();
        builder.Property(x => x.FragmentNo).HasDefaultValue(1).ValueGeneratedNever();
        builder.Property(x => x.StartDateTime).HasColumnType("datetime2");
        builder.Property(x => x.EndDateTime).HasColumnType("datetime2");
        builder.Property(x => x.ScheduleSourceHash).HasMaxLength(64).IsFixedLength().IsUnicode(false);
        builder.Property(x => x.CreatedDate).HasColumnType("datetime2");
        builder.Property(x => x.CreatedBy).HasMaxLength(10).IsRequired();
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => new { x.WorkOrderMachineId, x.ScheduleRevision, x.FragmentNo })
            .IsUnique()
            .HasDatabaseName("UQ_PrMachineScheduleAllocation_Machine_Revision_Fragment");
        builder.HasIndex(x => new { x.CompanyCode, x.MachineCode, x.StartDateTime, x.EndDateTime })
            .HasDatabaseName("IX_PrMachineScheduleAllocation_Machine_Start_End");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.WorkOrderId, x.WorkOrderOperationId })
            .HasDatabaseName("IX_PrMachineScheduleAllocation_Order_Operation");
    }
}
