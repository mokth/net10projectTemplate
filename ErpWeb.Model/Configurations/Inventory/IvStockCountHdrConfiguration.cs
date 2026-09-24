using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class IvStockCountHdrConfiguration : IEntityTypeConfiguration<IvStockCountHdr>
{
    public void Configure(EntityTypeBuilder<IvStockCountHdr> builder)
    {
        builder.ToTable("IvStockCountHdr");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("ID").ValueGeneratedOnAdd();
        builder.Property(e => e.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.CountNo).HasMaxLength(30).IsRequired();
        builder.Property(e => e.Status).HasMaxLength(20).IsRequired();
        builder.Property(e => e.WHCode).HasMaxLength(20);
        builder.Property(e => e.LocCode).HasMaxLength(10);
        builder.Property(e => e.IClassCode).HasMaxLength(10);
        builder.Property(e => e.ISubClassCode).HasMaxLength(10);
        builder.Property(e => e.IType).HasMaxLength(20);
        builder.Property(e => e.IStatus).HasMaxLength(20);
        builder.Property(e => e.ICodeList).HasMaxLength(1000);
        builder.Property(e => e.CountedBy).HasMaxLength(10);
        builder.Property(e => e.Remark).HasMaxLength(250);
        builder.Property(e => e.PostedBy).HasMaxLength(10);
        builder.Property(e => e.RolledBackBy).HasMaxLength(10);
        builder.Property(e => e.RollbackReason).HasMaxLength(250);

        // Legacy audit column aliases used across every inventory table.
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(10);

        builder.Property(e => e.RowVersion).IsRowVersion();

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.CountNo })
            .IsUnique()
            .HasDatabaseName("UQ_IvStockCountHdr_No");

        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.Status })
            .HasDatabaseName("IX_IvStockCountHdr_Status");

        builder.HasMany(e => e.Lines)
            .WithOne(l => l.Header)
            .HasForeignKey(l => l.StockCountId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
