namespace ErpWeb.Model.Entities.StockLedger;

/// <summary>
/// Effective-dated approved standard cost for one company/branch/item. Revisions are append-only
/// evidence; a later revision supersedes the earlier range instead of rewriting posted facts.
/// </summary>
public sealed class ItemStandardCostRevision
{
    public long Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string ItemCode { get; set; } = string.Empty;

    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }

    public decimal MaterialCost { get; set; }
    public decimal LabourCost { get; set; }
    public decimal MachineCost { get; set; }
    public decimal OverheadCost { get; set; }
    public decimal SubcontractCost { get; set; }
    public decimal TotalStandardCost { get; set; }

    public string Status { get; set; } = ItemStandardCostRevisionStatuses.Approved;
    public int Revision { get; set; }
    public string ApprovedBy { get; set; } = string.Empty;
    public DateTime ApprovedAtUtc { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public byte[] RowVersion { get; set; } = [];
}

public static class ItemStandardCostRevisionStatuses
{
    public const string Approved = "APPROVED";
    public const string Superseded = "SUPERSEDED";
}
