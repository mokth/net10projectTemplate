using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Purchase;

public class PoCjDetailConfiguration : IEntityTypeConfiguration<PoCjDetail>
{
    public void Configure(EntityTypeBuilder<PoCjDetail> builder)
    {
        builder.ToTable("POCJDetail");
        builder.HasKey(e => new { e.CompanyCode, e.BranchCode, e.CjNo, e.RelNo, e.Line });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.CjNo).HasColumnName("CJNo").HasMaxLength(30).IsRequired();
        builder.Property(e => e.ICode).HasColumnName("ICode").HasMaxLength(30);
        builder.Property(e => e.IDesc).HasColumnName("IDesc").HasMaxLength(200);
        builder.Property(e => e.CjdOrderQty).HasColumnName("CJDOrderQty").HasPrecision(18, 4);
        builder.Property(e => e.CjdBalQty).HasColumnName("CJDBalQty").HasPrecision(18, 4);
    }
}
