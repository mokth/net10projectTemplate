using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.StockLedger;

public sealed class ItemStandardCostRevisionConfiguration : IEntityTypeConfiguration<ItemStandardCostRevision>
{
    public void Configure(EntityTypeBuilder<ItemStandardCostRevision> builder)
    {
        builder.ToTable("ItemStandardCostRevision", table =>
        {
            table.HasCheckConstraint("CK_ItemStandardCostRevision_Range",
                "[EffectiveTo] IS NULL OR [EffectiveTo] > [EffectiveFrom]");
            table.HasCheckConstraint("CK_ItemStandardCostRevision_Costs",
                "[MaterialCost] >= 0 AND [LabourCost] >= 0 AND [MachineCost] >= 0 "
                + "AND [OverheadCost] >= 0 AND [SubcontractCost] >= 0 AND [TotalStandardCost] >= 0");
            table.HasCheckConstraint("CK_ItemStandardCostRevision_Status",
                "[Status] IN ('APPROVED','SUPERSEDED')");
            table.HasCheckConstraint("CK_ItemStandardCostRevision_Revision", "[Revision] > 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.ItemCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.EffectiveFrom).HasColumnType("date");
        builder.Property(x => x.EffectiveTo).HasColumnType("date");
        builder.Property(x => x.MaterialCost).HasPrecision(19, 6);
        builder.Property(x => x.LabourCost).HasPrecision(19, 6);
        builder.Property(x => x.MachineCost).HasPrecision(19, 6);
        builder.Property(x => x.OverheadCost).HasPrecision(19, 6);
        builder.Property(x => x.SubcontractCost).HasPrecision(19, 6);
        builder.Property(x => x.TotalStandardCost).HasPrecision(19, 6);
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ApprovedBy).HasMaxLength(100).IsRequired();
        builder.Property(x => x.ApprovedAtUtc).HasColumnType("datetime2(7)");
        builder.Property(x => x.Reason).HasMaxLength(250);
        builder.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(7)");
        builder.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.ItemCode, x.EffectiveFrom, x.Revision })
            .IsUnique().HasDatabaseName("UQ_ItemStandardCostRevision_Revision");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.ItemCode, x.EffectiveFrom })
            .HasDatabaseName("IX_ItemStandardCostRevision_EffectiveFrom");
        builder.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.ItemCode, x.Status })
            .HasDatabaseName("IX_ItemStandardCostRevision_Status");
    }
}
