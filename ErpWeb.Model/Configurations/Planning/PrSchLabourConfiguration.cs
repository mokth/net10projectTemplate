using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Planning;

public class PrSchLabourConfiguration : IEntityTypeConfiguration<PrSchLabour>
{
    public void Configure(EntityTypeBuilder<PrSchLabour> builder)
    {
        builder.ToTable("PrSchLabour");
        builder.HasKey(e => new
        {
            e.ScheCode,
            e.RelNo,
            e.ProdCode,
            e.WcCode,
            e.WciCode,
            e.ProcessCode,
            e.MachineCode,
            e.LabourCode
        });

        builder.Property(e => e.ScheCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.RelNo).IsRequired();
        builder.Property(e => e.ProdCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.WcCode).HasColumnName("WCCode").HasMaxLength(20).IsRequired();
        builder.Property(e => e.WciCode).HasColumnName("WCICode").HasMaxLength(20).IsRequired();
        builder.Property(e => e.ProcessCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.MachineCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.LabourCode).HasMaxLength(50).IsRequired();
        builder.Property(e => e.LabourCost);
    }
}
