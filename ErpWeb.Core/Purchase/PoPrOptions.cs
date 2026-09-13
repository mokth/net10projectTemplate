namespace ErpWeb.Core.Purchase;

public sealed class PoPrOptions
{
    public const string SectionName = "Purchase:PoPr";

    /// <summary>0 = item master only; 2 = vendor-item only; 3 = vendor-item then item master.</summary>
    public int SupplierPrice { get; set; }

    public bool UseWeight { get; set; }

    public bool PurchaseItemTaxInclusive { get; set; }

    public int PurchaseTaxDec { get; set; } = 2;

    public bool POControlIcode { get; set; }

    public bool SelfViewEdit { get; set; }

    public int DraftAttachmentTtlHours { get; set; } = 24;
}
