namespace ErpWeb.Model.Entities.Planning;

/// <summary>Work-order / schedule header snapshot (<c>dbo.PrSchMas</c>).</summary>
public class PrSchMa
{
    public string ScheCode { get; set; } = string.Empty;
    public short RelNo { get; set; }
    public string? Status { get; set; }
    public string? DrNo { get; set; }
    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }
    public double? StdBatchSize { get; set; }
    public double? ScheQty { get; set; }
    public string? StdUom { get; set; }
    public bool? Active { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? CompletedDate { get; set; }
    public DateTime? Created { get; set; }
    public DateTime? Updated { get; set; }
    public string? UserId { get; set; }
    public double? ConsignQty { get; set; }
    public double? DesireQty { get; set; }
    public double? DeliveryQty { get; set; }
    public string? Remarks { get; set; }
    public bool? FinalIssue { get; set; }
    public int? Release { get; set; }
    public string? UpdatedUid { get; set; }
    public DateTime? TransactionDate { get; set; }
    public string? ImageUrl { get; set; }
    public bool? StartFromStartDate { get; set; }

    public ICollection<PrSchBom> PrSchBoms { get; set; } = new List<PrSchBom>();
    public ICollection<PrSchLabour> PrSchLabours { get; set; } = new List<PrSchLabour>();
    public ICollection<PrSchMachine> PrSchMachines { get; set; } = new List<PrSchMachine>();
    public ICollection<PrSchProcess> PrSchProcesses { get; set; } = new List<PrSchProcess>();
    public ICollection<PrSchWcenter> PrSchWcenters { get; set; } = new List<PrSchWcenter>();
}
