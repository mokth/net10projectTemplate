using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Planning;

public class PrDefProcessConfiguration : IEntityTypeConfiguration<PrDefProcess>
{
    public void Configure(EntityTypeBuilder<PrDefProcess> builder)
    {
        builder.ToTable("PrDefProcess");
        builder.HasKey(e => new { e.ProdCode, e.WcCode, e.WciCode, e.ProcessCode });

        builder.Property(e => e.ProdCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.WcCode).HasColumnName("WCCode").HasMaxLength(20).IsRequired();
        builder.Property(e => e.WciCode).HasColumnName("WCICode").HasMaxLength(20).IsRequired();
        builder.Property(e => e.ProcessCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.SeqNo);
        builder.Property(e => e.SetupLostQty);
        builder.Property(e => e.OperationLostQty);
        builder.Property(e => e.FinalProcess);
        builder.Property(e => e.SCode).HasColumnName("SCode").HasMaxLength(20);
        builder.Property(e => e.CompCode).HasMaxLength(10);
        builder.Property(e => e.BranchCode).HasMaxLength(10);
        builder.Property(e => e.LocCode).HasMaxLength(10);
        builder.Property(e => e.Remark).HasMaxLength(250);
    }
}
