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
        builder.Property(e => e.PostedBy).HasMaxLength(10);
        builder.Property(e => e.RollbackBy).HasMaxLength(10);
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
    }
}
