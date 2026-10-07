using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Production;

public sealed class ProductionFinishedGoodReceiptConfiguration :
    IEntityTypeConfiguration<ProductionFinishedGoodReceipt>, IEntityTypeConfiguration<ProductionFinishedGoodSource>,
    IEntityTypeConfiguration<ProductionFinishedGoodFact>, IEntityTypeConfiguration<ProductionFinishedGoodLotOrigin>,
    IEntityTypeConfiguration<ProductionFinishedGoodPriceSnapshot>, IEntityTypeConfiguration<ProductionPoolValuation>,
    IEntityTypeConfiguration<ProductionValuationEvidence>, IEntityTypeConfiguration<ProductionPoolDependency>
{
    private static void ConfigureProperties<T>(EntityTypeBuilder<T> b) where T : class
    {
        foreach (var p in b.Metadata.GetProperties())
        {
            if (p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?))
            {
                var isAuthorityMoney = p.Name is "TotalValue" or "TrackedValue" or "Price";
                b.Property(p.Name).HasPrecision(
                    isAuthorityMoney ? 19 : 18,
                    p.Name.Contains("Factor") ? 8 : isAuthorityMoney ? 6 : 4);
            }
            if (p.ClrType == typeof(string)) b.Property(p.Name).HasMaxLength(200);
        }
    }
    public void Configure(EntityTypeBuilder<ProductionFinishedGoodReceipt> b)
    {
        b.ToTable("PrFinishedGoodReceipt"); b.HasKey(x => x.BatchId);
        b.Property(x => x.BatchId).ValueGeneratedNever();
        b.HasOne(x => x.Batch).WithOne().HasForeignKey<ProductionFinishedGoodReceipt>(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionWorkOrder>().WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionFinishedGoodReceipt>().WithMany().HasForeignKey(x => x.CorrectedBatchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StockPosting>().WithMany().HasForeignKey(x => x.PostingId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StockPosting>().WithMany().HasForeignKey(x => x.ReversalPostingId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.CompanyCode, x.BranchCode, x.WorkOrderId });
        b.HasIndex(x => x.PostingId).IsUnique().HasFilter("[PostingId] IS NOT NULL");
        b.HasIndex(x => x.ReversalPostingId).IsUnique().HasFilter("[ReversalPostingId] IS NOT NULL");
        b.Property(x => x.RowVersion).IsRowVersion();
        ConfigureProperties(b);
        b.Property(x => x.CompanyCode).HasMaxLength(5); b.Property(x => x.BranchCode).HasMaxLength(5);
        b.Property(x => x.ReversalReason).HasMaxLength(500);
        b.Property(x => x.DeletedAtUtc).HasColumnType("datetime2");
        b.Property(x => x.DeletedBy).HasMaxLength(10);
        b.Property(x => x.DeleteReason).HasMaxLength(250);
    }
    public void Configure(EntityTypeBuilder<ProductionFinishedGoodSource> b)
    {
        b.ToTable("PrFinishedGoodSource"); b.HasKey(x => x.Id);
        b.HasOne(x => x.Receipt).WithMany(x => x.Sources).HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Detail).WithOne().HasForeignKey<ProductionFinishedGoodSource>(x => x.DetailId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionBalLot>().WithMany().HasForeignKey(x => x.ProductionBalLotId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.BatchId, x.ProductionBalLotId }); ConfigureProperties(b);
        b.Property(x => x.SourceUom).HasMaxLength(10); b.Property(x => x.DestinationUom).HasMaxLength(10); b.Property(x => x.BaseUom).HasMaxLength(10);
    }
    public void Configure(EntityTypeBuilder<ProductionFinishedGoodFact> b)
    {
        b.ToTable("PrFinishedGoodFact", t => t.UseSqlOutputClause(false)); b.HasKey(x => x.Id);
        b.HasOne<ProductionFinishedGoodReceipt>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionFinishedGoodSource>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StockPosting>().WithMany().HasForeignKey(x => x.StockPostingId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionBalLotMovement>().WithMany().HasForeignKey(x => x.ProductionMovementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<IvTrxHistory>().WithMany().HasForeignKey(x => x.InventoryHistoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<IvBalLoc>().WithMany().HasForeignKey(x => x.DestinationBalanceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionFinishedGoodFact>().WithMany().HasForeignKey(x => x.ReversesFactId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.StockPostingId, x.SourceId }).IsUnique();
        b.HasIndex(x => x.ProductionMovementId).IsUnique(); b.HasIndex(x => x.InventoryHistoryId).IsUnique();
        b.HasIndex(x => x.ReversesFactId).IsUnique().HasFilter("[ReversesFactId] IS NOT NULL"); ConfigureProperties(b);
    }
    public void Configure(EntityTypeBuilder<ProductionFinishedGoodPriceSnapshot> b)
    {
        b.ToTable("PrFinishedGoodPriceSnapshot", t => t.UseSqlOutputClause(false)); b.HasKey(x => x.Id);
        b.HasOne<StockPosting>().WithMany().HasForeignKey(x => x.StockPostingId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<IvBalLoc>().WithMany().HasForeignKey(x => x.DestinationBalanceId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.StockPostingId, x.DestinationBalanceId }).IsUnique(); ConfigureProperties(b);
    }
    public void Configure(EntityTypeBuilder<ProductionFinishedGoodLotOrigin> b)
    {
        b.ToTable("PrFinishedGoodLotOrigin", t => t.UseSqlOutputClause(false)); b.HasKey(x => x.LotId); b.Property(x => x.LotId).ValueGeneratedNever();
        b.HasOne<IvLot>().WithOne().HasForeignKey<ProductionFinishedGoodLotOrigin>(x => x.LotId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionWorkOrder>().WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionWorkOrderRouteStep>().WithMany().HasForeignKey(x => x.RouteStepId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionWorkOrderOperation>().WithMany().HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.CompanyCode, x.OriginatingBranch, x.WorkOrderId }); ConfigureProperties(b);
        b.Property(x => x.CompanyCode).HasMaxLength(5); b.Property(x => x.OriginatingBranch).HasMaxLength(5); b.Property(x => x.PhysicalLotNo).HasMaxLength(50);
    }
    public void Configure(EntityTypeBuilder<ProductionPoolValuation> b)
    {
        b.ToTable("PrPoolValuation"); b.HasKey(x => x.ProductionBalLotId); b.Property(x => x.ProductionBalLotId).ValueGeneratedNever();
        b.HasOne<ProductionBalLot>().WithOne().HasForeignKey<ProductionPoolValuation>(x => x.ProductionBalLotId).OnDelete(DeleteBehavior.Restrict); ConfigureProperties(b);
    }
    public void Configure(EntityTypeBuilder<ProductionValuationEvidence> b)
    {
        b.ToTable("PrValuationEvidence", t => t.UseSqlOutputClause(false)); b.HasKey(x => x.MovementId); b.Property(x => x.MovementId).ValueGeneratedNever();
        b.HasOne<ProductionBalLotMovement>().WithOne().HasForeignKey<ProductionValuationEvidence>(x => x.MovementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionBalLot>().WithMany().HasForeignKey(x => x.ProductionBalLotId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<IvTrxHistory>().WithMany().HasForeignKey(x => x.InventoryHistoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionBalLotMovement>().WithMany().HasForeignKey(x => x.OriginalMovementId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ProductionBalLotId, x.Generation }); ConfigureProperties(b);
    }
    public void Configure(EntityTypeBuilder<ProductionPoolDependency> b)
    {
        b.ToTable("PrPoolDependency", t => t.UseSqlOutputClause(false)); b.HasKey(x => x.Id);
        b.HasOne<ProductionBalLotMovement>().WithMany().HasForeignKey(x => x.ContributorMovementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionBalLotMovement>().WithMany().HasForeignKey(x => x.ConsumerMovementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StockPosting>().WithMany().HasForeignKey(x => x.StockPostingId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionPoolDependency>().WithMany().HasForeignKey(x => x.ReversesDependencyId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ConsumerMovementId, x.ContributorMovementId, x.StockPostingId }).IsUnique();
        b.HasIndex(x => x.ReversesDependencyId).IsUnique().HasFilter("[ReversesDependencyId] IS NOT NULL");
    }
}
