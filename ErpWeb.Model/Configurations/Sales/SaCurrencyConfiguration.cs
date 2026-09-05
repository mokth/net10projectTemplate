using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public class SaCurrencyConfiguration : IEntityTypeConfiguration<SaCurrency>
{
    public void Configure(EntityTypeBuilder<SaCurrency> builder)
    {
        builder.ToTable("SaCurrency");
        builder.HasKey(e => new { e.CompanyCode, e.CurrCode });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.CurrCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.CurrDesc).HasMaxLength(100);
        builder.Property(e => e.IsActive).HasColumnName("Active");
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(20);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(20);
        builder.Property(e => e.BranchCode).HasMaxLength(10);
        builder.Property(e => e.LocationCode).HasMaxLength(20);
    }
}
