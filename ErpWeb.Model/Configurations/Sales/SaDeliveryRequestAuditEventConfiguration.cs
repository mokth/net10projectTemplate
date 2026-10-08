using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public sealed class SaDeliveryRequestAuditEventConfiguration : IEntityTypeConfiguration<SaDeliveryRequestAuditEvent>
{
    public void Configure(EntityTypeBuilder<SaDeliveryRequestAuditEvent> builder)
    {
        builder.ToTable("SaDeliveryRequestAudit");
        builder.HasKey(x => x.Uid);
        builder.Property(x => x.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(x => x.DeliveryRequestId).HasColumnName("DeliveryRequestID").IsRequired();
        builder.Property(x => x.EventType).HasMaxLength(40).IsRequired();
        builder.Property(x => x.SourceId).HasColumnName("SourceID");
        builder.Property(x => x.WorkOrderId).HasColumnName("WorkOrderID");
        builder.Property(x => x.DetailsJson);
        builder.Property(x => x.Reason).HasMaxLength(500);
        builder.Property(x => x.OccurredDate).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.ActorUserId).HasMaxLength(20).IsRequired();
        builder.HasIndex(x => new { x.DeliveryRequestId, x.OccurredDate })
            .HasDatabaseName("IX_SaDeliveryRequestAudit_Request_Date");
        builder.HasOne(x => x.DeliveryRequest)
            .WithMany(x => x.AuditEvents)
            .HasForeignKey(x => x.DeliveryRequestId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
