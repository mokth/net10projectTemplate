namespace ErpWeb.Model.Entities;

public enum CostingMethod
{
    MOVING_AVG = 1
}

public class Item : SoftDeletableCompanyEntity
{
    public string ItemCode { get; set; } = null!;
    public string ItemDescription { get; set; } = null!;
    public long BaseUOMId { get; set; }
    public bool IsStockItem { get; set; } = true;
    public bool IsBatchItem { get; set; }
    public CostingMethod CostingMethod { get; set; } = CostingMethod.MOVING_AVG;
    public decimal MinStockQty { get; set; }
    public decimal MaxStockQty { get; set; }
    public decimal ReorderQty { get; set; }
    public string? TaxCode { get; set; }
    public bool IsActive { get; set; } = true;
}

public class ItemVariant : SoftDeletableCompanyEntity
{
    public long ItemId { get; set; }
    public string SKU { get; set; } = null!;
    public string? Barcode { get; set; }
    public string? VariantDescription { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
}

public class UOM : SoftDeletableCompanyEntity
{
    public string UOMCode { get; set; } = null!;
    public string UOMName { get; set; } = null!;
    public int DecimalPlaces { get; set; } = 4;
    public bool IsActive { get; set; } = true;
}

public class UOMConversion : SoftDeletableCompanyEntity
{
    public long ItemId { get; set; }
    public long FromUOMId { get; set; }
    public long ToUOMId { get; set; }
    public decimal ConversionRate { get; set; }
}

public class Warehouse : SoftDeletableBranchEntity
{
    public string WarehouseCode { get; set; } = null!;
    public string WarehouseName { get; set; } = null!;
    public bool IsActive { get; set; } = true;
}

public class WarehouseLocation : SoftDeletableBranchEntity
{
    public long WarehouseId { get; set; }
    public string LocationCode { get; set; } = null!;
    public string? LocationName { get; set; }
    public bool IsActive { get; set; } = true;
}

public class ReasonCode : SoftDeletableCompanyEntity
{
    public string ReasonCodeValue { get; set; } = null!;
    public string ReasonName { get; set; } = null!;
    public string AppliesTo { get; set; } = null!;
    public bool IsActive { get; set; } = true;
}
