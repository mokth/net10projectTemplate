using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Production;

public sealed class ProductionOutputMaterialConfiguration : IEntityTypeConfiguration<ProductionOutputMaterial>
{
    public void Configure(EntityTypeBuilder<ProductionOutputMaterial> builder)
    {
        builder.ToTable("PrProductionOutputMaterial", table =>
        {
            table.HasCheckConstraint(
                "CK_PrProductionOutputMaterial_Qty",
                "[ConsumeQty] >= 0 AND [StandardQty] >= 0 AND [TolerancePercent] >= 0 "
                + "AND [ConversionFactorToBase] > 0");
            table.HasCheckConstraint(
                "CK_PrProductionOutputMaterial_Identity",
                "([IsHandoff] = 0 AND [WorkOrderMaterialID] IS NOT NULL AND [HandoffFromOperationID] IS NULL) "
                + "OR ([IsHandoff] = 1 AND [WorkOrderMaterialID] IS NULL AND [HandoffFromOperationID] IS NOT NULL)");
        });

        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.ProductionOutputId).HasColumnName("ProductionOutputID");
        builder.Property(x => x.WorkOrderMaterialId).HasColumnName("WorkOrderMaterialID");
        builder.Property(x => x.HandoffFromOperationId).HasColumnName("HandoffFromOperationID");
        builder.Property(x => x.ComponentCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.RequiredUom).HasColumnName("RequiredUOM").HasMaxLength(10).IsRequired();
        builder.Property(x => x.SupplySource).HasMaxLength(40).IsRequired();
        builder.Property(x => x.IssueMethod).HasMaxLength(20).IsRequired();
        builder.Property(x => x.TolerancePercent).HasPrecision(18, 4);
        builder.Property(x => x.WoBomRequiredQty).HasPrecision(18, 4);
        builder.Property(x => x.ConversionFactorToBase).HasPrecision(18, 8).HasDefaultValue(1m).ValueGeneratedNever();
        builder.Property(x => x.BaseUom).HasColumnName("BaseUOM").HasMaxLength(10);
        builder.Property(x => x.StandardQty).HasPrecision(18, 4);
        builder.Property(x => x.ConsumeQty).HasPrecision(18, 4);
        builder.Property(x => x.VarianceQty).HasPrecision(18, 4);
        builder.Property(x => x.VarianceReasonCode).HasMaxLength(30);
        builder.Property(x => x.VarianceReasonText).HasMaxLength(250);
        builder.Property(x => x.CreatedBy).HasMaxLength(10).IsRequired();
        builder.Property(x => x.ModifiedBy).HasMaxLength(10);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasOne(x => x.ProductionOutput)
            .WithMany(x => x.Materials)
            .HasForeignKey(x => new { x.ProductionOutputId, x.CompanyCode, x.BranchCode })
            .HasPrincipalKey(x => new { x.Uid, x.CompanyCode, x.BranchCode })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.WorkOrderMaterial)
            .WithMany()
            .HasForeignKey(x => x.WorkOrderMaterialId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.ProductionOutputId, x.WorkOrderMaterialId })
            .IsUnique()
            .HasFilter("[WorkOrderMaterialID] IS NOT NULL")
            .HasDatabaseName("UQ_PrProductionOutputMaterial_Output_Material");
        builder.HasIndex(x => x.ProductionOutputId)
            .IsUnique()
            .HasFilter("[IsHandoff] = 1")
            .HasDatabaseName("UQ_PrProductionOutputMaterial_Output_Handoff");
        builder.HasIndex(x => x.ProductionOutputId)
            .HasDatabaseName("IX_PrProductionOutputMaterial_Output");
        builder.HasIndex(x => x.WorkOrderMaterialId)
            .HasDatabaseName("IX_PrProductionOutputMaterial_WorkOrderMaterial");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.ComponentCode })
            .HasDatabaseName("IX_PrProductionOutputMaterial_Component");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.VarianceReasonCode })
            .HasDatabaseName("IX_PrProductionOutputMaterial_Reason");
    }
}
