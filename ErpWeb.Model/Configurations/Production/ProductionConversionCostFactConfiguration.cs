using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Production;

public sealed class ProductionConversionCostFactConfiguration : IEntityTypeConfiguration<ProductionConversionCostFact>
{
    public void Configure(EntityTypeBuilder<ProductionConversionCostFact> builder)
    {
        builder.ToTable("PrProductionConversionCostFact", table =>
        {
            table.HasCheckConstraint(
                "CK_PrProductionConversionCostFact_Type",
                "[CostType] IN ('LABOUR','MACHINE','UTILITIES_OVERHEAD','OTHER')");
            table.HasCheckConstraint(
                "CK_PrProductionConversionCostFact_Positive",
                "[BasisQty] > 0 AND [RatePerOutputUnit] > 0 AND [CostAmount] > 0");
            table.HasCheckConstraint(
                "CK_PrProductionConversionCostFact_SourceShape",
                "([CostType] = 'LABOUR' AND [WorkOrderLabourID] IS NOT NULL AND [WorkOrderMachineID] IS NULL) "
                + "OR ([CostType] = 'MACHINE' AND [WorkOrderLabourID] IS NULL AND [WorkOrderMachineID] IS NOT NULL) "
                + "OR ([CostType] IN ('UTILITIES_OVERHEAD','OTHER') AND [WorkOrderLabourID] IS NULL AND [WorkOrderMachineID] IS NULL)");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.StockPostingId).HasColumnName("StockPostingID");
        builder.Property(x => x.ProductionOutputId).HasColumnName("ProductionOutputID");
        builder.Property(x => x.ProductionMovementId).HasColumnName("ProductionMovementID");
        builder.Property(x => x.WorkOrderId).HasColumnName("WorkOrderID");
        builder.Property(x => x.RouteStepId).HasColumnName("RouteStepID");
        builder.Property(x => x.WorkOrderOperationId).HasColumnName("WorkOrderOperationID");
        builder.Property(x => x.CostType).HasMaxLength(20).IsRequired();
        builder.Property(x => x.SourceLineKey).HasMaxLength(80).IsRequired();
        builder.Property(x => x.WorkOrderLabourId).HasColumnName("WorkOrderLabourID");
        builder.Property(x => x.WorkOrderMachineId).HasColumnName("WorkOrderMachineID");
        builder.Property(x => x.BasisQty).HasPrecision(18, 4);
        builder.Property(x => x.BasisUom).HasMaxLength(10).IsRequired();
        builder.Property(x => x.RatePerOutputUnit).HasPrecision(19, 6);
        builder.Property(x => x.CostAmount).HasPrecision(19, 6);
        builder.Property(x => x.ReversesFactId).HasColumnName("ReversesFactID");
        builder.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(7)");
        builder.Property(x => x.CreatedBy).HasMaxLength(10).IsRequired();

        builder.HasOne<StockPosting>()
            .WithMany()
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.StockPostingId })
            .HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProductionOutput>()
            .WithMany()
            .HasForeignKey(x => x.ProductionOutputId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProductionBalLotMovement>()
            .WithMany()
            .HasForeignKey(x => x.ProductionMovementId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProductionWorkOrder>()
            .WithMany()
            .HasForeignKey(x => x.WorkOrderId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProductionWorkOrderRouteStep>()
            .WithMany()
            .HasForeignKey(x => x.RouteStepId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProductionWorkOrderOperation>()
            .WithMany()
            .HasForeignKey(x => x.WorkOrderOperationId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProductionWorkOrderLabour>()
            .WithMany()
            .HasForeignKey(x => x.WorkOrderLabourId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProductionWorkOrderMachine>()
            .WithMany()
            .HasForeignKey(x => x.WorkOrderMachineId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProductionConversionCostFact>()
            .WithMany()
            .HasForeignKey(x => x.ReversesFactId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.ProductionMovementId, x.SourceLineKey })
            .IsUnique()
            .HasDatabaseName("UQ_PrProductionConversionCostFact_Movement_Source");
        builder.HasIndex(x => x.ReversesFactId)
            .IsUnique()
            .HasFilter("[ReversesFactID] IS NOT NULL")
            .HasDatabaseName("UQ_PrProductionConversionCostFact_Reversal");
        builder.HasIndex(x => new { x.StockPostingId, x.ProductionOutputId })
            .HasDatabaseName("IX_PrProductionConversionCostFact_Posting_Output");
        builder.HasIndex(x => new { x.WorkOrderId, x.WorkOrderOperationId })
            .HasDatabaseName("IX_PrProductionConversionCostFact_WorkOrder_Operation");
    }
}
