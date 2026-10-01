using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Production;

public sealed class WipItemBalLocConfiguration : IEntityTypeConfiguration<WipItemBalLoc>
{
    public void Configure(EntityTypeBuilder<WipItemBalLoc> builder)
    {
        builder.ToTable("WIPItemBalLoc");
        builder.HasKey(e => new
        {
            e.ICode,
            e.WcCode,
            e.ProcessCode,
            e.LotNo,
            e.RevNo,
            e.ScheCode,
            e.RelNo,
            e.StdUom
        });

        builder.Property(e => e.ICode).HasColumnName("ICode").HasMaxLength(20).IsRequired();
        builder.Property(e => e.WcCode).HasColumnName("WCCode").HasMaxLength(20).IsRequired();
        builder.Property(e => e.ProcessCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.LotNo).HasMaxLength(20).IsRequired();
        builder.Property(e => e.RevNo).IsRequired();
        builder.Property(e => e.ProcessSeq);
        builder.Property(e => e.StdQty);
        builder.Property(e => e.StdUom).HasColumnName("StdUom").HasMaxLength(5).IsRequired();
        builder.Property(e => e.WtQty);
        builder.Property(e => e.WtUom).HasColumnName("WtUom").HasMaxLength(5);
        builder.Property(e => e.Created);
        builder.Property(e => e.Updated);
        builder.Property(e => e.UserId).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.ScheCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.RelNo).IsRequired();
        builder.Property(e => e.WcICode).HasColumnName("WCICode").HasMaxLength(20);
        builder.Property(e => e.Remark).HasMaxLength(20);
        builder.Property(e => e.ProdCode).HasMaxLength(20);
        builder.Property(e => e.TransactionDate);
        builder.Property(e => e.TrxType).HasMaxLength(5);
        builder.Property(e => e.UnitPrice).HasPrecision(18, 8);
        builder.Property(e => e.RowVersion).IsRowVersion();
    }
}
