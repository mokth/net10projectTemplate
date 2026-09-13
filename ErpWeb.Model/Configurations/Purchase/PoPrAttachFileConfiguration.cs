using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Purchase;

public class PoPrAttachFileConfiguration : IEntityTypeConfiguration<PoPrAttachFile>
{
    public void Configure(EntityTypeBuilder<PoPrAttachFile> builder)
    {
        builder.ToTable("POPRAttachFile");
        builder.HasKey(e => new { e.CompanyCode, e.BranchCode, e.DocId, e.DocName });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.DocId).HasColumnName("DocID").HasMaxLength(50).IsRequired();
        builder.Property(e => e.DocName).HasMaxLength(200).IsRequired();
        builder.Property(e => e.DocKey).HasMaxLength(30);
        builder.Property(e => e.DocName2).HasMaxLength(200);

        builder.Property(e => e.CreatedDate).HasColumnName("Created").HasColumnType("datetime2");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(20);

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.DocKey })
            .HasDatabaseName("IX_POPRAttachFile_Company_Branch_DocKey");
    }
}
