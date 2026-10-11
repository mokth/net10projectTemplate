namespace ErpWeb.Model.Entities.Purchase;

/// <summary>Append-only supplier contact note tied to one PO revision and line.</summary>
public sealed class PoDeliveryFollowUp
{
    public long Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string PoNo { get; set; } = string.Empty;
    public short PoRelNo { get; set; }
    public short PoLineNo { get; set; }
    public DateTime ContactAtUtc { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public DateOnly? SupplierStatedDate { get; set; }
    public DateOnly? NextFollowUpDate { get; set; }
    public string? Note { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}
