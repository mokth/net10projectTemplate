using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Production;

public sealed class ProductionAuditEventConfiguration : IEntityTypeConfiguration<ProductionAuditEvent>
{
    public void Configure(EntityTypeBuilder<ProductionAuditEvent> builder)
    {
        builder.ToTable("PrWorkOrderAudit");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.WorkOrderId).HasColumnName("WorkOrderID");
        builder.Property(x => x.EventType).HasMaxLength(40).IsRequired();
        builder.Property(x => x.FromStatus).HasMaxLength(20);
        builder.Property(x => x.ToStatus).HasMaxLength(20);
        builder.Property(x => x.Reason).HasMaxLength(500);
        builder.Property(x => x.ActorUserId).HasMaxLength(10).IsRequired();
        builder.HasIndex(x => new { x.WorkOrderId, x.OccurredDate })
            .HasDatabaseName("IX_PrWorkOrderAudit_Order_Date");
    }
}

