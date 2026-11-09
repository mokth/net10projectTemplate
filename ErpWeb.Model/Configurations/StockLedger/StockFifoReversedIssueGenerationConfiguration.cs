using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.StockLedger;

public sealed class StockFifoReversedIssueGenerationConfiguration : IEntityTypeConfiguration<StockFifoReversedIssueGeneration>
{
    public void Configure(EntityTypeBuilder<StockFifoReversedIssueGeneration> b)
    {
        b.ToTable("StockFifoReversedIssueGeneration", t =>
        {
            t.UseSqlOutputClause(false);
            t.HasCheckConstraint("CK_FifoReversedIssueGeneration_Qty", "[BaseQty] > 0 AND [TotalCost] >= 0");
            t.HasCheckConstraint("CK_FifoReversedIssueGeneration_Identity",
                "[IssueHistoryId] <> [ReversalHistoryId] AND [IssueMovementId] <> [ReversalMovementId] AND ([PreviousGenerationId] IS NULL OR [PreviousGenerationId] <> [Id])");
        });
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.CompanyCode, x.BranchCode, x.Id });
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        b.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        b.Property(x => x.BaseQty).HasPrecision(19, 6);
        b.Property(x => x.TotalCost).HasPrecision(19, 6);
        b.Property(x => x.SplitHash).HasColumnType("char(64)").IsRequired();
        b.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(7)");
        b.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
        b.HasOne<StockReplayRun>().WithMany().HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.ReplayRunId })
            .HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StockPosting>().WithMany().HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.ReplayStockPostingId })
            .HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<IvTrxHistory>().WithMany().HasForeignKey(x => x.IssueHistoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<IvTrxHistory>().WithMany().HasForeignKey(x => x.ReversalHistoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionBalLotMovement>().WithMany().HasForeignKey(x => x.IssueMovementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionBalLotMovement>().WithMany().HasForeignKey(x => x.ReversalMovementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.PreviousGeneration).WithMany().HasForeignKey(x => x.PreviousGenerationId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.IssueHistoryId, x.ReplayStockPostingId }).IsUnique();
        b.HasIndex(x => x.PreviousGenerationId).IsUnique().HasFilter("[PreviousGenerationId] IS NOT NULL");
        b.HasIndex(x => x.IssueHistoryId).IsUnique().HasFilter("[PreviousGenerationId] IS NULL")
            .HasDatabaseName("UQ_FifoReversedIssueGeneration_First");
    }
}

public sealed class StockFifoReversedIssueSplitConfiguration : IEntityTypeConfiguration<StockFifoReversedIssueSplit>
{
    public void Configure(EntityTypeBuilder<StockFifoReversedIssueSplit> b)
    {
        b.ToTable("StockFifoReversedIssueSplit", t =>
        {
            t.UseSqlOutputClause(false);
            t.HasCheckConstraint("CK_FifoReversedIssueSplit_Values", "[SplitOrdinal] >= 0 AND [ConsumedQty] > 0 AND [ConsumedValue] >= 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.CompanyCode).HasMaxLength(5).IsRequired();
        b.Property(x => x.BranchCode).HasMaxLength(5).IsRequired();
        b.Property(x => x.ConsumedQty).HasPrecision(19, 6);
        b.Property(x => x.ConsumedValue).HasPrecision(19, 6);
        b.HasOne(x => x.Generation).WithMany(x => x.Splits)
            .HasForeignKey(x => new { x.CompanyCode, x.BranchCode, x.GenerationId })
            .HasPrincipalKey(x => new { x.CompanyCode, x.BranchCode, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.FifoLayer).WithMany().HasForeignKey(x => x.FifoLayerId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.GenerationId, x.SplitOrdinal }).IsUnique();
        b.HasIndex(x => new { x.GenerationId, x.FifoLayerId }).IsUnique();
    }
}
