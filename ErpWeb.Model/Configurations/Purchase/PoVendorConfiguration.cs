using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Purchase;

public class PoVendorConfiguration : IEntityTypeConfiguration<PoVendor>
{
    public void Configure(EntityTypeBuilder<PoVendor> builder)
    {
        builder.ToTable("POVendor");
        builder.HasKey(e => new { e.CompanyCode, e.CustCode });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.CustCode).HasMaxLength(60).IsRequired();
        builder.Property(e => e.CustName).HasMaxLength(200).IsRequired();
        builder.Property(e => e.CustShortName).HasMaxLength(100);
        builder.Property(e => e.CustType).HasMaxLength(20);
        builder.Property(e => e.Address1).HasMaxLength(100);
        builder.Property(e => e.Address2).HasMaxLength(100);
        builder.Property(e => e.Address3).HasMaxLength(100);
        builder.Property(e => e.City).HasMaxLength(50);
        builder.Property(e => e.State).HasMaxLength(50);
        builder.Property(e => e.PostalCode).HasMaxLength(20);
        builder.Property(e => e.Country).HasMaxLength(50);
        builder.Property(e => e.Tel).HasMaxLength(50);
        builder.Property(e => e.Fax).HasMaxLength(50);
        builder.Property(e => e.Telex).HasMaxLength(50);
        builder.Property(e => e.Email).HasMaxLength(100);
        builder.Property(e => e.Website).HasMaxLength(100);
        builder.Property(e => e.GstregNo).HasColumnName("GSTRegNo").HasMaxLength(50);
        builder.Property(e => e.PayCode).HasMaxLength(20);
        builder.Property(e => e.Currency).HasMaxLength(20);
        builder.Property(e => e.ContactPerson).HasMaxLength(100);
        builder.Property(e => e.Title).HasMaxLength(50);
        builder.Property(e => e.Department).HasMaxLength(50);
        builder.Property(e => e.ContactEmail).HasMaxLength(100);
        builder.Property(e => e.ContactTelp).HasMaxLength(50);
        builder.Property(e => e.ContactFax).HasMaxLength(50);
        builder.Property(e => e.TaxGrCode).HasMaxLength(20);
        builder.Property(e => e.IsActive).HasColumnName("Active").HasDefaultValue(true).ValueGeneratedNever();

        builder.Property(e => e.ShipAddress1).HasMaxLength(100);
        builder.Property(e => e.ShipAddress2).HasMaxLength(100);
        builder.Property(e => e.ShipAddress3).HasMaxLength(100);
        builder.Property(e => e.ShipCity).HasMaxLength(50);
        builder.Property(e => e.ShipState).HasMaxLength(50);
        builder.Property(e => e.ShipPostalCode).HasMaxLength(20);
        builder.Property(e => e.ShipCountry).HasMaxLength(50);
        builder.Property(e => e.ShipTel).HasMaxLength(50);
        builder.Property(e => e.ShipFax).HasMaxLength(50);
        builder.Property(e => e.ShipTelex).HasMaxLength(50);
        builder.Property(e => e.ShipEmail).HasMaxLength(100);
        builder.Property(e => e.ShipWebsite).HasMaxLength(100);

        builder.Property(e => e.InvAddress1).HasMaxLength(100);
        builder.Property(e => e.InvAddress2).HasMaxLength(100);
        builder.Property(e => e.InvAddress3).HasMaxLength(100);
        builder.Property(e => e.InvCity).HasMaxLength(50);
        builder.Property(e => e.InvState).HasMaxLength(50);
        builder.Property(e => e.InvPostalCode).HasMaxLength(20);
        builder.Property(e => e.InvCountry).HasMaxLength(50);
        builder.Property(e => e.InvTel).HasMaxLength(50);
        builder.Property(e => e.InvFax).HasMaxLength(50);
        builder.Property(e => e.InvTelex).HasMaxLength(50);
        builder.Property(e => e.InvEmail).HasMaxLength(100);
        builder.Property(e => e.InvWebsite).HasMaxLength(100);

        builder.Property(e => e.BuyingTerm).HasMaxLength(20);
        builder.Property(e => e.BranchCode).HasMaxLength(10);
        builder.Property(e => e.LocationCode).HasMaxLength(10);

        builder.Property(e => e.CreatedDate).HasColumnName("Created").HasColumnType("datetime2");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(20);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated").HasColumnType("datetime2");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(20);
        builder.Property(e => e.RowVersion).IsRowVersion();

        builder.HasIndex(e => new { e.CompanyCode, e.CustName })
            .HasDatabaseName("IX_POVendor_Company_CustName");

        builder.HasIndex(e => new { e.CompanyCode, e.IsActive })
            .HasDatabaseName("IX_POVendor_Company_Active");
    }
}
