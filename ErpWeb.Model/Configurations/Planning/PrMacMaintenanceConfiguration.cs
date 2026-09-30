using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Planning;

public class PrMacMaintenanceConfiguration : IEntityTypeConfiguration<PrMacMaintenance>
{
    public void Configure(EntityTypeBuilder<PrMacMaintenance> builder)
    {
        builder.ToTable("PrMacMaintenance");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("ID").ValueGeneratedOnAdd();
        builder.Property(e => e.TrxDate);
        builder.Property(e => e.MacCode).HasMaxLength(30);
        builder.Property(e => e.Description);
        builder.Property(e => e.ActionTaken);
        builder.Property(e => e.Status).HasMaxLength(20);
        builder.Property(e => e.ReportBy).HasMaxLength(20);
        builder.Property(e => e.ActionBy).HasMaxLength(20);
        builder.Property(e => e.ActionOn);
        builder.Property(e => e.RefCode).HasMaxLength(25);
        builder.Property(e => e.RepType).HasMaxLength(25);
        builder.Property(e => e.MType).HasColumnName("MType").HasMaxLength(10);
        builder.Property(e => e.Name).HasMaxLength(100);
        builder.Property(e => e.Reminder).HasMaxLength(10);
    }
}
