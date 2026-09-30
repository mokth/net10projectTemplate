namespace ErpWeb.Model.Entities.Planning;

/// <summary>Product-definition master (<c>dbo.PrDefMas</c>).</summary>
public class PrDefMa
{
    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }
    public double? StdBatchSize { get; set; }
    public string? StdUom { get; set; }
    public bool? Active { get; set; }
    public double? TotalTime { get; set; }
    public DateTime? Created { get; set; }
    public DateTime? Updated { get; set; }
    public string? UserId { get; set; }
    public string? UpdatedUid { get; set; }
    public string? CompCode { get; set; }
    public string? BranchCode { get; set; }
    public string? LocCode { get; set; }
    public string? Remark { get; set; }
    public string? Prefix { get; set; }
    public string? DrNo { get; set; }
    public string? ActPrdCode { get; set; }

    public ICollection<PrDefMachine> PrDefMachines { get; set; } = new List<PrDefMachine>();
    public ICollection<PrDefProcess> PrDefProcesses { get; set; } = new List<PrDefProcess>();
    public ICollection<PrDefWcenter> PrDefWcenters { get; set; } = new List<PrDefWcenter>();
}
