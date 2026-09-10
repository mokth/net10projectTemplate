using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public class SaDocApplicationBackfillSkipConfiguration : IEntityTypeConfiguration<SaDocApplicationBackfillSkip>
{
    public void Configure(EntityTypeBuilder<SaDocApplicationBackfillSkip> builder)
    {
        builder.ToTable("SaDocApplicationBackfillSkip");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedOnAdd();

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.DocType).HasMaxLength(10).IsRequired();
        builder.Property(e => e.DocId).HasMaxLength(30).IsRequired();
        builder.Property(e => e.Line).IsRequired();
        builder.Property(e => e.ViolationCode).HasMaxLength(40).IsRequired();
        builder.Property(e => e.Reason).HasMaxLength(500).IsRequired();
        builder.Property(e => e.SourceValues).HasMaxLength(1000);
        builder.Property(e => e.TargetValues).HasMaxLength(1000);
        builder.Property(e => e.CreatedUtc).HasColumnType("datetime2").IsRequired();

        builder.HasIndex(e => new
            {
                e.CompanyCode,
                e.BranchCode,
                e.DocType,
                e.DocId,
                e.Line,
                e.ViolationCode
            })
            .IsUnique()
            .HasDatabaseName("UQ_SaDocApplicationBackfillSkip_Key");
    }
}
