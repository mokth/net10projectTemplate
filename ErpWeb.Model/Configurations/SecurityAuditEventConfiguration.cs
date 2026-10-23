using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class SecurityAuditEventConfiguration : IEntityTypeConfiguration<SecurityAuditEvent>
{
    public void Configure(EntityTypeBuilder<SecurityAuditEvent> builder)
    {
        builder.ToTable("SecurityAuditEvent");
        builder.HasKey(e => e.SecurityAuditEventId);

        builder.Property(e => e.CompanyCode).HasMaxLength(5);
        builder.Property(e => e.ActorUserId).HasMaxLength(10);
        builder.Property(e => e.EventType).HasMaxLength(50).IsRequired();
        builder.Property(e => e.Outcome).HasMaxLength(20).IsRequired();
        builder.Property(e => e.OldValue).HasMaxLength(200);
        builder.Property(e => e.NewValue).HasMaxLength(200);
        builder.Property(e => e.Details).HasMaxLength(500);
    }
}
