using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Planning;

public class PrDefMachineConfiguration : IEntityTypeConfiguration<PrDefMachine>
{
    public void Configure(EntityTypeBuilder<PrDefMachine> builder)
    {
        builder.ToTable("PrDefMachine");
        builder.HasKey(e => new { e.ProdCode, e.WcCode, e.WciCode, e.ProcessCode, e.MachineCode });

        builder.Property(e => e.ProdCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.WcCode).HasColumnName("WCCode").HasMaxLength(20).IsRequired();
        builder.Property(e => e.WciCode).HasColumnName("WCICode").HasMaxLength(20).IsRequired();
        builder.Property(e => e.ProcessCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.MachineCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.MachineName).HasMaxLength(30);
        builder.Property(e => e.CycleTime).IsRequired();
        builder.Property(e => e.ConversionTime);
        builder.Property(e => e.StartupTime);
        builder.Property(e => e.QueueTime);
        builder.Property(e => e.SeqNo);
        builder.Property(e => e.MacDefault);
        builder.Property(e => e.CompCode).HasMaxLength(10);
        builder.Property(e => e.BranchCode).HasMaxLength(10);
        builder.Property(e => e.LocCode).HasMaxLength(10);
        builder.Property(e => e.CycleTimeCal);
        builder.Property(e => e.CycleTimeInvd);
        builder.Property(e => e.NoMachine);
    }
}
