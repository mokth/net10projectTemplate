using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class IvPeriodCloseHdrConfiguration : IEntityTypeConfiguration<IvPeriodCloseHdr>
{
    public void Configure(EntityTypeBuilder<IvPeriodCloseHdr> builder)
    {
        builder.ToTable("IvPeriodCloseHdr");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("ID").ValueGeneratedOnAdd();
        builder.Property(e => e.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(e => e.PeriodFrom).HasColumnType("date").IsRequired();
        builder.Property(e => e.PeriodTo).HasColumnType("date").IsRequired();
        builder.Property(e => e.Status).HasMaxLength(20).IsRequired();
        builder.Property(e => e.ClosedBy).HasMaxLength(10);
        builder.Property(e => e.ReopenedBy).HasMaxLength(10);
        builder.Property(e => e.ReopenReason).HasMaxLength(250);
        builder.Property(e => e.Remark).HasMaxLength(250);

        builder.Property(e => e.TotalOpeningValue).HasPrecision(18, 4);
        builder.Property(e => e.TotalInValue).HasPrecision(18, 4);
        builder.Property(e => e.TotalOutValue).HasPrecision(18, 4);
        builder.Property(e => e.TotalClosingValue).HasPrecision(18, 4);
        builder.Property(e => e.LastReopenClosingValue).HasPrecision(18, 4);

        // Legacy audit column aliases used across every inventory table.
        builder.Property(e => e.CreatedDate).HasColumnName("Created");
        builder.Property(e => e.CreatedBy).HasColumnName("UserID").HasMaxLength(10);
        builder.Property(e => e.ModifiedDate).HasColumnName("Updated");
        builder.Property(e => e.ModifiedBy).HasColumnName("UpdatedUID").HasMaxLength(10);

        builder.Property(e => e.RowVersion).IsRowVersion();

        // One closed period per company+branch+PeriodFrom (D5/D6: periods are identified by dates).
        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.PeriodFrom })
            .IsUnique()
            .HasDatabaseName("UQ_IvPeriodCloseHdr_Period");

        // The guard's hot path: the latest CLOSED period for a tenant, with its closed-through date.
        builder.HasIndex(e => new { e.CompanyCode, e.BranchCode, e.Status })
            .HasDatabaseName("IX_IvPeriodCloseHdr_Tenant_Status")
            .IncludeProperties(e => e.PeriodTo);

        builder.HasMany(e => e.Lines)
            .WithOne(l => l.Header)
            .HasForeignKey(l => l.PeriodCloseId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
