namespace ErpWeb.Core.Purchase;

public sealed class PoOrderOptions
{
    public const string SectionName = "Purchase:PoOrder";

    /// <summary>0 = item master only; 2 = vendor-item only; 3 = vendor-item then item master.</summary>
    public int SupplierPrice { get; set; }

    public bool UseWeight { get; set; }

    public bool PurchaseItemTaxInclusive { get; set; }

    public int PurchaseTaxDec { get; set; } = 2;

    public int POPriceDecimal { get; set; } = 6;

    public bool POControlIcode { get; set; }

    public bool SelfViewEdit { get; set; }

    public int DraftAttachmentTtlHours { get; set; } = 24;
}
