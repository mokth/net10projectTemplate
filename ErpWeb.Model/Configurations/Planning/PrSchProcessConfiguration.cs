using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Planning;

public class PrSchProcessConfiguration : IEntityTypeConfiguration<PrSchProcess>
{
    public void Configure(EntityTypeBuilder<PrSchProcess> builder)
    {
        builder.ToTable("PrSchProcess");
        builder.HasKey(e => new { e.ScheCode, e.RelNo, e.ProdCode, e.WcCode, e.WciCode, e.ProcessCode });

        builder.Property(e => e.ScheCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.RelNo).IsRequired();
        builder.Property(e => e.ProdCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.WcCode).HasColumnName("WCCode").HasMaxLength(20).IsRequired();
        builder.Property(e => e.WciCode).HasColumnName("WCICode").HasMaxLength(20).IsRequired();
        builder.Property(e => e.ProcessCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.SeqNo);
        builder.Property(e => e.SetupLostQty);
        builder.Property(e => e.OperationLostQty);
        builder.Property(e => e.FinalProcess);
        builder.Property(e => e.StartDate);
        builder.Property(e => e.EndDate);
        builder.Property(e => e.Completed);
        builder.Property(e => e.Remark).HasMaxLength(250);
    }
}
