using ErpWeb.Model.Entities.CustomerProfile;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public class SaCustGroupConfiguration : IEntityTypeConfiguration<SaCustGroup>
{
    public void Configure(EntityTypeBuilder<SaCustGroup> builder)
    {
        builder.ToTable("SaCustGroup");
        builder.HasKey(e => new { e.CompanyCode, e.CustGroupCode });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.CustGroupCode).HasMaxLength(40).IsRequired();
        builder.Property(e => e.CustGroupDesc).HasMaxLength(200);
        builder.Property(e => e.BranchCode).HasMaxLength(10);
        builder.Property(e => e.LocationCode).HasMaxLength(20);
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(20);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(20);
        builder.Property(e => e.RowVersion).IsRowVersion();
    }
}
