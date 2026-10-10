using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public sealed class SaDeliveryPodAttachmentConfiguration : IEntityTypeConfiguration<SaDeliveryPodAttachment>
{
    public void Configure(EntityTypeBuilder<SaDeliveryPodAttachment> builder)
    {
        builder.ToTable("SaDeliveryPodAttachment", table =>
        {
            table.HasCheckConstraint("CK_SaDeliveryPodAttachment_Type", "AttachmentType IN ('PHOTO', 'SIGNATURE', 'DOCUMENT')");
            table.HasCheckConstraint("CK_SaDeliveryPodAttachment_FileSize", "FileSize > 0");
        });
        builder.HasKey(x => new { x.CompanyCode, x.BranchCode, x.AttachmentId });
        builder.Property(x => x.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(x => x.AttachmentId).HasColumnName("AttachmentID").ValueGeneratedOnAdd();
        builder.Property(x => x.AttemptId).HasColumnName("AttemptID").IsRequired();
        builder.Property(x => x.DoNo).HasColumnName("DONo").HasMaxLength(30);
        builder.Property(x => x.AttachmentType).HasMaxLength(20).IsRequired();
        builder.Property(x => x.OriginalFileName).HasMaxLength(255).IsRequired();
        builder.Property(x => x.StoredFileName).HasMaxLength(255).IsRequired();
        builder.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.FileSize).IsRequired();
        builder.Property(x => x.CreatedDate).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.CreatedBy).HasMaxLength(20).IsRequired();

        builder.HasOne(x => x.Do)
            .WithMany()
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.DoNo })
            .OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("FK_SaDeliveryPodAttachment_SaDO");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.AttemptId, x.CreatedDate })
            .HasDatabaseName("IX_SaDeliveryPodAttachment_Tenant_Attempt_Date");
    }
}
