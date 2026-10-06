using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Sales;

public sealed class SalesReturnCostAllocationConfiguration
    : IEntityTypeConfiguration<SalesReturnCostAllocation>
{
    public void Configure(EntityTypeBuilder<SalesReturnCostAllocation> builder)
    {
        builder.ToTable("SalesReturnCostAllocation", table =>
        {
            table.HasCheckConstraint("CK_SalesReturnCostAllocation_Qty",
                "[ReturnedBaseQty] > 0");
            table.HasCheckConstraint("CK_SalesReturnCostAllocation_Amount",
                "[ReturnedCostAmount] >= 0");
            table.HasCheckConstraint("CK_SalesReturnCostAllocation_Line",
                "[ReturnDocumentLine] > 0 AND [ReturnCostingRevision] >= 0");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        builder.Property(x => x.ReturnDocumentType).HasMaxLength(40).IsRequired();
        builder.Property(x => x.ReturnDocumentNo).HasMaxLength(50).IsRequired();
        builder.Property(x => x.OriginalOwnerType).HasMaxLength(40).IsRequired();
        builder.Property(x => x.OriginalOwnerDocumentNo).HasMaxLength(50).IsRequired();
        builder.Property(x => x.OriginalOwnerDocumentLine).HasMaxLength(50);
        builder.Property(x => x.ReturnedBaseQty).HasPrecision(19, 6);
        builder.Property(x => x.ReturnedCostAmount).HasPrecision(19, 6);
        builder.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(7)");
        builder.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();

        builder.HasOne(x => x.OriginalValuationFact)
            .WithMany()
            .HasForeignKey(x => x.OriginalValuationFactId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ReturnValuationFact)
            .WithMany()
            .HasForeignKey(x => x.ReturnValuationFactId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.ReversesAllocation)
            .WithMany()
            .HasForeignKey(x => x.ReversesAllocationId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ErpWeb.Model.Entities.StockLedger.StockPosting>()
            .WithMany()
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.StockPostingId })
            .HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new
        {
            x.CompanyCode,
            x.BranchCode,
            x.ReturnDocumentType,
            x.ReturnDocumentNo,
            x.ReturnCostingRevision,
            x.ReturnDocumentLine
        }).HasDatabaseName("IX_SalesReturnCostAllocation_Return");
        builder.HasIndex(x => new
        {
            x.CompanyCode,
            x.BranchCode,
            x.OriginalValuationFactId
        }).HasDatabaseName("IX_SalesReturnCostAllocation_Original");
        builder.HasIndex(x => x.ReversesAllocationId)
            .IsUnique()
            .HasFilter("[ReversesAllocationId] IS NOT NULL")
            .HasDatabaseName("UX_SalesReturnCostAllocation_Reversal");
    }
}
