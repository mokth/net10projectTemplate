using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.StockLedger;

public sealed class StockPostingConfiguration : IEntityTypeConfiguration<StockPosting>
{
    public void Configure(EntityTypeBuilder<StockPosting> builder)
    {
        builder.ToTable("StockPosting", table =>
        {
            table.UseSqlOutputClause(false);
            table.HasCheckConstraint("CK_StockPosting_Sequence", "[PostingSequence] > 0");
            table.HasCheckConstraint("CK_StockPosting_Revision", "[DocumentRevision] >= 0");
            table.HasCheckConstraint("CK_StockPosting_Hashes",
                "LEN([RequestFingerprint]) = 64 AND LEN([SourceSnapshotHash]) = 64");
            table.HasCheckConstraint("CK_StockPosting_NoSelfReverse",
                "[ReversesPostingId] IS NULL OR [ReversesPostingId] <> [Id]");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.CommandType).HasMaxLength(40).IsRequired();
        builder.Property(x => x.RequestFingerprint).HasColumnType("char(64)").IsRequired();
        builder.Property(x => x.SourceModule).HasMaxLength(20).IsRequired();
        builder.Property(x => x.SourceDocumentType).HasMaxLength(40).IsRequired();
        builder.Property(x => x.SourceDocumentId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.SourceDocumentNo).HasMaxLength(50).IsRequired();
        builder.Property(x => x.PostingRole).HasMaxLength(20).IsRequired();
        builder.Property(x => x.SourceSnapshotJson).IsRequired();
        builder.Property(x => x.SourceSnapshotHash).HasColumnType("char(64)").IsRequired();
        builder.Property(x => x.EffectiveAt).HasColumnType("datetime2(7)");
        builder.Property(x => x.BusinessDate).HasColumnType("date");
        builder.Property(x => x.PeriodKey).HasColumnType("char(7)").IsRequired();
        builder.Property(x => x.PostedAtUtc).HasColumnType("datetime2(7)");
        builder.Property(x => x.PostedBy).HasMaxLength(100).IsRequired();
        builder.Property(x => x.ReasonCode).HasMaxLength(40);
        builder.Property(x => x.ReasonText).HasMaxLength(500);
        builder.Property(x => x.SealedAtUtc).HasColumnType("datetime2(7)");

        builder.HasAlternateKey(x => new { x.CompanyCode, x.BranchCode, x.Id })
            .HasName("AK_StockPosting_Tenant_Id");
        builder.HasOne(x => x.LedgerEpoch).WithMany(x => x.Postings)
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.LedgerEpochId })
            .HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ReversesPosting).WithMany()
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.ReversesPostingId })
            .HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.PostingSequence })
            .IsUnique().HasDatabaseName("UQ_StockPosting_BranchSequence");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.CommandType, x.RequestId })
            .IsUnique().HasDatabaseName("UQ_StockPosting_Request");
        builder.HasIndex(x => new
            {
                x.CompanyCode, x.BranchCode, x.SourceModule, x.SourceDocumentType,
                x.SourceDocumentId, x.DocumentRevision, x.PostingRole
            })
            .IsUnique()
            .HasDatabaseName("UQ_StockPosting_SourceRevisionRole");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.ReversesPostingId })
            .IsUnique().HasFilter("[ReversesPostingId] IS NOT NULL")
            .HasDatabaseName("UQ_StockPosting_Reversal");
    }
}
