using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public class IvCustPriceConfiguration : IEntityTypeConfiguration<IvCustPrice>
{
    public void Configure(EntityTypeBuilder<IvCustPrice> builder)
    {
        builder.ToTable("IvCustPrice");
        builder.HasKey(e => new { e.CompanyCode, e.CustPriceCode, e.ICode, e.UOM });

        builder.Property(e => e.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.CustPriceCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.ICode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.UOM).HasMaxLength(5).IsRequired();
        builder.Property(e => e.IDesc).HasMaxLength(200);
        builder.Property(e => e.CustPriceDesc).HasMaxLength(50);
        builder.Property(e => e.SellingPrice).HasPrecision(18, 4);
        builder.Property(e => e.SellPackSize).HasPrecision(18, 4);
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(10);
        builder.Property(e => e.BranchCode).HasMaxLength(5);
        builder.Property(e => e.LocationCode).HasMaxLength(10);
    }
}
