namespace ErpWeb.Model.Entities.Inventory;

/// <summary>Approved item-specific UOM conversion. Reverse conversion uses the same quantity pair.</summary>
public sealed class IvItemUomConversion
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string ItemCode { get; set; } = string.Empty;
    public IvStockMaster? Item { get; set; }
    public string FromUom { get; set; } = string.Empty;
    public string ToUom { get; set; } = string.Empty;
    public decimal FromQty { get; set; } = 1m;
    public decimal ToQty { get; set; } = 1m;
    public int RoundingScale { get; set; } = 4;
    public string RoundingMode { get; set; } = IvUomRoundingModes.AwayFromZero;
    public bool IsActive { get; set; } = true;
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public static class IvUomRoundingModes
{
    public const string AwayFromZero = "AWAY_FROM_ZERO";
    public const string ToEven = "TO_EVEN";
    public const string Ceiling = "CEILING";
    public const string Floor = "FLOOR";
}
