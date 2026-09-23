using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Purchase;

public class PoSbCdnConfiguration : IEntityTypeConfiguration<PoSbCdn>
{
    public void Configure(EntityTypeBuilder<PoSbCdn> builder)
    {
        builder.ToTable("POSbCdn");
        builder.HasKey(e => new { e.CompanyCode, e.BranchCode, e.DocNo });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.DocNo).HasMaxLength(30).IsRequired();
        builder.Property(e => e.DocDate).HasColumnType("datetime2").IsRequired();
        builder.Property(e => e.Status).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Type).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Prefix).HasMaxLength(20);

        builder.Property(e => e.VendorCode).HasMaxLength(60).IsRequired();
        builder.Property(e => e.VendorName).HasMaxLength(200);

        // Soft reference to the originating self-billed invoice. Resolved by (CompanyCode, BranchCode,
        // DocNo) — never a bare number lookup. No FK: the origin is validated by PoSbOriginResolver.
        builder.Property(e => e.OriginSbInvNo).HasMaxLength(30);

        builder.Property(e => e.Currency).HasMaxLength(20);
        builder.Property(e => e.CurrRate).HasPrecision(18, 6);
        builder.Property(e => e.TaxGrCode).HasMaxLength(20);
        builder.Property(e => e.Remarks).HasMaxLength(500);
        builder.Property(e => e.GrossAmnt).HasPrecision(18, 2);
        builder.Property(e => e.Taxes).HasPrecision(18, 2);
        builder.Property(e => e.TotAmnt).HasPrecision(18, 2);
        builder.Property(e => e.LocationCode).HasMaxLength(10);

        // LHDN e-Invoice state — spellings mirror SaInvoice/SaCdn.
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
            .WithOne(e => e.Cdn)
            .HasForeignKey(e => new { e.CompanyCode, e.BranchCode, e.DocNo })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.Type, e.Status, e.DocDate })
            .HasDatabaseName("IX_POSbCdn_Company_Branch_Type_Status_DocDate");

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.VendorCode })
            .HasDatabaseName("IX_POSbCdn_Company_Branch_VendorCode");

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.OriginSbInvNo })
            .HasDatabaseName("IX_POSbCdn_Company_Branch_OriginSbInvNo");

        // Drives the refresh-all candidate query (SUBMITTED notes in this branch).
        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.IrbmStatus })
            .HasDatabaseName("IX_POSbCdn_Company_Branch_IrbmStatus");
    }
}
