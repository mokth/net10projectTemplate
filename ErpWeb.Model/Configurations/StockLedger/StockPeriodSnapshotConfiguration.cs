using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.StockLedger;

public sealed class StockPeriodSnapshotHdrConfiguration : IEntityTypeConfiguration<StockPeriodSnapshotHdr>
{
    public void Configure(EntityTypeBuilder<StockPeriodSnapshotHdr> builder)
    {
        builder.ToTable("StockPeriodSnapshotHdr", table =>
        {
            table.HasCheckConstraint("CK_StockPeriodSnapshotHdr_Revision", "[Revision] > 0");
            table.HasCheckConstraint("CK_StockPeriodSnapshotHdr_Watermark", "[PostingSequenceWatermark] >= 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.PeriodKey).HasColumnType("char(7)").IsRequired();
        builder.Property(x => x.SourceDataHash).HasColumnType("char(64)").IsRequired();
        builder.Property(x => x.QuantityStatus).HasMaxLength(20).IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(7)");
        builder.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
        builder.HasOne(x => x.LedgerEpoch).WithMany()
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.LedgerEpochId })
            .HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.PeriodKey, x.Revision })
            .IsUnique().HasDatabaseName("UQ_StockPeriodSnapshotHdr_Revision");
    }
}

public sealed class StockPeriodSnapshotLineConfiguration : IEntityTypeConfiguration<StockPeriodSnapshotLine>
{
    public void Configure(EntityTypeBuilder<StockPeriodSnapshotLine> builder)
    {
        builder.ToTable("StockPeriodSnapshotLine");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.LedgerArea).HasMaxLength(10).IsRequired();
        builder.Property(x => x.StockIdentity).HasMaxLength(250).IsRequired();
        builder.Property(x => x.ItemCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.BaseUom).HasMaxLength(10).IsRequired();
        builder.Property(x => x.BaseQty).HasPrecision(18, 4);
        builder.HasOne(x => x.Header).WithMany(x => x.Lines)
            .HasForeignKey(x => x.HeaderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.HeaderId, x.LedgerArea, x.StockIdentity })
            .IsUnique().HasDatabaseName("UQ_StockPeriodSnapshotLine_Identity");
    }
}
