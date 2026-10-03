using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Production;

public sealed class ProductionMovementAllocationConfiguration : IEntityTypeConfiguration<ProductionMovementAllocation>
{
    public void Configure(EntityTypeBuilder<ProductionMovementAllocation> builder)
    {
        builder.ToTable("PrProductionMovementAllocation", table =>
        {
            table.UseSqlOutputClause(false);
            table.HasCheckConstraint("CK_PrProductionMovementAllocation_Qty", "[BaseQty] > 0");
            table.HasCheckConstraint("CK_PrProductionMovementAllocation_Distinct",
                "[ReceiptMovementId] <> [OutboundMovementId]");
            table.HasCheckConstraint("CK_PrProductionMovementAllocation_NoSelfReverse",
                "[ReversesAllocationId] IS NULL OR [ReversesAllocationId] <> [Id]");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BaseQty).HasPrecision(18, 4);
        builder.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(7)");

        builder.HasOne(x => x.StockPosting).WithMany()
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.StockPostingId })
            .HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ReceiptMovement).WithMany()
            .HasForeignKey(x => x.ReceiptMovementId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.OutboundMovement).WithMany()
            .HasForeignKey(x => x.OutboundMovementId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ReversesAllocation).WithMany()
            .HasForeignKey(x => x.ReversesAllocationId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.StockPostingId, x.ReceiptMovementId, x.OutboundMovementId })
            .IsUnique().HasFilter("[ReversesAllocationId] IS NULL")
            .HasDatabaseName("UQ_PrProductionMovementAllocation_Normal");
        builder.HasIndex(x => x.ReversesAllocationId)
            .IsUnique().HasFilter("[ReversesAllocationId] IS NOT NULL")
            .HasDatabaseName("UQ_PrProductionMovementAllocation_Reversal");
    }
}
