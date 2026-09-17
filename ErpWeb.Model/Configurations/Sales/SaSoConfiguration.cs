using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public class SaSoConfiguration : IEntityTypeConfiguration<SaSo>
{
    public void Configure(EntityTypeBuilder<SaSo> builder)
    {
        builder.ToTable("SaSO");
        builder.HasKey(e => new { e.CompanyCode, e.BranchCode, e.SoNo, e.CustRel });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.SoNo).HasColumnName("SONo").HasMaxLength(30).IsRequired();
        builder.Property(e => e.CustRel).IsRequired();
        builder.Property(e => e.IsCurrent).IsRequired();
        builder.Property(e => e.LastCustRel).IsRequired();
        builder.Property(e => e.RevisionReason).HasMaxLength(200);
        builder.Property(e => e.SoDate).HasColumnName("SODate").HasColumnType("datetime2").IsRequired();
        builder.Property(e => e.Status).HasMaxLength(20).IsRequired();
        builder.Property(e => e.FulfillmentStatus).HasMaxLength(10).IsRequired();
        builder.Property(e => e.BillingStatus).HasMaxLength(10).IsRequired();
        builder.Property(e => e.ClosedReason).HasMaxLength(20);
        builder.Property(e => e.ClosedDate).HasColumnType("datetime2");
        builder.Property(e => e.ClosedBy).HasMaxLength(20);
        builder.Property(e => e.CustCode).HasMaxLength(60).IsRequired();
        builder.Property(e => e.CustName).HasMaxLength(200);
        builder.Property(e => e.CustPo).HasColumnName("CustPO").HasMaxLength(50);
        builder.Property(e => e.ShipName).HasMaxLength(100);
        builder.Property(e => e.ShipAddress1).HasMaxLength(100);
        builder.Property(e => e.ShipAddress2).HasMaxLength(100);
        builder.Property(e => e.ShipAddress3).HasMaxLength(100);
        builder.Property(e => e.ShipAddress4).HasMaxLength(100);
        builder.Property(e => e.ShipCity).HasMaxLength(50);
        builder.Property(e => e.ShipState).HasMaxLength(50);
        builder.Property(e => e.ShipPostalCode).HasMaxLength(20);
        builder.Property(e => e.ShipCountry).HasMaxLength(50);
        builder.Property(e => e.ShipTel).HasMaxLength(50);
        builder.Property(e => e.ShipFax).HasMaxLength(50);
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
        builder.Property(e => e.TaxGrCode).HasMaxLength(20);
        builder.Property(e => e.Currency).HasMaxLength(20);
        builder.Property(e => e.CurrRate).HasPrecision(18, 6).IsRequired();
        builder.Property(e => e.PayCode).HasMaxLength(20);
        builder.Property(e => e.Remarks).HasMaxLength(500);
        builder.Property(e => e.GrossAmnt).HasPrecision(18, 2);
        builder.Property(e => e.Taxes).HasPrecision(18, 2);
        builder.Property(e => e.TotAmnt).HasPrecision(18, 2);
        builder.Property(e => e.Prefix).HasMaxLength(20);
        builder.Property(e => e.LocationCode).HasMaxLength(10);
        builder.Property(e => e.CustDiscount).HasPrecision(18, 6);
        builder.Property(e => e.ContactPerson).HasMaxLength(100);
        builder.Property(e => e.ProjId).HasColumnName("ProjID").HasMaxLength(20);
        builder.Property(e => e.SalesRep).HasMaxLength(20);
        builder.Property(e => e.Ref1).HasMaxLength(50);
        builder.Property(e => e.Ref2).HasMaxLength(50);
        builder.Property(e => e.Ref3).HasMaxLength(50);
        builder.Property(e => e.Ref4).HasMaxLength(50);

        // Sales Quotation source stamps (see scripts/alter-saso-qt-link-columns.sql).
        builder.Property(e => e.QtNo).HasColumnName("QTNo").HasMaxLength(30);
        builder.Property(e => e.QtCustRel).HasColumnName("QTCustRel");

        builder.Property(e => e.CreatedDate).HasColumnName("Created").HasColumnType("datetime2");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(20);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated").HasColumnType("datetime2");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(20);
        builder.Property(e => e.RowVersion).IsRowVersion();

        builder.HasMany(e => e.Details)
            .WithOne(e => e.So)
            .HasForeignKey(e => new { e.CompanyCode, e.BranchCode, e.SoNo, e.CustRel })
            .OnDelete(DeleteBehavior.Cascade);

        // SQL Server / SQLite both accept this filter form via EnsureCreated / migrations.
        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.SoNo })
            .IsUnique()
            .HasFilter("IsCurrent = 1")
            .HasDatabaseName("UX_SaSO_Current");

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.Status, e.SoDate })
            .HasDatabaseName("IX_SaSO_Company_Branch_Status_SoDate");

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.CustCode })
            .HasDatabaseName("IX_SaSO_Company_Branch_CustCode");

        // One Sales Order per quotation revision. Filtered so ordinary (non-quotation) SOs, which
        // all carry NULL QtNo, are unaffected.
        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.QtNo, e.QtCustRel })
            .IsUnique()
            .HasFilter("QTNo IS NOT NULL")
            .HasDatabaseName("UX_SaSO_QtSource");
    }
}
