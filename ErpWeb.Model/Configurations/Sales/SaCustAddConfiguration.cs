using ErpWeb.Model.Entities.CustomerProfile;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public class SaCustAddConfiguration : IEntityTypeConfiguration<SaCustAdd>
{
    public void Configure(EntityTypeBuilder<SaCustAdd> builder)
    {
        builder.ToTable("SaCustAdd");
        builder.HasKey(e => new { e.CompanyCode, e.CustCode, e.Line });

        builder.Property(e => e.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.CustCode).HasMaxLength(30).IsRequired();
        builder.Property(e => e.AddName).HasMaxLength(100);
        builder.Property(e => e.DeliverTo).HasMaxLength(100);
        builder.Property(e => e.Address1).HasMaxLength(100);
        builder.Property(e => e.Address2).HasMaxLength(100);
        builder.Property(e => e.Address3).HasMaxLength(100);
        builder.Property(e => e.Address4).HasMaxLength(100);
        builder.Property(e => e.City).HasMaxLength(50);
        builder.Property(e => e.State).HasMaxLength(50);
        builder.Property(e => e.PostalCode).HasMaxLength(20);
        builder.Property(e => e.Country).HasMaxLength(50);
        builder.Property(e => e.Tel).HasMaxLength(50);
        builder.Property(e => e.Fax).HasMaxLength(50);
    }
}
