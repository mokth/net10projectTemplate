using ErpWeb.Model.Entities.CustomerProfile;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public class SaCustConfiguration : IEntityTypeConfiguration<SaCust>
{
    public void Configure(EntityTypeBuilder<SaCust> builder)
    {
        builder.ToTable("SaCust");
        builder.HasKey(e => new { e.CompanyCode, e.CustCode });

        builder.Property(e => e.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.CustCode).HasMaxLength(30).IsRequired();
        builder.Property(e => e.CustName).HasMaxLength(200).IsRequired();
        builder.Property(e => e.CustShortName).HasMaxLength(100);
        builder.Property(e => e.CustType).HasMaxLength(20);
        builder.Property(e => e.InvoicePrefix).HasMaxLength(20);
        builder.Property(e => e.CustGroupCode).HasMaxLength(20);
        builder.Property(e => e.SalesmanCode).HasMaxLength(20);
        builder.Property(e => e.AreaCode).HasMaxLength(20);
        builder.Property(e => e.SubGroupCode).HasMaxLength(20);
        builder.Property(e => e.IndustryCode).HasMaxLength(20);
        builder.Property(e => e.ChannelCode).HasMaxLength(20);

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
        builder.Property(e => e.Telex).HasMaxLength(50);
        builder.Property(e => e.Email).HasMaxLength(100);
        builder.Property(e => e.Website).HasMaxLength(100);
        builder.Property(e => e.CjLmw).HasMaxLength(50);
        builder.Property(e => e.CustBrn).HasMaxLength(50);
        builder.Property(e => e.RegType).HasMaxLength(20);
        builder.Property(e => e.Remark).HasMaxLength(500);

        builder.Property(e => e.GstregNo).HasColumnName("GSTRegNo").HasMaxLength(50);
        builder.Property(e => e.PayCode).HasMaxLength(20);
        builder.Property(e => e.Currency).HasMaxLength(10);
        builder.Property(e => e.TaxGrCode).HasMaxLength(20);
        builder.Property(e => e.GroupDiscount).HasMaxLength(20);
        builder.Property(e => e.DiscountMethod).HasMaxLength(20);
        builder.Property(e => e.PriceMethod).HasMaxLength(50);
        builder.Property(e => e.AgingType).HasMaxLength(20);
        builder.Property(e => e.PaidUpCapital).HasPrecision(18, 2);
        builder.Property(e => e.GlCode).HasMaxLength(20);
        builder.Property(e => e.OpeningAmount).HasPrecision(18, 2);
        builder.Property(e => e.CreditTerm).HasMaxLength(50);
        builder.Property(e => e.CreditLimit).HasPrecision(18, 2);
        builder.Property(e => e.CustPriceCode).HasMaxLength(20);

        builder.Property(e => e.ContactPerson).HasMaxLength(100);
        builder.Property(e => e.Title).HasMaxLength(50);
        builder.Property(e => e.Department).HasMaxLength(50);
        builder.Property(e => e.ContactEmail).HasMaxLength(100);
        builder.Property(e => e.ContactTelp).HasMaxLength(50);
        builder.Property(e => e.ContactFax).HasMaxLength(50);

        builder.Property(e => e.IsActive).HasColumnName("Active").HasDefaultValue(true).ValueGeneratedNever();

        builder.Property(e => e.ShipName).HasMaxLength(100);
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

        builder.Property(e => e.InvName).HasMaxLength(100);
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

        builder.Property(e => e.BranchCode).HasMaxLength(5);
        builder.Property(e => e.LocationCode).HasMaxLength(10);

        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(10);
        builder.Property(e => e.RowVersion).IsRowVersion();

        builder.HasMany(e => e.Addresses)
            .WithOne(e => e.Customer)
            .HasForeignKey(e => new { e.CompanyCode, e.CustCode })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(e => e.Contacts)
            .WithOne(e => e.Customer)
            .HasForeignKey(e => new { e.CompanyCode, e.CustCode })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.CompanyCode, e.CustName })
            .HasDatabaseName("IX_SaCust_Company_CustName");

        builder.HasIndex(e => new { e.CompanyCode, e.IsActive })
            .HasDatabaseName("IX_SaCust_Company_Active");
    }
}
