using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public class SaSalesRepTargetConfiguration : IEntityTypeConfiguration<SaSalesRepTarget>
{
    public void Configure(EntityTypeBuilder<SaSalesRepTarget> builder)
    {
        builder.ToTable("SaSalesRepTarget");

        // Company-wide by design: no BranchCode part in the key.
        builder.HasKey(e => new { e.CompanyCode, e.SrepCode, e.Year, e.Month });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.SrepCode).HasColumnName("SRepCode").HasMaxLength(20).IsRequired();
        builder.Property(e => e.Year).IsRequired();
        builder.Property(e => e.Month).IsRequired();
        builder.Property(e => e.TargetAmount).HasPrecision(18, 2);

        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(20);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(20);
    }
}
