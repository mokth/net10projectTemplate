using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Planning;

public class PrBomHdrConfiguration : IEntityTypeConfiguration<PrBomHdr>
{
    public void Configure(EntityTypeBuilder<PrBomHdr> builder)
    {
        builder.ToTable("PrBomHdr");
        builder.HasKey(e => e.Uid);

        builder.Property(e => e.Uid).HasColumnName("UID").ValueGeneratedOnAdd();
        builder.Property(e => e.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.ProdCode).HasMaxLength(30).IsRequired();
        builder.Property(e => e.Version).IsRequired();
        builder.Property(e => e.Status).HasMaxLength(20).IsRequired();
        builder.Property(e => e.EffectiveFrom);
        builder.Property(e => e.EffectiveTo);
        builder.Property(e => e.BaseQty).HasPrecision(18, 4).IsRequired();
        builder.Property(e => e.BaseUom).HasColumnName("BaseUOM").HasMaxLength(10);
        builder.Property(e => e.Prefix).HasMaxLength(10);
        builder.Property(e => e.Remark).HasMaxLength(1000);
        builder.Property(e => e.BranchCode).HasMaxLength(5);
        builder.Property(e => e.LocationCode).HasMaxLength(10);
        builder.Property(e => e.ValidationRuleVersion).HasMaxLength(30);
        builder.Property(e => e.ValidationStatus).HasMaxLength(20).IsRequired()
            .HasDefaultValue(PrBomValidationStatuses.Unverified).ValueGeneratedNever();
        builder.Property(e => e.ValidatedBy).HasMaxLength(10);
        builder.Property(e => e.ActivatedBy).HasMaxLength(10);
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(10);
        builder.Property(e => e.RowVersion).IsRowVersion();

        builder.HasIndex(e => new { e.CompanyCode, e.ProdCode, e.Version })
            .IsUnique()
            .HasDatabaseName("UQ_PrBomHdr_Company_Prod_Version");

        builder.HasIndex(e => new { e.CompanyCode, e.ProdCode, e.Status })
            .HasDatabaseName("IX_PrBomHdr_Company_Prod_Status");

        builder.HasMany(e => e.Lines)
            .WithOne(e => e.Header!)
            .HasForeignKey(e => e.BomHdrId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(e => e.RouteSteps)
            .WithOne(e => e.Header!)
            .HasForeignKey(e => e.BomHdrId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
