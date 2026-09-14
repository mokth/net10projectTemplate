using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class IvTrxBatchConfiguration : IEntityTypeConfiguration<IvTrxBatch>
{
    public void Configure(EntityTypeBuilder<IvTrxBatch> builder)
    {
        builder.ToTable("IvTrxBatch");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("ID").ValueGeneratedOnAdd();
        builder.Property(e => e.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.TrxType).HasMaxLength(20).IsRequired();
        builder.Property(e => e.BatchStatus).HasMaxLength(20).IsRequired();
        builder.Property(e => e.RefNo).HasMaxLength(50);
        builder.Property(e => e.Remarks).HasMaxLength(250);
        builder.Property(e => e.LocationCode).HasMaxLength(10);
        builder.Property(e => e.SourceFingerprint).HasMaxLength(64);
        builder.Property(e => e.PostedBy).HasMaxLength(10);
        builder.Property(e => e.RollbackBy).HasMaxLength(10);
        builder.Property(e => e.ForceCloseBy).HasMaxLength(10);
        builder.Property(e => e.ForceCloseReason).HasMaxLength(250);
        builder.Property(e => e.PostedCount).HasDefaultValue(0);
        builder.Property(e => e.RollbackCount).HasDefaultValue(0);
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(10);

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.BatchNo })
            .IsUnique()
            .HasDatabaseName("UQ_IvTrxBatch_Company_Branch_BatchNo");

        builder.HasIndex(e => new { e.CompanyCode, e.BatchStatus })
            .HasDatabaseName("IX_IvTrxBatch_Company_BatchStatus");

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.TrxType, e.RefNo })
            .HasDatabaseName("IX_IvTrxBatch_Company_Branch_TrxType_RefNo");

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.RefNo })
            .IsUnique()
            .HasFilter("[TrxType] = 'SP' AND [RefNo] IS NOT NULL")
            .HasDatabaseName("UQ_IvTrxBatch_SP_Ref");

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.RefNo })
            .IsUnique()
            .HasFilter("[TrxType] = N'CR' AND [RefNo] >= N'CN/' AND [RefNo] < N'CN0'")
            .HasDatabaseName("UQ_IvTrxBatch_CR_CnRef");

        // C7: one PoCdn owns at most one VR batch (and one batch is owned by at most one PoCdn).
        // IvVendorReturnService.NormalizeRefNo defaults RefNo to the batch number, so the filter is
        // range-scoped to the PCN/ prefix, exactly as the CR index scopes the CN/ prefix.
        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.RefNo })
            .IsUnique()
            .HasFilter("[TrxType] = N'VR' AND [RefNo] >= N'PCN/' AND [RefNo] < N'PCN0'")
            .HasDatabaseName("UQ_IvTrxBatch_VR_PcnRef");
    }
}
