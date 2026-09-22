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

        // Audit columns follow the legacy naming shared by the rest of the sales masters.
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(20);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(20);
    }
}
