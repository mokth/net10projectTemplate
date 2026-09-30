using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Planning;

public sealed class PrBomOperationConfiguration : IEntityTypeConfiguration<PrBomOperation>
{
    public void Configure(EntityTypeBuilder<PrBomOperation> builder)
    {
        builder.ToTable("PrBomOperation");
        builder.HasKey(x => x.Uid);
        builder.HasIndex(x => new { x.BomHdrId, x.OperationKey }).IsUnique();
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.BomHdrId).HasColumnName("BomHdrID");
        builder.Property(x => x.RouteStepId).HasColumnName("RouteStepID");
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.WorkCentreCode).HasMaxLength(20).IsRequired();
        builder.Property(x => x.OutputItemCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.OutputBaseQty).HasPrecision(18, 4);
        builder.Property(x => x.OutputUom).HasColumnName("OutputUOM").HasMaxLength(10);
        builder.Property(x => x.OperationCode).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ProcessType).HasMaxLength(20).IsRequired();
        builder.Property(x => x.StandardDurationMinutes).HasPrecision(18, 4);
        builder.Property(x => x.SetupLossQty).HasPrecision(18, 4);
        builder.Property(x => x.OperationLossQty).HasPrecision(18, 4);
        builder.Property(x => x.Remark).HasMaxLength(500);
        builder.Property(x => x.CreatedBy).HasMaxLength(10);
        builder.Property(x => x.ModifiedBy).HasMaxLength(10);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => new { x.RouteStepId, x.ProcessSequence })
            .IsUnique()
            .HasFilter("[RouteStepID] IS NOT NULL")
            .HasDatabaseName("UQ_PrBomOperation_RouteStep_Sequence");
        builder.HasIndex(x => new { x.CompanyCode, x.WorkCentreCode })
            .HasDatabaseName("IX_PrBomOperation_Company_WorkCentre");

        builder.HasOne(x => x.Header)
            .WithMany(x => x.Operations)
            .HasForeignKey(x => x.BomHdrId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.RouteStep)
            .WithMany(x => x.Operations)
            .HasForeignKey(x => x.RouteStepId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasMany(x => x.Machines)
            .WithOne(x => x.Operation!)
            .HasForeignKey(x => x.OperationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class PrBomMachineOptionConfiguration : IEntityTypeConfiguration<PrBomMachineOption>
{
    public void Configure(EntityTypeBuilder<PrBomMachineOption> builder)
    {
        builder.ToTable("PrBomMachineOption");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.OperationId).HasColumnName("OperationID");
        builder.Property(x => x.MachineCode).HasMaxLength(20).IsRequired();
        builder.Property(x => x.MachineDescription).HasMaxLength(200);
        builder.Property(x => x.Priority).HasDefaultValue(1).ValueGeneratedNever();
        builder.Property(x => x.CycleSeconds).HasPrecision(18, 4);
        builder.Property(x => x.OutputPerCycle).HasPrecision(18, 4).HasDefaultValue(1m).ValueGeneratedNever();
        builder.Property(x => x.ConversionSeconds).HasPrecision(18, 4);
        builder.Property(x => x.SetupSeconds).HasPrecision(18, 4);
        builder.Property(x => x.QueueSeconds).HasPrecision(18, 4);
        builder.Property(x => x.MachineRatePerHour).HasPrecision(18, 6);
        builder.Ignore(x => x.IsDefault);
        builder.Property(x => x.CreatedBy).HasMaxLength(10);
        builder.Property(x => x.ModifiedBy).HasMaxLength(10);
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => new { x.OperationId, x.MachineCode })
            .IsUnique()
            .HasDatabaseName("UQ_PrBomMachineOption_Operation_Machine");
        builder.HasIndex(x => x.OperationId)
            .IsUnique()
            .HasFilter("[IsPrimary] = 1")
            .HasDatabaseName("UX_PrBomMachineOption_OneDefault");
        builder.HasIndex(x => new { x.OperationId, x.Priority })
            .IsUnique()
            .HasDatabaseName("UQ_PrBomMachineOption_Operation_Priority");
        builder.HasMany(x => x.Labours)
            .WithOne(x => x.MachineOption!)
            .HasForeignKey(x => x.MachineOptionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class PrBomLabourStandardConfiguration : IEntityTypeConfiguration<PrBomLabourStandard>
{
    public void Configure(EntityTypeBuilder<PrBomLabourStandard> builder)
    {
        builder.ToTable("PrBomLabourStandard");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.MachineOptionId).HasColumnName("MachineOptionID");
        builder.Property(x => x.LabourCode).HasMaxLength(20).IsRequired();
        builder.Property(x => x.LabourDescription).HasMaxLength(200);
        builder.Property(x => x.CostPerOutputUnit).HasPrecision(18, 6);
        builder.Property(x => x.CreatedBy).HasMaxLength(10);
        builder.Property(x => x.ModifiedBy).HasMaxLength(10);
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => new { x.MachineOptionId, x.LabourCode })
            .IsUnique()
            .HasDatabaseName("UQ_PrBomLabourStandard_Machine_Labour");
    }
}
