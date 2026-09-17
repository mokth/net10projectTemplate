using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class AdSmParamConfiguration : IEntityTypeConfiguration<AdSmParam>
{
    public void Configure(EntityTypeBuilder<AdSmParam> builder)
    {
        builder.ToTable("AdSmParam");
        builder.HasKey(e => e.ParamId);

        builder.Property(e => e.ModuleCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.ParamKey).HasMaxLength(60).IsRequired();
        builder.Property(e => e.ScopeCode).HasMaxLength(10).IsRequired();

        // 5 matches Company.CompanyCode and the company claim limit in InventoryTenantContext.
        builder.Property(e => e.CompanyCode).HasMaxLength(5);
        builder.Property(e => e.BranchCode).HasMaxLength(5);

        builder.Property(e => e.ValueText).HasMaxLength(400);

        // Explicit precision: EF's default would silently round a stored tolerance or quantity.
        builder.Property(e => e.ValueNumber).HasPrecision(28, 10);
        builder.Property(e => e.ValueDate).HasColumnType("date");

        builder.Property(e => e.Remark).HasMaxLength(400);
        builder.Property(e => e.CreatedBy).HasMaxLength(10);
        builder.Property(e => e.ModifiedBy).HasMaxLength(10);
        builder.Property(e => e.CreatedDate).HasColumnType("datetime");
        builder.Property(e => e.ModifiedDate).HasColumnType("datetime");
        builder.Property(e => e.RowVersion).IsRowVersion();

        // One row per key per scope target. NULL counts as EQUAL in a SQL Server unique index, which is
        // what keeps GLOBAL rows (CompanyCode / BranchCode both NULL) unique. On SQLite NULLs are
        // DISTINCT, so the service also checks for an existing row before inserting; the race itself is
        // covered by AdSmParamSqlServerConcurrencyTests.
        //
        // Deliberately NOT filtered on IsActive: a filtered index forces SET QUOTED_IDENTIFIER ON on
        // every INSERT/UPDATE against the table (SQL Server error 1934) and would exclude nothing,
        // because clearing a value DELETEs the row rather than deactivating it.
        builder.HasIndex(e => new { e.ModuleCode, e.ParamKey, e.ScopeCode, e.CompanyCode, e.BranchCode })
            .IsUnique()
            .HasDatabaseName("UQ_AdSmParam_Key");

        // The production script additionally declares INCLUDE columns on this index so the read path is
        // covered. That is declared in create-adsmparam.sql rather than here, because SQLite has no
        // equivalent and EF Core would emit nothing for it anyway.
        builder.HasIndex(e => new { e.CompanyCode, e.ModuleCode, e.ParamKey })
            .HasDatabaseName("IX_AdSmParam_Lookup");
    }
}
