using ErpWeb.Model.Entities.CustomerProfile;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public class SaDisCustConfiguration : IEntityTypeConfiguration<SaDisCust>
{
    public void Configure(EntityTypeBuilder<SaDisCust> builder)
    {
        builder.ToTable("SaDisCust");
        builder.HasKey(e => new { e.CompanyCode, e.GroupName, e.PayCode, e.CustCode });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.GroupName).HasMaxLength(40).IsRequired();
        builder.Property(e => e.PayCode).HasMaxLength(40).IsRequired();
        builder.Property(e => e.CustCode).HasMaxLength(30).IsRequired();
        builder.Property(e => e.CustName).HasMaxLength(200).IsRequired();
    }
}
