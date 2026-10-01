using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Planning;

public class PrMachineConfiguration : IEntityTypeConfiguration<PrMachine>
{
    public void Configure(EntityTypeBuilder<PrMachine> builder)
    {
        builder.ToTable("PrMachine");
        builder.HasKey(e => new { e.MachineCd, e.ProcessCd });

        builder.Property(e => e.MachineCd).HasColumnName("Machine_Cd").HasMaxLength(10).IsRequired();
        builder.Property(e => e.MachineDes).HasColumnName("Machine_Des").HasMaxLength(30);
        builder.Property(e => e.ProcessCd).HasColumnName("Process_Cd").HasMaxLength(10).IsRequired();
        builder.Property(e => e.Created);
        builder.Property(e => e.Updated);
        builder.Property(e => e.UserId).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.UpdatedUid).HasColumnName("UpdatedUID").HasMaxLength(10);
        builder.Property(e => e.CompCode).HasMaxLength(10);
        builder.Property(e => e.BranchCode).HasMaxLength(10);
        builder.Property(e => e.LocCode).HasMaxLength(10);
        builder.Property(e => e.ConversionTime);
        builder.Property(e => e.StartupTime);
        builder.Property(e => e.QueueTime);
        builder.Property(e => e.Active).IsRequired().HasDefaultValue(true);
        builder.Property(e => e.MachineType).HasMaxLength(30);
        builder.Property(e => e.SerialNo).HasMaxLength(50);
        builder.Property(e => e.HourlyCost).HasPrecision(19, 6).IsRequired().HasDefaultValue(0m);
    }
}
