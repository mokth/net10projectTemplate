namespace ErpWeb.Model.Entities.Inventory;

/// <summary>
/// One countable pile (<see cref="IvBalLoc"/>) on a stock-count sheet.
///
/// <see cref="SystemQty"/> and <see cref="SnapshotUnitPrice"/> are display evidence, not cost or
/// adjustment authority. A replay-aware count derives its adjustment from the historical slice
/// immediately before the end-of-day count event.
///
/// Once any posted generation exists, <see cref="PhysicalQty"/> is immutable even after rollback.
/// </summary>
public class IvStockCountLine
{
    public int Id { get; set; }

    /// <summary>FK to <see cref="IvStockCountHdr.Id"/>. Restrict, never cascade.</summary>
    public int StockCountId { get; set; }

    public short LineNumber { get; set; }

    /// <summary>The pile. The only posting identity the ADJ engine needs.</summary>
    public int BalLocId { get; set; }

    // ── Denormalised display / slice-match columns (read from the balance at Generate) ────────────

    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }
    public string? WHCode { get; set; }
    public string? LocCode { get; set; }
    public string? LotNo { get; set; }
    public string IStatus { get; set; } = string.Empty;
    public string? IClassCode { get; set; }
    public string? StdUom { get; set; }
    public DateTime? ExpiryDate { get; set; }

    /// <summary>Quantity at Generate — evidence, never a posting input.</summary>
    public decimal SystemQty { get; set; }

    /// <summary>Counted quantity. NULL = not counted, which excludes the line from posting.</summary>
    public decimal? PhysicalQty { get; set; }

    /// <summary>
    /// Evidence only, never a posting input. Filled from the shared on-hand projection's
    /// <c>PurchasePrice</c>; posting resolves its own price from the locked balance.
    /// </summary>
    public decimal? SnapshotUnitPrice { get; set; }

    public short RecountCount { get; set; }

    public string? CountedBy { get; set; }
    public DateTime? CountedOn { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public IvStockCountHdr Header { get; set; } = null!;
}
