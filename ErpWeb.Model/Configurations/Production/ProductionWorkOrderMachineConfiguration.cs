using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Production;

public sealed class ProductionWorkOrderMachineConfiguration : IEntityTypeConfiguration<ProductionWorkOrderMachine>
{
    public void Configure(EntityTypeBuilder<ProductionWorkOrderMachine> builder)
    {
        builder.ToTable("PrWorkOrderMachine", table =>
        {
            table.HasCheckConstraint(
                "CK_PrWorkOrderMachine_Parallel",
                "[ParallelMachineCount] >= 1");
            table.HasCheckConstraint(
                "CK_PrWorkOrderMachine_OutputPerCycle",
                "[OutputPerCycle] > 0");
            table.HasCheckConstraint(
                "CK_PrWorkOrderMachine_Times",
                "[CycleSeconds] >= 0 AND [ConversionSeconds] >= 0 AND [SetupSeconds] >= 0 "
                + "AND [QueueSeconds] >= 0 AND [MachineRatePerHour] >= 0");
        });

        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.OperationId).HasColumnName("OperationID");
        builder.Property(x => x.SourceMachineOptionId).HasColumnName("SourceMachineOptionID");
        builder.Property(x => x.Priority).HasDefaultValue(1).ValueGeneratedNever();
        builder.Property(x => x.MachineCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.MachineDescription).HasMaxLength(200);
        builder.Property(x => x.IsDefault).HasDefaultValue(false).ValueGeneratedNever();
        builder.Property(x => x.IsSelected).HasDefaultValue(false).ValueGeneratedNever();
        builder.Property(x => x.ParallelMachineCount).HasDefaultValue(1).ValueGeneratedNever();
        builder.Property(x => x.CycleQuantityMode).HasMaxLength(20).IsRequired();
        ConfigureQty(builder.Property(x => x.CycleSeconds));
        ConfigureQty(builder.Property(x => x.OutputPerCycle));
        builder.Property(x => x.OutputPerCycleUom).HasColumnName("OutputPerCycleUOM").HasMaxLength(10);
        ConfigureQty(builder.Property(x => x.RequiredMachineOutputQty));
        builder.Property(x => x.RequiredMachineOutputUom).HasColumnName("RequiredMachineOutputUOM").HasMaxLength(10);
        ConfigureQty(builder.Property(x => x.PlannedCycleCount));
        ConfigureQty(builder.Property(x => x.PlannedCycleSlots));
        ConfigureQty(builder.Property(x => x.PlannedRunMinutes));
        ConfigureQty(builder.Property(x => x.ConversionSeconds));
        ConfigureQty(builder.Property(x => x.SetupSeconds));
        ConfigureQty(builder.Property(x => x.QueueSeconds));
        builder.Property(x => x.MachineRatePerHour).HasPrecision(19, 6).HasDefaultValue(0m).ValueGeneratedNever();

        builder.Property(x => x.CalendarSourceId).HasColumnName("CalendarSourceID");
        builder.Property(x => x.CalendarSourceLastModified).HasColumnType("datetime2");
        builder.Property(x => x.ScheduleSourceHash).HasMaxLength(64).IsFixedLength().IsUnicode(false);
        builder.Property(x => x.CalendarHorizonStart).HasColumnType("datetime2");
        builder.Property(x => x.CalendarHorizonEnd).HasColumnType("datetime2");
        builder.Property(x => x.PlannedStartDateTime).HasColumnType("datetime2");
        builder.Property(x => x.PlannedCompletionDateTime).HasColumnType("datetime2");

        builder.Property(x => x.CreatedBy).HasMaxLength(10);
        builder.Property(x => x.ModifiedBy).HasMaxLength(10);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => new { x.OperationId, x.SourceMachineKey })
            .IsUnique()
            .HasFilter("[SourceMachineKey] IS NOT NULL")
            .HasDatabaseName("UQ_PrWorkOrderMachine_Operation_SourceKey");

        builder.HasIndex(x => new { x.OperationId, x.Priority })
            .HasDatabaseName("IX_PrWorkOrderMachine_Operation_Priority");

        // Exactly one selected machine option per operation (plan §6.4).
        builder.HasIndex(x => x.OperationId)
            .IsUnique()
            .HasFilter("[IsSelected] = 1")
            .HasDatabaseName("UX_PrWorkOrderMachine_OneSelected");

        // Operation -> Machine is the single structural path leading to labour (Option A1).
        builder.HasOne(x => x.Operation)
            .WithMany(x => x.Machines)
            .HasForeignKey(x => x.OperationId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureQty(PropertyBuilder<decimal> property) =>
        property.HasPrecision(18, 4).HasDefaultValue(0m).ValueGeneratedNever();
}
