using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Planning;

public class PrMaintenanceReasonConfiguration : IEntityTypeConfiguration<PrMaintenanceReason>
{
    public void Configure(EntityTypeBuilder<PrMaintenanceReason> builder)
    {
        builder.ToTable("PrMaintenanceReason");
        builder.HasKey(e => new { e.CompCode, e.ReasonCd });

        builder.Property(e => e.ReasonCd).HasMaxLength(10).IsRequired();
        builder.Property(e => e.Description).HasMaxLength(100).IsRequired();
        builder.Property(e => e.ReasonType).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Active).IsRequired().HasDefaultValue(true);
        builder.Property(e => e.CompCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(10);
        builder.Property(e => e.LocCode).HasMaxLength(10);
        builder.Property(e => e.Created);
        builder.Property(e => e.Updated);
        builder.Property(e => e.UserId).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.UpdatedUid).HasColumnName("UpdatedUID").HasMaxLength(10);
    }
}
