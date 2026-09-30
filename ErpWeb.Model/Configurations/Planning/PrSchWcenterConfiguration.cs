using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Planning;

public class PrSchWcenterConfiguration : IEntityTypeConfiguration<PrSchWcenter>
{
    public void Configure(EntityTypeBuilder<PrSchWcenter> builder)
    {
        builder.ToTable("PrSchWCenter");
        builder.HasKey(e => new { e.ScheCode, e.RelNo, e.ProdCode, e.WcCode, e.ICode });

        builder.Property(e => e.ScheCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.RelNo).IsRequired();
        builder.Property(e => e.ProdCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.WcCode).HasColumnName("WCCode").HasMaxLength(20).IsRequired();
        builder.Property(e => e.ICode).HasColumnName("ICode").HasMaxLength(20).IsRequired();
        builder.Property(e => e.IDesc).HasColumnName("IDesc").HasMaxLength(200);
        builder.Property(e => e.Class).HasMaxLength(10);
        builder.Property(e => e.SeqNo);
        builder.Property(e => e.StdPackSize);
        builder.Property(e => e.StdUom).HasColumnName("StdUOM").HasMaxLength(5);
        builder.Property(e => e.ScheQty);
        builder.Property(e => e.StartDate);
        builder.Property(e => e.CompleteDate);
        builder.Property(e => e.Completed);
    }
}
