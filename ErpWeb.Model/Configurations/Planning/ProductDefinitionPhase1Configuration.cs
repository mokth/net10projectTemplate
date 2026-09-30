using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Planning;

public sealed class PrBomRouteStepConfiguration : IEntityTypeConfiguration<PrBomRouteStep>
{
    public void Configure(EntityTypeBuilder<PrBomRouteStep> builder)
    {
        builder.ToTable("PrBomRouteStep");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.BomHdrId).HasColumnName("BomHdrID");
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.WorkCentreCode).HasMaxLength(20).IsRequired();
        builder.Property(x => x.OutputItemCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.OutputType).HasMaxLength(20).IsRequired();
        builder.Property(x => x.StandardOutputQty).HasPrecision(18, 4);
        builder.Property(x => x.OutputUom).HasColumnName("OutputUOM").HasMaxLength(10).IsRequired();
        builder.Property(x => x.YieldPercent).HasPrecision(9, 4);
        builder.Property(x => x.CreatedBy).HasMaxLength(10);
        builder.Property(x => x.ModifiedBy).HasMaxLength(10);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => new { x.BomHdrId, x.RouteStepKey })
            .IsUnique()
            .HasDatabaseName("UQ_PrBomRouteStep_Header_Key");
        builder.HasIndex(x => new { x.BomHdrId, x.StageSequence })
            .HasDatabaseName("IX_PrBomRouteStep_Header_Stage");
    }
}

public sealed class PrBomLabourRequirementConfiguration : IEntityTypeConfiguration<PrBomLabourRequirement>
{
    public void Configure(EntityTypeBuilder<PrBomLabourRequirement> builder)
    {
        builder.ToTable("PrBomLabourRequirement");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.OperationId).HasColumnName("OperationID");
        builder.Property(x => x.MachineOptionId).HasColumnName("MachineOptionID");
        builder.Property(x => x.LabourCode).HasMaxLength(20).IsRequired();
        builder.Property(x => x.RequiredHeadcount).HasPrecision(9, 4);
        builder.Property(x => x.SetupMinutes).HasPrecision(18, 4);
        builder.Property(x => x.RunMinutes).HasPrecision(18, 4);
        builder.Property(x => x.CostRate).HasPrecision(18, 6);
        builder.Property(x => x.CostBasis).HasMaxLength(20).IsRequired();
        builder.Property(x => x.CreatedBy).HasMaxLength(10);
        builder.Property(x => x.ModifiedBy).HasMaxLength(10);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasOne(x => x.Operation)
            .WithMany(x => x.LabourRequirements)
            .HasForeignKey(x => x.OperationId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.MachineOption)
            .WithMany(x => x.LabourRequirements)
            .HasForeignKey(x => x.MachineOptionId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(x => new { x.OperationId, x.LabourCode })
            .IsUnique()
            .HasFilter("[MachineOptionID] IS NULL")
            .HasDatabaseName("UX_PrBomLabourRequirement_OperationWide");
        builder.HasIndex(x => new { x.OperationId, x.MachineOptionId, x.LabourCode })
            .IsUnique()
            .HasFilter("[MachineOptionID] IS NOT NULL")
            .HasDatabaseName("UX_PrBomLabourRequirement_MachineSpecific");
    }
}

public sealed class PrBomMaterialBranchDefaultConfiguration : IEntityTypeConfiguration<PrBomMaterialBranchDefault>
{
    public void Configure(EntityTypeBuilder<PrBomMaterialBranchDefault> builder)
    {
        builder.ToTable("PrBomMaterialBranchDefault");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.MaterialId).HasColumnName("MaterialID");
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.WarehouseCode).HasMaxLength(20).IsRequired();
        builder.Property(x => x.CreatedBy).HasMaxLength(10);
        builder.Property(x => x.ModifiedBy).HasMaxLength(10);
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => new { x.MaterialId, x.BranchCode })
            .IsUnique()
            .HasDatabaseName("UQ_PrBomMaterialBranchDefault_Material_Branch");
    }
}
