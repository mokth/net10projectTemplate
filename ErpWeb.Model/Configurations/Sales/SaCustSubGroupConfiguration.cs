using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ErpWeb.Model.Entities.Sales;

namespace ErpWeb.Model.Configurations.Sales;

public class SaCustSubGroupConfiguration : IEntityTypeConfiguration<SaCustSubGroup>
{
    public void Configure(EntityTypeBuilder<SaCustSubGroup> builder)
    {
        builder.ToTable("SaCustSubGroup");
        builder.HasKey(e => new { e.CompanyCode, e.CustSubGroupCode });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.CustSubGroupCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.CustSubGroupDesc).HasMaxLength(100);
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(20);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(20);
        builder.Property(e => e.BranchCode).HasMaxLength(10);
        builder.Property(e => e.LocationCode).HasMaxLength(20);
        builder.Property(e => e.RowVersion).IsRowVersion();
    }
}
