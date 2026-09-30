using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Production;

public sealed class ProductionWorkOrderOperationConfiguration : IEntityTypeConfiguration<ProductionWorkOrderOperation>
{
    public void Configure(EntityTypeBuilder<ProductionWorkOrderOperation> builder)
    {
        builder.ToTable("PrWorkOrderOperation");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.WorkOrderId).HasColumnName("WorkOrderID");
        builder.Property(x => x.RouteStepId).HasColumnName("RouteStepID");
        builder.Property(x => x.SourceOperationId).HasColumnName("SourceOperationID");

        // Legacy flattened sequence. Kept for version-1 rows only; ProcessSequence is authoritative
        // inside a route step.
        builder.Property(x => x.SequenceNo);

        builder.Property(x => x.WorkCentreCode).HasMaxLength(20);
        builder.Property(x => x.WorkCentreDescription).HasMaxLength(100);
        builder.Property(x => x.OperationCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.OperationDescription).HasMaxLength(200);
        builder.Property(x => x.ProcessType).HasMaxLength(20);
        ConfigureQty(builder.Property(x => x.StandardDurationMinutes));
        ConfigureQty(builder.Property(x => x.PlannedInputQty));
        builder.Property(x => x.PlannedInputUom).HasColumnName("PlannedInputUOM").HasMaxLength(10);
        ConfigureQty(builder.Property(x => x.PlannedOutputQty));
        builder.Property(x => x.PlannedOutputUom).HasColumnName("PlannedOutputUOM").HasMaxLength(10);
        ConfigureQty(builder.Property(x => x.PlannedQty));
        ConfigureQty(builder.Property(x => x.SetupLossQty));
        ConfigureQty(builder.Property(x => x.OperationLossQty));
        ConfigureQty(builder.Property(x => x.InputQty));
        ConfigureQty(builder.Property(x => x.ProcessedQty));
        ConfigureQty(builder.Property(x => x.GoodQty));
        ConfigureQty(builder.Property(x => x.ScrapQty));
        ConfigureQty(builder.Property(x => x.RejectQty));
        ConfigureQty(builder.Property(x => x.HoldQty));
        ConfigureQty(builder.Property(x => x.ReworkQty));
        ConfigureQty(builder.Property(x => x.TransferredQty));
        ConfigureQty(builder.Property(x => x.RemainingQty));

        builder.Property(x => x.CalendarSourceType).HasMaxLength(20);
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

        // Stable source identity inside a route step (plan §6.2).
        builder.HasIndex(x => new { x.RouteStepId, x.SourceOperationKey })
            .IsUnique()
            .HasFilter("[SourceOperationKey] IS NOT NULL")
            .HasDatabaseName("UQ_PrWorkOrderOperation_RouteStep_SourceKey");

        // ProcessSequence is deliberately NOT unique: parallel processes are supported.
        builder.HasIndex(x => new { x.RouteStepId, x.ProcessSequence })
            .HasDatabaseName("IX_PrWorkOrderOperation_RouteStep_Process");

        // Version-1 rows have no route step and keep their historical identity.
        builder.HasIndex(x => new { x.WorkOrderId, x.SequenceNo })
            .IsUnique()
            .HasFilter("[RouteStepID] IS NULL")
            .HasDatabaseName("UQ_PrWorkOrderOperation_Order_Sequence_Legacy");

        builder.HasIndex(x => x.WorkOrderId)
            .HasDatabaseName("IX_PrWorkOrderOperation_WorkOrderID");

        // Legacy generic resources. Deprecated; new rows use machines and labour (plan §6.4).
        builder.HasMany(x => x.Resources)
            .WithOne(x => x.Operation!)
            .HasForeignKey(x => x.OperationId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureQty(PropertyBuilder<decimal> property) =>
        property.HasPrecision(18, 4).HasDefaultValue(0m).ValueGeneratedNever();
}

public sealed class ProductionWorkOrderResourceConfiguration : IEntityTypeConfiguration<ProductionWorkOrderResource>
{
    public void Configure(EntityTypeBuilder<ProductionWorkOrderResource> builder)
    {
        builder.ToTable("PrWorkOrderResource");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.OperationId).HasColumnName("OperationID");
        builder.Property(x => x.ResourceType).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ResourceCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.ResourceDescription).HasMaxLength(200);
        ConfigureQty(builder.Property(x => x.PlannedUnits));
        ConfigureQty(builder.Property(x => x.SetupMinutes));
        ConfigureQty(builder.Property(x => x.RunMinutes));
        ConfigureQty(builder.Property(x => x.QueueMinutes));
        builder.Property(x => x.Rate).HasPrecision(19, 6).HasDefaultValue(0m).ValueGeneratedNever();
        builder.Property(x => x.PlannedAmount).HasPrecision(19, 4).HasDefaultValue(0m).ValueGeneratedNever();
        builder.Property(x => x.CreatedBy).HasMaxLength(10);
        builder.HasIndex(x => new { x.OperationId, x.SequenceNo })
            .IsUnique()
            .HasDatabaseName("UQ_PrWorkOrderResource_Operation_Sequence");
    }

    private static void ConfigureQty(PropertyBuilder<decimal> property) =>
        property.HasPrecision(18, 4).HasDefaultValue(0m).ValueGeneratedNever();
}
