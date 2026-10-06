using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Production;

public sealed class ProductionOutputConfiguration : IEntityTypeConfiguration<ProductionOutput>
{
    public void Configure(EntityTypeBuilder<ProductionOutput> builder)
    {
        builder.ToTable("PrProductionOutput", table =>
        {
            table.HasCheckConstraint("CK_PrProductionOutput_Status",
                "[Status] IN ('NEW', 'POSTED', 'REVERSED')");
            table.HasCheckConstraint("CK_PrProductionOutput_Qty",
                "[GoodQty] >= 0 AND [ScrapQty] >= 0 AND [RejectQty] >= 0 AND [HoldQty] >= 0");
        });

        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.DocumentNo).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.WorkOrderId).HasColumnName("WorkOrderID");
        builder.Property(x => x.RouteStepId).HasColumnName("RouteStepID");
        builder.Property(x => x.WorkOrderOperationId).HasColumnName("WorkOrderOperationID");
        builder.Property(x => x.ProductionDate).HasColumnType("datetime2");
        builder.Property(x => x.ShiftCode).HasMaxLength(20);
        builder.Property(x => x.PlannedMachineCode).HasMaxLength(30);
        builder.Property(x => x.ActualMachineCode).HasMaxLength(30);
        builder.Property(x => x.OperatorCode).HasMaxLength(30);
        builder.Property(x => x.GoodQty).HasPrecision(18, 4);
        builder.Property(x => x.ScrapQty).HasPrecision(18, 4);
        builder.Property(x => x.RejectQty).HasPrecision(18, 4);
        builder.Property(x => x.HoldQty).HasPrecision(18, 4);
        builder.Property(x => x.OutputUom).HasColumnName("OutputUOM").HasMaxLength(10).IsRequired();
        builder.Property(x => x.OutputItemCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.OutputType).HasMaxLength(20);
        builder.Property(x => x.OutputLotNo).HasMaxLength(50).IsRequired();
        builder.Property(x => x.SnapshotHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.PostingRequestId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.PostedBy).HasMaxLength(10);
        builder.Property(x => x.ReversedBy).HasMaxLength(10);
        builder.Property(x => x.DeletedAtUtc).HasColumnType("datetime2");
        builder.Property(x => x.DeletedBy).HasMaxLength(10);
        builder.Property(x => x.DeleteReason).HasMaxLength(250);
        builder.Property(x => x.CreatedBy).HasMaxLength(10).IsRequired();
        builder.Property(x => x.ModifiedBy).HasMaxLength(10);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasOne(x => x.WorkOrder).WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.RouteStep).WithMany().HasForeignKey(x => x.RouteStepId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.WorkOrderOperation).WithMany().HasForeignKey(x => x.WorkOrderOperationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.DocumentNo })
            .IsUnique()
            .HasDatabaseName("UQ_PrProductionOutput_DocumentNo");
        builder.HasIndex(x => x.PostingRequestId)
            .IsUnique()
            .HasDatabaseName("UQ_PrProductionOutput_PostingRequest");
        builder.HasIndex(x => new { x.WorkOrderId, x.ProductionDate })
            .HasDatabaseName("IX_PrProductionOutput_WorkOrder_Date");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.ProductionDate, x.Status })
            .HasDatabaseName("IX_PrProductionOutput_Tenant_Date_Status");
        builder.HasIndex(x => new { x.Uid, x.CompanyCode, x.BranchCode })
            .IsUnique()
            .HasDatabaseName("UQ_PrProductionOutput_Uid_Tenant");
    }
}
