using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public class SaEInvoiceLogConfiguration : IEntityTypeConfiguration<SaEInvoiceLog>
{
    public void Configure(EntityTypeBuilder<SaEInvoiceLog> builder)
    {
        builder.ToTable("SaEInvoiceLog");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).ValueGeneratedOnAdd();
        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.DocumentType).HasMaxLength(10).IsRequired();
        builder.Property(e => e.DocumentNo).HasMaxLength(30).IsRequired();
        builder.Property(e => e.SubmissionId).HasMaxLength(50);
        builder.Property(e => e.DocumentUuid).HasMaxLength(50);
        builder.Property(e => e.Action).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Status).HasMaxLength(50);
        builder.Property(e => e.AttemptNo).IsRequired();
        builder.Property(e => e.RequestTime).HasColumnType("datetime2");
        builder.Property(e => e.ResponseTime).HasColumnType("datetime2");
        builder.Property(e => e.ErrorCode).HasMaxLength(100);
        // Full error text, deliberately wider than the document's short IRBMError column.
        builder.Property(e => e.ErrorMessage).HasMaxLength(4000);
        builder.Property(e => e.CreatedBy).HasMaxLength(20);
        builder.Property(e => e.CreatedOn).HasColumnName("CreatedOn").HasColumnType("datetime2").IsRequired();

        builder.HasIndex(e => new { e.CompanyCode, e.DocumentType, e.DocumentNo, e.Id })
            .HasDatabaseName("IX_SaEInvoiceLog_Document");

        builder.HasIndex(e => new { e.CompanyCode, e.CreatedOn })
            .HasDatabaseName("IX_SaEInvoiceLog_Company_CreatedOn");
    }
}
