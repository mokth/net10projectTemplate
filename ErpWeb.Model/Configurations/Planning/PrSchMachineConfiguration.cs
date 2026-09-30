using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Planning;

public class PrSchMachineConfiguration : IEntityTypeConfiguration<PrSchMachine>
{
    public void Configure(EntityTypeBuilder<PrSchMachine> builder)
    {
        builder.ToTable("PrSchMachine");
        builder.HasKey(e => new { e.ScheCode, e.RelNo, e.ProdCode, e.WcCode, e.WciCode, e.ProcessCode, e.MachineCode });

        builder.Property(e => e.ScheCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.RelNo).IsRequired();
        builder.Property(e => e.ProdCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.WcCode).HasColumnName("WCCode").HasMaxLength(20).IsRequired();
        builder.Property(e => e.WciCode).HasColumnName("WCICode").HasMaxLength(20).IsRequired();
        builder.Property(e => e.ProcessCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.MachineCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.MachineName).HasMaxLength(30);
        builder.Property(e => e.CycleTime);
        builder.Property(e => e.ConversionTime);
        builder.Property(e => e.StartupTime);
        builder.Property(e => e.QueueTime);
        builder.Property(e => e.SeqNo);
        builder.Property(e => e.MacDefault);
        builder.Property(e => e.StartDate);
        builder.Property(e => e.CompleteDate);
    }
}
