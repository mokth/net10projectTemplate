using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Planning;

public class PrSchBomConfiguration : IEntityTypeConfiguration<PrSchBom>
{
    public void Configure(EntityTypeBuilder<PrSchBom> builder)
    {
        builder.ToTable("PrSchBOM");
        builder.HasKey(e => new { e.ScheCode, e.RelNo, e.ProdCode, e.WcCode, e.WciCode, e.ProcessCode, e.ICode });

        builder.Property(e => e.ScheCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.RelNo).IsRequired();
        builder.Property(e => e.ProdCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.WcCode).HasColumnName("WCCode").HasMaxLength(20).IsRequired();
        builder.Property(e => e.WciCode).HasColumnName("WCICode").HasMaxLength(20).IsRequired();
        builder.Property(e => e.ProcessCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.ICode).HasColumnName("ICode").HasMaxLength(20).IsRequired();
        builder.Property(e => e.IName).HasColumnName("IName").HasMaxLength(200);
        builder.Property(e => e.StdQty);
        builder.Property(e => e.StdUom).HasColumnName("StdUOM").HasMaxLength(5);
        builder.Property(e => e.Warehouse).HasMaxLength(10);
        builder.Property(e => e.BomDefault);
        builder.Property(e => e.WipBomDefault).HasColumnName("WIPBomDefault");
        builder.Property(e => e.Tolerance).HasColumnName("tolerance");
    }
}
