using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations.Production;

/// <summary>Storage contracts shared by the two Production stock document sidecars.</summary>
internal static class ProductionStockTransactionMapping
{
    internal static void Header<T>(EntityTypeBuilder<T> b) where T : class
    {
        b.HasKey("BatchId");
        b.Property<int>("BatchId").ValueGeneratedNever();
        b.Property<string>("CompanyCode").HasMaxLength(5).IsRequired();
        b.Property<string>("BranchCode").HasMaxLength(5).IsRequired();
        b.Property<string>("SnapshotHash").HasMaxLength(64).IsUnicode(false).IsRequired();
        b.Property<byte[]>("RowVersion").IsRowVersion();
        b.Property<string>("ReversalReason").HasMaxLength(500);
        b.Property<string>("DeletedBy").HasMaxLength(10);
        b.Property<string>("DeleteReason").HasMaxLength(250);
        b.Property<DateTime?>("DeletedAtUtc").HasColumnType("datetime2(7)");
        b.HasIndex("CompanyCode", "BranchCode", "WorkOrderId");
        b.HasIndex("PostingId").IsUnique().HasFilter("[PostingId] IS NOT NULL");
        b.HasIndex("ReversalPostingId").IsUnique().HasFilter("[ReversalPostingId] IS NOT NULL");
    }

    internal const string HeaderCheck = "[DocumentRevision] > 0 AND [SnapshotRevision] > 0 AND LEN([SnapshotHash]) = 64 "
        + "AND ([ReversalPostingId] IS NULL OR ([PostingId] IS NOT NULL AND [ReversalPostingId] <> [PostingId]))";

    internal static void Line<T>(EntityTypeBuilder<T> b) where T : class
    {
        b.HasKey("Id");
        b.Property<decimal>("BaseQty").HasPrecision(18, 4);
        b.Property<decimal>("SourceFactor").HasPrecision(18, 8);
        b.Property<string>("SourceUom").HasMaxLength(10).IsRequired();
        b.Property<string>("BaseUom").HasMaxLength(10).IsRequired();
        b.HasIndex("DetailId").IsUnique();
        b.Property<int>("DocumentRevision").HasDefaultValue(1);
        b.HasIndex("BatchId", "DocumentRevision", "ProductionBalLotId").IsUnique();
    }

    internal static void Fact<T>(EntityTypeBuilder<T> b, string lineId) where T : class
    {
        b.HasKey("Id");
        b.Property<decimal>("BaseQty").HasPrecision(18, 4);
        b.Property<decimal>("TotalValue").HasPrecision(19, 6);
        b.HasIndex("StockPostingId", lineId).IsUnique();
        b.HasIndex("ProductionMovementId").IsUnique();
        b.HasIndex("ReversesFactId").IsUnique().HasFilter("[ReversesFactId] IS NOT NULL");
    }

    internal static void Approval<T>(EntityTypeBuilder<T> b) where T : class
    {
        b.Property<string>("CostEvidenceType").HasMaxLength(30);
        b.Property<string>("CostOverrideReason").HasMaxLength(250);
        b.Property<string>("CostApprovedBy").HasMaxLength(10);
        b.Property<DateTime?>("CostApprovedAtUtc").HasColumnType("datetime2(7)");
    }
}
