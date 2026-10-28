using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public sealed class IvStockMasterLifecycleEventConfiguration
    : IEntityTypeConfiguration<IvStockMasterLifecycleEvent>
{
    public void Configure(EntityTypeBuilder<IvStockMasterLifecycleEvent> builder)
    {
        builder.ToTable("IvStockMasterLifecycleEvent", table =>
        {
            table.HasCheckConstraint(
                "CK_IvStockMasterLifecycleEvent_Status",
                "[OldStatus] IN ('ACTIVE','DISCONTINUED','INACTIVE') AND [NewStatus] IN ('ACTIVE','DISCONTINUED','INACTIVE')");
            table.HasCheckConstraint(
                "CK_IvStockMasterLifecycleEvent_Change",
                "[OldStatus] <> [NewStatus]");
            table.HasCheckConstraint(
                "CK_IvStockMasterLifecycleEvent_Source",
                "[SourceAction] IN ('ITEM_FORM','BULK','SYSTEM')");
            table.HasCheckConstraint(
                "CK_IvStockMasterLifecycleEvent_Reason",
                "LEN(LTRIM(RTRIM([Reason]))) > 0");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.ICode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.OldStatus).HasMaxLength(16).IsRequired();
        builder.Property(x => x.NewStatus).HasMaxLength(16).IsRequired();
        builder.Property(x => x.OccurredAtUtc).HasColumnType("datetime2").IsRequired();
        builder.Property(x => x.ActorUserId).HasMaxLength(10).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(500).IsRequired();
        builder.Property(x => x.SourceAction).HasMaxLength(20).IsRequired();
        builder.Property(x => x.CorrelationId).HasMaxLength(64);

        // No relationship to IvStockMaster: the audit must outlive any item deletion.
        builder.HasIndex(x => new { x.CompanyCode, x.ICode, x.OccurredAtUtc })
            .HasDatabaseName("IX_IvStockMasterLifecycleEvent_Company_Item_Time");
    }
}
