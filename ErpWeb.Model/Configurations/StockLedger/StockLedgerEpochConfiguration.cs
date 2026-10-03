using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.StockLedger;

public sealed class StockLedgerEpochConfiguration : IEntityTypeConfiguration<StockLedgerEpoch>
{
    public void Configure(EntityTypeBuilder<StockLedgerEpoch> builder)
    {
        builder.ToTable("StockLedgerEpoch", table =>
        {
            table.HasCheckConstraint("CK_StockLedgerEpoch_Status", "[Status] IN ('PREPARED','ACTIVE','RETIRED')");
            table.HasCheckConstraint("CK_StockLedgerEpoch_Version", "[Version] >= 2");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.EffectiveFrom).HasColumnType("datetime2(7)");
        builder.Property(x => x.Status).HasMaxLength(10).IsRequired();
        builder.Property(x => x.ReconciliationManifestHash).HasColumnType("char(64)").IsRequired();
        builder.Property(x => x.ActivatedAtUtc).HasColumnType("datetime2(7)");
        builder.Property(x => x.ActivatedBy).HasMaxLength(100);

        builder.HasAlternateKey(x => new { x.CompanyCode, x.BranchCode, x.Id })
            .HasName("AK_StockLedgerEpoch_Tenant_Id");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.Status })
            .IsUnique()
            .HasFilter("[Status] = N'ACTIVE'")
            .HasDatabaseName("UQ_StockLedgerEpoch_ActiveBranch");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.MigrationBatchId })
            .IsUnique()
            .HasDatabaseName("UQ_StockLedgerEpoch_MigrationBatch");
    }
}
