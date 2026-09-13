using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Purchase;

public class PoSupplierConfiguration : IEntityTypeConfiguration<PoSupplier>
{
    public void Configure(EntityTypeBuilder<PoSupplier> builder)
    {
        builder.ToTable("POSupplier");
        builder.HasKey(e => new { e.CompanyCode, e.BranchCode, e.SuppCode });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.SuppCode).HasMaxLength(60).IsRequired();
        builder.Property(e => e.SuppName).HasMaxLength(200).IsRequired();
        builder.Property(e => e.SuppShortName).HasMaxLength(100);
        builder.Property(e => e.SuppType).HasMaxLength(20);
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
        builder.Property(e => e.GstregNo).HasColumnName("GSTRegNo").HasMaxLength(50);
        builder.Property(e => e.PayCode).HasMaxLength(20);
        builder.Property(e => e.Currency).HasMaxLength(20);
        builder.Property(e => e.GlCode).HasMaxLength(20);
        builder.Property(e => e.ContactPerson).HasMaxLength(100);
        builder.Property(e => e.Title).HasMaxLength(50);
        builder.Property(e => e.Department).HasMaxLength(50);
        builder.Property(e => e.ContactEmail).HasMaxLength(100);
        builder.Property(e => e.ContactTelp).HasMaxLength(50);
        builder.Property(e => e.ContactFax).HasMaxLength(50);
        builder.Property(e => e.TaxGrCode).HasMaxLength(20);
        builder.Property(e => e.IsActive).HasColumnName("Active").HasDefaultValue(true).ValueGeneratedNever();
        builder.Property(e => e.CategoryCode).HasMaxLength(20);

        builder.Property(e => e.ContactPerson2).HasMaxLength(100);
        builder.Property(e => e.Title2).HasMaxLength(50);
        builder.Property(e => e.Department2).HasMaxLength(50);
        builder.Property(e => e.ContactEmail2).HasMaxLength(100);
        builder.Property(e => e.ContactTelp2).HasMaxLength(50);
        builder.Property(e => e.ContactFax2).HasMaxLength(50);
        builder.Property(e => e.ContactPerson3).HasMaxLength(100);
        builder.Property(e => e.Title3).HasMaxLength(50);
        builder.Property(e => e.Department3).HasMaxLength(50);
        builder.Property(e => e.ContactEmail3).HasMaxLength(100);
        builder.Property(e => e.ContactTelp3).HasMaxLength(50);
        builder.Property(e => e.ContactFax3).HasMaxLength(50);
        builder.Property(e => e.ContactPerson4).HasMaxLength(100);
        builder.Property(e => e.Title4).HasMaxLength(50);
        builder.Property(e => e.Department4).HasMaxLength(50);
        builder.Property(e => e.ContactEmail4).HasMaxLength(100);
        builder.Property(e => e.ContactTelp4).HasMaxLength(50);
        builder.Property(e => e.ContactFax4).HasMaxLength(50);

        builder.Property(e => e.PoPrefix).HasColumnName("POPrefix").HasMaxLength(20);
        builder.Property(e => e.TaxGroup).HasMaxLength(20);
        builder.Property(e => e.BuyingTerm).HasMaxLength(20);
        builder.Property(e => e.SupplierBrn).HasColumnName("SupplierBRN").HasMaxLength(50);
        builder.Property(e => e.BankName).HasMaxLength(100);
        builder.Property(e => e.AccountNo).HasMaxLength(50);
        builder.Property(e => e.Remark).HasMaxLength(500);
        builder.Property(e => e.Lmw).HasColumnName("LMW");
        builder.Property(e => e.CreditorGroup).HasMaxLength(20);
        builder.Property(e => e.CreditorSubGroup).HasMaxLength(20);
        builder.Property(e => e.CreditLimit).HasPrecision(18, 2);
        builder.Property(e => e.AreaCode).HasMaxLength(20);
        builder.Property(e => e.AgingType).HasMaxLength(20);
        builder.Property(e => e.StatementType).HasMaxLength(20);
        builder.Property(e => e.TinNo).HasColumnName("TINNo").HasMaxLength(20);
        builder.Property(e => e.RegType).HasMaxLength(20);
        builder.Property(e => e.StateCode).HasMaxLength(20);
        builder.Property(e => e.CountryCode).HasMaxLength(20);
        builder.Property(e => e.MiscCode).HasColumnName("MISCCode").HasMaxLength(20);
        builder.Property(e => e.BizDesc).HasMaxLength(200);

        builder.Property(e => e.CreatedDate).HasColumnName("Created").HasColumnType("datetime2");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(20);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated").HasColumnType("datetime2");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(20);
        builder.Property(e => e.RowVersion).IsRowVersion();

        builder.HasMany(e => e.Addresses)
            .WithOne(e => e.Supplier)
            .HasForeignKey(e => new { e.CompanyCode, e.BranchCode, e.SuppCode })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.SuppName })
            .HasDatabaseName("IX_POSupplier_Company_Branch_SuppName");

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.IsActive })
            .HasDatabaseName("IX_POSupplier_Company_Branch_Active");
    }
}
