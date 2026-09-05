using ErpWeb.Model.Entities.CustomerProfile;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public class SaCustContactConfiguration : IEntityTypeConfiguration<SaCustContact>
{
    public void Configure(EntityTypeBuilder<SaCustContact> builder)
    {
        builder.ToTable("SaCustContact");
        builder.HasKey(e => new { e.CompanyCode, e.CustCode, e.Line });

        builder.Property(e => e.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.CustCode).HasMaxLength(30).IsRequired();
        builder.Property(e => e.ContactPerson).HasMaxLength(100);
        builder.Property(e => e.Title).HasMaxLength(50);
        builder.Property(e => e.Department).HasMaxLength(50);
        builder.Property(e => e.ContactEmail).HasMaxLength(100);
        builder.Property(e => e.ContactTelp).HasMaxLength(50);
        builder.Property(e => e.ContactFax).HasMaxLength(50);
    }
}
