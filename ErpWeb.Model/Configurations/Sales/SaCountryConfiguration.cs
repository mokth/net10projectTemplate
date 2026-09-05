using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public class SaCountryConfiguration : IEntityTypeConfiguration<SaCountry>
{
    public void Configure(EntityTypeBuilder<SaCountry> builder)
    {
        builder.ToTable("SaCountry");
        builder.HasKey(e => e.CountryCode);

        builder.Property(e => e.CountryCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.CountryName).HasMaxLength(100);
        builder.Property(e => e.Latitude).HasColumnType("decimal(9,6)");
        builder.Property(e => e.Longitude).HasColumnType("decimal(9,6)");
    }
}
