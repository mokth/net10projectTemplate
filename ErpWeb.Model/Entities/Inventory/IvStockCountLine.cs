namespace ErpWeb.Model.Entities.Inventory;

/// <summary>
/// One countable pile (<see cref="IvBalLoc"/>) on a stock-count sheet.
///
/// <see cref="SystemQty"/> is the on-hand quantity at Generate time and is EVIDENCE ONLY: the
/// adjustment delta is computed at post against the live <c>IvBalLoc.StdQty</c>, never against
/// this column. <see cref="SnapshotUnitPrice"/> is evidence for the same reason — posting resolves
/// its own price from the locked balance.
///
/// Immutability: <see cref="PhysicalQty"/> may be written only while the header is DRAFT or
/// ROLLED_BACK. After POSTED nothing on this row is mutated — not by rollback, not by Recover.
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

    /// <summary>Live quantity at Generate — evidence, never a posting input.</summary>
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
