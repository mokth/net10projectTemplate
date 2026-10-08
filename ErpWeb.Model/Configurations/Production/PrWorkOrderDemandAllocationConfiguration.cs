using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Production;

public sealed class PrWorkOrderDemandAllocationConfiguration : IEntityTypeConfiguration<PrWorkOrderDemandAllocation>
{
    public void Configure(EntityTypeBuilder<PrWorkOrderDemandAllocation> builder)
    {
        builder.ToTable("PrWorkOrderDemandAllocation");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.WorkOrderId).HasColumnName("WorkOrderID").IsRequired();
        builder.Property(x => x.DeliveryRequestId).HasColumnName("DeliveryRequestID").IsRequired();
        builder.Property(x => x.AllocatedQty).HasPrecision(18, 4).IsRequired();
        builder.Property(x => x.IsActive).IsRequired();
        builder.Property(x => x.ReleasedBy).HasMaxLength(20);
        builder.Property(x => x.ReleaseReason).HasMaxLength(500);
        builder.Property(x => x.CreatedBy).HasMaxLength(20);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasOne(x => x.WorkOrder)
            .WithMany(x => x.DemandAllocations)
            .HasForeignKey(x => x.WorkOrderId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.DeliveryRequest)
            .WithMany(x => x.WorkOrderAllocations)
            .HasForeignKey(x => x.DeliveryRequestId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.WorkOrderId)
            .IsUnique()
            .HasDatabaseName("UQ_PrWorkOrderDemandAllocation_WorkOrder");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.DeliveryRequestId })
            .HasDatabaseName("IX_PrWorkOrderDemandAllocation_Request");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.WorkOrderId })
            .HasDatabaseName("IX_PrWorkOrderDemandAllocation_WorkOrder");
        builder.HasCheckConstraint("CK_PrWorkOrderDemandAllocation_AllocatedQty", "AllocatedQty > 0");
    }
}
