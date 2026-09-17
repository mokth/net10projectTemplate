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

        // Sales pricing method token; width mirrors SaCompanyPriceMethod.MaxLength in Core (the
        // model layer must not reference Core, so the literal is intentional and must stay in sync).
        builder.Property(e => e.SalesPriceMethod).HasMaxLength(32);

        builder.Property(e => e.CreatedBy).HasMaxLength(10);
        builder.Property(e => e.ModifiedBy).HasMaxLength(10);
        builder.Property(e => e.IsActive).HasDefaultValue(true);

        // LHDN e-Invoice supplier profile. Non-secret only: the client id/secret and certificate
        // password never live in the database (see EInvoiceServiceCollectionExtensions).
        builder.Property(e => e.EInvMsicCode).HasColumnName("EInvMSICCode").HasMaxLength(20);
        builder.Property(e => e.EInvBizDescription).HasMaxLength(300);
        builder.Property(e => e.EInvSstNo).HasMaxLength(50);
        builder.Property(e => e.EInvRegType).HasMaxLength(20);
        builder.Property(e => e.EInvStateCode).HasMaxLength(10);
        builder.Property(e => e.EInvCountryCode).HasMaxLength(10);
        builder.Property(e => e.EInvOnBehalfTin).HasMaxLength(50);
        builder.Property(e => e.EInvDocumentVersion).HasMaxLength(10);

        builder.HasIndex(e => e.CompanyCode)
            .IsUnique()
            .HasDatabaseName("UQ_Company_CompanyCode");

        builder.HasIndex(e => e.IsActive)
            .HasDatabaseName("IX_Company_IsActive");
    }
}
