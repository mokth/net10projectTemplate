using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Purchase;

public class PoSbInvoiceConfiguration : IEntityTypeConfiguration<PoSbInvoice>
{
    public void Configure(EntityTypeBuilder<PoSbInvoice> builder)
    {
        builder.ToTable("POSbInvoice");
        builder.HasKey(e => new { e.CompanyCode, e.BranchCode, e.DocNo });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.DocNo).HasMaxLength(30).IsRequired();
        builder.Property(e => e.DocDate).HasColumnType("datetime2").IsRequired();
        builder.Property(e => e.Status).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Prefix).HasMaxLength(20);
        builder.Property(e => e.VendorCode).HasMaxLength(60).IsRequired();
        builder.Property(e => e.VendorName).HasMaxLength(200);
        builder.Property(e => e.Currency).HasMaxLength(20);
        builder.Property(e => e.CurrRate).HasPrecision(18, 6);
        builder.Property(e => e.TaxGrCode).HasMaxLength(20);
        builder.Property(e => e.Remarks).HasMaxLength(500);
        builder.Property(e => e.GrossAmnt).HasPrecision(18, 2);
        builder.Property(e => e.Taxes).HasPrecision(18, 2);
        builder.Property(e => e.TotAmnt).HasPrecision(18, 2);
        builder.Property(e => e.LocationCode).HasMaxLength(10);

        // LHDN e-Invoice state. Column names/spellings mirror SaInvoice/SaCdn (IRNMCancelOn included)
        // so the façade's LoadStateAsync / ApplyState read and write the same names as every other family.
        builder.Property(e => e.IrbmSubmitId).HasColumnName("IRBMSubmitID").HasMaxLength(50);
        builder.Property(e => e.IrbmUuid).HasColumnName("IRBMUUID").HasMaxLength(50);
        builder.Property(e => e.IrbmOriUuid).HasColumnName("IRBMORIUUID").HasMaxLength(50);
        builder.Property(e => e.IrbmSentOn).HasColumnName("IRBMSentOn").HasColumnType("datetime2");
        builder.Property(e => e.IrbmValidOn).HasColumnName("IRBMValidOn").HasColumnType("datetime2");
        builder.Property(e => e.IrbmError).HasColumnName("IRBMError").HasMaxLength(500);
        builder.Property(e => e.IrbmStatus).HasColumnName("IRBMStatus").HasMaxLength(50);
        builder.Property(e => e.IrbmOutcome).HasColumnName("IRBMOutcome").HasMaxLength(30);
        builder.Property(e => e.IrnmCancelOn).HasColumnName("IRNMCancelOn").HasColumnType("datetime2");

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
            .WithOne(e => e.Invoice)
            .HasForeignKey(e => new { e.CompanyCode, e.BranchCode, e.DocNo })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.Status, e.DocDate })
            .HasDatabaseName("IX_POSbInvoice_Company_Branch_Status_DocDate");

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.VendorCode })
            .HasDatabaseName("IX_POSbInvoice_Company_Branch_VendorCode");

        // Drives the refresh-all candidate query (SUBMITTED documents in this branch).
        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.IrbmStatus })
            .HasDatabaseName("IX_POSbInvoice_Company_Branch_IrbmStatus");
    }
}
