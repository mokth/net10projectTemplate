using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public class SaTaxGroupConfiguration : IEntityTypeConfiguration<SaTaxGroup>
{
    public void Configure(EntityTypeBuilder<SaTaxGroup> builder)
    {
        builder.ToTable("SaTaxGroup");
        builder.HasKey(e => new { e.CompanyCode, e.TaxGrCode });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.TaxGrCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.TaxGrDesc).HasMaxLength(100);
        builder.Property(e => e.Percentage).HasPrecision(18, 6);
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(20);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(20);
        builder.Property(e => e.BranchCode).HasMaxLength(10);
        builder.Property(e => e.LocationCode).HasMaxLength(20);
    }
}
