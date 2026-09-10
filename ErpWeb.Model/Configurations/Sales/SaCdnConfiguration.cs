using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public class SaCdnConfiguration : IEntityTypeConfiguration<SaCdn>
{
    public void Configure(EntityTypeBuilder<SaCdn> builder)
    {
        builder.ToTable("SaCDN");
        builder.HasKey(e => new { e.CompanyCode, e.BranchCode, e.DocNo });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.DocNo).HasMaxLength(30).IsRequired();
        builder.Property(e => e.DocDate).HasColumnType("datetime2").IsRequired();
        builder.Property(e => e.Status).HasMaxLength(20).IsRequired();
        builder.Property(e => e.InvNo).HasMaxLength(30);
        builder.Property(e => e.DoNo).HasColumnName("DONo").HasMaxLength(30);
        builder.Property(e => e.Type).HasMaxLength(20).IsRequired();
        builder.Property(e => e.CustCode).HasMaxLength(60).IsRequired();
        builder.Property(e => e.CustName).HasMaxLength(200);
        builder.Property(e => e.InvAddress1).HasMaxLength(100);
        builder.Property(e => e.InvAddress2).HasMaxLength(100);
        builder.Property(e => e.InvAddress3).HasMaxLength(100);
        builder.Property(e => e.InvAddress4).HasMaxLength(100);
        builder.Property(e => e.City).HasMaxLength(50);
        builder.Property(e => e.State).HasMaxLength(50);
        builder.Property(e => e.PostalCode).HasMaxLength(20);
        builder.Property(e => e.Country).HasMaxLength(50);
        builder.Property(e => e.Tel).HasMaxLength(50);
        builder.Property(e => e.Fax).HasMaxLength(50);
        builder.Property(e => e.PayCode).HasMaxLength(20);
        builder.Property(e => e.Currency).HasMaxLength(20);
        builder.Property(e => e.TaxGrCode).HasMaxLength(20);
        builder.Property(e => e.Remarks).HasMaxLength(500);
        builder.Property(e => e.GrossAmnt).HasPrecision(18, 2);
        builder.Property(e => e.Taxes).HasPrecision(18, 2);
        builder.Property(e => e.TotAmnt).HasPrecision(18, 2);
        builder.Property(e => e.Prefix).HasMaxLength(20);
        builder.Property(e => e.LocationCode).HasMaxLength(10);
        builder.Property(e => e.ProjId).HasColumnName("ProjID").HasMaxLength(20);
        builder.Property(e => e.SalesRep).HasMaxLength(20);
        builder.Property(e => e.Dept).HasMaxLength(20);
        builder.Property(e => e.CurrRate).HasPrecision(18, 6);
        builder.Property(e => e.RefNo).HasMaxLength(50);
        builder.Property(e => e.ExternalDocNo).HasMaxLength(50);
        builder.Property(e => e.ReturnStock).IsRequired();
        builder.Property(e => e.IrnmCancelOn).HasColumnName("IRNMCancelOn").HasColumnType("datetime2");
        builder.Property(e => e.IrbmSubmitId).HasColumnName("IRBMSubmitID").HasMaxLength(50);
        builder.Property(e => e.IrbmUuid).HasColumnName("IRBMUUID").HasMaxLength(50);
        builder.Property(e => e.IrbmOriUuid).HasColumnName("IRBMORIUUID").HasMaxLength(50);
        builder.Property(e => e.IrbmSentOn).HasColumnName("IRBMSentOn").HasColumnType("datetime2");
        builder.Property(e => e.IrbmValidOn).HasColumnName("IRBMValidOn").HasColumnType("datetime2");
        builder.Property(e => e.IrbmError).HasColumnName("IRBMError").HasMaxLength(500);
        builder.Property(e => e.IrbmStatus).HasColumnName("IRBMStatus").HasMaxLength(50);
        builder.Property(e => e.PostedDate).HasColumnType("datetime2");
        builder.Property(e => e.PostedBy).HasMaxLength(20);
        builder.Property(e => e.RollbackDate).HasColumnType("datetime2");
        builder.Property(e => e.RollbackBy).HasMaxLength(20);

        builder.Property(e => e.CreatedDate).HasColumnName("Created").HasColumnType("datetime2");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(20);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated").HasColumnType("datetime2");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(20);
        builder.Property(e => e.RowVersion).IsRowVersion();

        builder.HasMany(e => e.Details)
            .WithOne(e => e.Cdn)
            .HasForeignKey(e => new { e.CompanyCode, e.BranchCode, e.DocNo })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.Status, e.DocDate })
            .HasDatabaseName("IX_SaCDN_Company_Branch_Status_DocDate");

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.Type, e.Status })
            .HasDatabaseName("IX_SaCDN_Company_Branch_Type_Status");

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.CustCode })
            .HasDatabaseName("IX_SaCDN_Company_Branch_CustCode");

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.InvNo })
            .HasDatabaseName("IX_SaCDN_Company_Branch_InvNo");
    }
}
