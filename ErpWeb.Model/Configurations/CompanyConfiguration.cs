using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> builder)
    {
        builder.ToTable("Company");
        builder.HasKey(e => e.CompanyId);

        builder.Property(e => e.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.CompanyName).HasMaxLength(100).IsRequired();
        builder.Property(e => e.LegalName).HasMaxLength(150);
        builder.Property(e => e.RegistrationNo).HasMaxLength(50);
        builder.Property(e => e.TaxNo).HasMaxLength(50);

        builder.Property(e => e.Phone).HasMaxLength(30);
        builder.Property(e => e.Fax).HasMaxLength(30);
        builder.Property(e => e.Email).HasMaxLength(100);
        builder.Property(e => e.Website).HasMaxLength(200);

        builder.Property(e => e.Address1).HasMaxLength(100);
        builder.Property(e => e.Address2).HasMaxLength(100);
        builder.Property(e => e.Address3).HasMaxLength(100);
        builder.Property(e => e.City).HasMaxLength(50);
        builder.Property(e => e.State).HasMaxLength(50);
        builder.Property(e => e.PostCode).HasMaxLength(20);
        builder.Property(e => e.Country).HasMaxLength(50);

        builder.Property(e => e.LogoUrl).HasMaxLength(500);
        builder.Property(e => e.CurrencyCode).HasMaxLength(3);
        builder.Property(e => e.TimeZoneId).HasMaxLength(64);

        builder.Property(e => e.CreatedBy).HasMaxLength(10);
        builder.Property(e => e.ModifiedBy).HasMaxLength(10);
        builder.Property(e => e.IsActive).HasDefaultValue(true);

        builder.HasIndex(e => e.CompanyCode)
            .IsUnique()
            .HasDatabaseName("UQ_Company_CompanyCode");

        builder.HasIndex(e => e.IsActive)
            .HasDatabaseName("IX_Company_IsActive");
    }
}
