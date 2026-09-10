using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public class SaDoConfiguration : IEntityTypeConfiguration<SaDo>
{
    public void Configure(EntityTypeBuilder<SaDo> builder)
    {
        builder.ToTable("SaDO");
        builder.HasKey(e => new { e.CompanyCode, e.BranchCode, e.DoNo });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.DoNo).HasColumnName("DONo").HasMaxLength(30).IsRequired();
        builder.Property(e => e.DoDate).HasColumnName("DODate").HasColumnType("datetime2").IsRequired();
        builder.Property(e => e.Status).HasMaxLength(20).IsRequired();
        builder.Property(e => e.BillingStatus).HasMaxLength(10).IsRequired();
        builder.Property(e => e.CustCode).HasMaxLength(60).IsRequired();
        builder.Property(e => e.CustName).HasMaxLength(200);
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
        builder.Property(e => e.ShipVia).HasMaxLength(50);
        builder.Property(e => e.Currency).HasMaxLength(20);
        builder.Property(e => e.CurrRate).HasPrecision(18, 6).IsRequired();
        builder.Property(e => e.PayCode).HasMaxLength(20);
        builder.Property(e => e.Remarks).HasMaxLength(500);
        builder.Property(e => e.Departure).HasMaxLength(100);
        builder.Property(e => e.Destination).HasMaxLength(100);
        builder.Property(e => e.Vessel).HasMaxLength(100);
        builder.Property(e => e.GrossAmnt).HasPrecision(18, 2);
        builder.Property(e => e.Taxes).HasPrecision(18, 2);
        builder.Property(e => e.TotAmnt).HasPrecision(18, 2);
        builder.Property(e => e.Prefix).HasMaxLength(20);
        builder.Property(e => e.ShipWarehouse).HasMaxLength(20);
        builder.Property(e => e.LocationCode).HasMaxLength(10);
        builder.Property(e => e.CustDiscount).HasPrecision(18, 6);
        builder.Property(e => e.ContactPerson).HasMaxLength(100);
        builder.Property(e => e.ProjId).HasColumnName("ProjID").HasMaxLength(20);
        builder.Property(e => e.SalesRep).HasMaxLength(20);
        builder.Property(e => e.Ref1).HasMaxLength(50);
        builder.Property(e => e.Ref2).HasMaxLength(50);
        builder.Property(e => e.Ref3).HasMaxLength(50);
        builder.Property(e => e.Ref4).HasMaxLength(50);
        builder.Property(e => e.ShipOutDate).HasColumnType("datetime2");
        builder.Property(e => e.DriverName).HasMaxLength(100);
        builder.Property(e => e.DriverPlate).HasMaxLength(50);
        builder.Property(e => e.RecordTime).HasColumnType("datetime2");
        builder.Property(e => e.ShipOutRemark).HasMaxLength(500);
        builder.Property(e => e.PostedBy).HasMaxLength(20);
        builder.Property(e => e.RollbackBy).HasMaxLength(20);

        builder.Property(e => e.CreatedDate).HasColumnName("Created").HasColumnType("datetime2");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(20);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated").HasColumnType("datetime2");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(20);
        builder.Property(e => e.RowVersion).IsRowVersion();

        builder.HasMany(e => e.Details)
            .WithOne(e => e.Do)
            .HasForeignKey(e => new { e.CompanyCode, e.BranchCode, e.DoNo })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.Status, e.DoDate })
            .HasDatabaseName("IX_SaDO_Company_Branch_Status_DoDate");

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.CustCode })
            .HasDatabaseName("IX_SaDO_Company_Branch_CustCode");
    }
}
