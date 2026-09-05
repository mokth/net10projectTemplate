using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public class SaInvoiceConfiguration : IEntityTypeConfiguration<SaInvoice>
{
    public void Configure(EntityTypeBuilder<SaInvoice> builder)
    {
        builder.ToTable("SaInvoice");
        builder.HasKey(e => new { e.CompanyCode, e.BranchCode, e.InvNo });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.InvNo).HasMaxLength(30).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.LocationCode).HasMaxLength(10);
        builder.Property(e => e.CustCode).HasMaxLength(60).IsRequired();
        builder.Property(e => e.InvDate).HasColumnType("datetime2").IsRequired();
        builder.Property(e => e.Status).HasMaxLength(20).IsRequired();
        builder.Property(e => e.DoNo).HasColumnName("DONo").HasMaxLength(30).IsRequired();
        builder.Property(e => e.Currency).HasMaxLength(20);
        builder.Property(e => e.CurrRate).HasPrecision(18, 6).IsRequired();
        builder.Property(e => e.GrossAmnt).HasPrecision(18, 2);
        builder.Property(e => e.Taxes).HasPrecision(18, 2);
        builder.Property(e => e.TotAmnt).HasPrecision(18, 2);
        builder.Property(e => e.InvPrefix).HasMaxLength(20);
        builder.Property(e => e.PayCode).HasMaxLength(20);
        builder.Property(e => e.TaxGrCode).HasMaxLength(20);
        builder.Property(e => e.SalesmanCode).HasMaxLength(20);
        builder.Property(e => e.PoNo).HasMaxLength(50);
        builder.Property(e => e.Remark).HasMaxLength(500);
        builder.Property(e => e.CustName).HasMaxLength(200);
        builder.Property(e => e.InvName).HasMaxLength(100);
        builder.Property(e => e.InvAddress1).HasMaxLength(100);
        builder.Property(e => e.InvAddress2).HasMaxLength(100);
        builder.Property(e => e.InvAddress3).HasMaxLength(100);
        builder.Property(e => e.InvAddress4).HasMaxLength(100);
        builder.Property(e => e.InvCity).HasMaxLength(50);
        builder.Property(e => e.InvState).HasMaxLength(50);
        builder.Property(e => e.InvPostalCode).HasMaxLength(20);
        builder.Property(e => e.InvCountry).HasMaxLength(50);
        builder.Property(e => e.InvTel).HasMaxLength(50);
        builder.Property(e => e.InvFax).HasMaxLength(50);
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
        builder.Property(e => e.PostedDate).HasColumnType("datetime");
        builder.Property(e => e.PostedBy).HasMaxLength(20);
        builder.Property(e => e.RollbackDate).HasColumnType("datetime");
        builder.Property(e => e.RollbackBy).HasMaxLength(20);
        builder.Property(e => e.CreatedDate).HasColumnName("Created").HasColumnType("datetime2");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(20);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated").HasColumnType("datetime2");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(20);
        builder.Property(e => e.RowVersion).IsRowVersion();

        builder.HasMany(e => e.Details)
            .WithOne(e => e.Invoice)
            .HasForeignKey(e => new { e.CompanyCode, e.BranchCode, e.InvNo })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.CompanyCode, e.Status })
            .HasDatabaseName("IX_SaInvoice_Company_Status");

        builder.HasIndex(e => new { e.CompanyCode, e.CustCode })
            .HasDatabaseName("IX_SaInvoice_Company_CustCode");
    }
}
