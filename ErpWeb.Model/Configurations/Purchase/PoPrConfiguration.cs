using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Purchase;

public class PoPrConfiguration : IEntityTypeConfiguration<PoPr>
{
    public void Configure(EntityTypeBuilder<PoPr> builder)
    {
        builder.ToTable("POPR");
        builder.HasKey(e => new { e.CompanyCode, e.BranchCode, e.PrNo });

        builder.Property(e => e.CompanyCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(10).IsRequired();
        builder.Property(e => e.PrNo).HasColumnName("PRNo").HasMaxLength(30).IsRequired();
        builder.Property(e => e.CreateDt).HasColumnType("datetime2").IsRequired();
        builder.Property(e => e.Requester).HasMaxLength(50);
        builder.Property(e => e.Status).HasColumnName("PRStat").HasMaxLength(20).IsRequired();
        builder.Property(e => e.DeptCode).HasMaxLength(20);
        builder.Property(e => e.CheckedBy).HasMaxLength(20);
        builder.Property(e => e.AuthorisedBy).HasMaxLength(20);
        builder.Property(e => e.ApprovedBy).HasMaxLength(20);
        builder.Property(e => e.ApprovedDate).HasColumnType("datetime2");
        builder.Property(e => e.Remarks).HasMaxLength(500);
        builder.Property(e => e.AuthorisedBy2nd).HasMaxLength(20);
        builder.Property(e => e.PrType).HasColumnName("PRType").HasMaxLength(20);
        builder.Property(e => e.LocationCode).HasMaxLength(10);
        builder.Property(e => e.PoNo).HasColumnName("PONo").HasMaxLength(30);
        builder.Property(e => e.ApprReason).HasMaxLength(200);
        builder.Property(e => e.ProjId).HasColumnName("ProjID").HasMaxLength(20);
        builder.Property(e => e.ApprovedBy2).HasMaxLength(20);
        builder.Property(e => e.ApprovedDate2).HasMaxLength(50);

        builder.Property(e => e.CreatedDate).HasColumnName("Created").HasColumnType("datetime2");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(20);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated").HasColumnType("datetime2");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(20);
        builder.Property(e => e.RowVersion).IsRowVersion();

        builder.HasMany(e => e.Details)
            .WithOne(e => e.Pr)
            .HasForeignKey(e => new { e.CompanyCode, e.BranchCode, e.PrNo })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.Status, e.CreateDt })
            .HasDatabaseName("IX_POPR_Company_Branch_Status_CreateDt");
    }
}
