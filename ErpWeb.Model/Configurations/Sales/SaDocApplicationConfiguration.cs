using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public class SaDocApplicationConfiguration : IEntityTypeConfiguration<SaDocApplication>
{
    public void Configure(EntityTypeBuilder<SaDocApplication> builder)
    {
        builder.ToTable("SaDocApplication");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedOnAdd();

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.SourceDocType).HasMaxLength(10).IsRequired();
        builder.Property(e => e.SourceDocId).HasMaxLength(30).IsRequired();
        builder.Property(e => e.SourceCustRel).IsRequired();
        builder.Property(e => e.SourceLineId).IsRequired();
        builder.Property(e => e.TargetDocType).HasMaxLength(10).IsRequired();
        builder.Property(e => e.TargetDocId).HasMaxLength(30).IsRequired();
        builder.Property(e => e.TargetCustRel).IsRequired();
        builder.Property(e => e.TargetLineId).IsRequired();
        builder.Property(e => e.RelatedSoNo).HasColumnName("RelatedSONo").HasMaxLength(30).IsRequired();
        builder.Property(e => e.RelatedCustRel).IsRequired();
        builder.Property(e => e.RelatedSoLine).HasColumnName("RelatedSOLine").IsRequired();
        builder.Property(e => e.AppliedQty).HasPrecision(18, 4).IsRequired();
        builder.Property(e => e.AppliedAmount).HasPrecision(18, 2);
        builder.Property(e => e.Created).HasColumnType("datetime2");
        builder.Property(e => e.CreatedUid).HasColumnName("CreatedUID").HasMaxLength(20);

        builder.HasIndex(e => new
            {
                e.CompanyCode,
                e.BranchCode,
                e.SourceDocType,
                e.SourceDocId,
                e.SourceCustRel,
                e.SourceLineId,
                e.TargetDocType,
                e.TargetDocId,
                e.TargetLineId
            })
            .IsUnique()
            .HasDatabaseName("UQ_SaDocApplication_SourceTarget");

        builder.HasIndex(e => new
            {
                e.CompanyCode,
                e.BranchCode,
                e.TargetDocType,
                e.TargetDocId,
                e.TargetLineId
            })
            .IsUnique()
            .HasDatabaseName("UQ_SaDocApplication_TargetLine");

        builder.HasIndex(e => new
            {
                e.CompanyCode,
                e.BranchCode,
                e.SourceDocType,
                e.SourceDocId,
                e.SourceCustRel,
                e.SourceLineId
            })
            .HasDatabaseName("IX_SaDocApplication_Source");

        builder.HasIndex(e => new
            {
                e.CompanyCode,
                e.BranchCode,
                e.TargetDocType,
                e.RelatedSoNo,
                e.RelatedCustRel,
                e.RelatedSoLine
            })
            .HasDatabaseName("IX_SaDocApplication_RelatedSO");
    }
}
