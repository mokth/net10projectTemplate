using ErpWeb.Core.Costing;
using ErpWeb.Core.Inventory;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.UI.Inventory.Inquiry;

public sealed record CostingSourceLink(string Label, string Route);

public static class CostingSourceNavigationResolver
{
    public static CostingSourceLink? Resolve(CostingRepairOwnershipResult ownership)
    {
        var owner = ownership.Owner;
        if (owner is null || !ownership.IsProven)
            return null;

        var batchNo = owner.PhysicalSourceDocumentNo;
        var sourceId = owner.PhysicalSourceDocumentId;
        return owner.OwnerType switch
        {
            CostingRepairOwnerTypes.MiscReceipt => BatchLink("Misc Receipt", "/inventory/misc-receipt/view/", batchNo),
            CostingRepairOwnerTypes.MiscIssue => BatchLink("Misc Issue", "/inventory/misc-issue/view/", batchNo),
            CostingRepairOwnerTypes.Scrap => BatchLink("Scrap", "/inventory/scrap/view/", batchNo),
            CostingRepairOwnerTypes.VendorReturn => BatchLink("Vendor Return", "/inventory/vendor-return/view/", batchNo),
            CostingRepairOwnerTypes.Transfer => BatchLink("Stock Transfer", "/inventory/stock-transfer/view/", batchNo),
            CostingRepairOwnerTypes.Adjustment => BatchLink("Stock Adjustment", "/inventory/stock-adjustment/view/", batchNo),
            CostingRepairOwnerTypes.CustomerReturn => BatchLink("Customer Return", "/inventory/stock-return/view/", batchNo),
            CostingRepairOwnerTypes.PurchaseGoodsReceipt => BatchLink("Goods Receipt", "/inventory/goods-receipts/view/", batchNo),
            CostingRepairOwnerTypes.SalesInvoice => DocumentLink("Sales Invoice", "/sales/invoices/view/", owner.OwnerDocumentNo),
            CostingRepairOwnerTypes.SalesDeliveryOrder => DocumentLink("Delivery Order", "/sales/delivery-orders/view/", owner.OwnerDocumentNo),
            CostingRepairOwnerTypes.SalesCreditNote => DocumentLink("Sales Credit Note", "/sales/credit-notes/view/", owner.OwnerDocumentNo),
            CostingRepairOwnerTypes.PurchaseCreditNote => DocumentLink("Purchase Credit Note", "/purchase/credit-notes/view/", owner.OwnerDocumentNo),
            CostingRepairOwnerTypes.ProductionMaterialIssue => BatchLink("Material Issue", "/planning/material-issues/view/", batchNo),
            CostingRepairOwnerTypes.ProductionFinishedGood => IdLink("Finished Good Receipt", "/planning/finished-good-receipts/", sourceId, "/view"),
            CostingRepairOwnerTypes.ProductionOutput => IdLink("Daily Production", "/planning/daily-production/view/", sourceId, null),
            _ => PhysicalLink(owner.PhysicalSourceDocumentType, batchNo, sourceId, owner.OwnerDocumentNo)
        };
    }

    public static CostingSourceLink? PhysicalLink(string? sourceType, string? sourceNo, string? sourceId, string? ownerNo)
    {
        var type = sourceType?.Trim() ?? string.Empty;
        if (string.Equals(type, IvTrxTypes.MiscellaneousReceipt, StringComparison.OrdinalIgnoreCase))
            return BatchLink("Misc Receipt", "/inventory/misc-receipt/view/", sourceNo);
        if (string.Equals(type, IvTrxTypes.MiscellaneousIssue, StringComparison.OrdinalIgnoreCase))
            return BatchLink("Misc Issue", "/inventory/misc-issue/view/", sourceNo);
        if (string.Equals(type, IvTrxTypes.Scrap, StringComparison.OrdinalIgnoreCase))
            return BatchLink("Scrap", "/inventory/scrap/view/", sourceNo);
        if (string.Equals(type, IvTrxTypes.VendorReturn, StringComparison.OrdinalIgnoreCase))
            return BatchLink("Vendor Return", "/inventory/vendor-return/view/", sourceNo);
        if (string.Equals(type, IvTrxTypes.StockTransfer, StringComparison.OrdinalIgnoreCase))
            return BatchLink("Stock Transfer", "/inventory/stock-transfer/view/", sourceNo);
        if (string.Equals(type, IvTrxTypes.StockAdjustment, StringComparison.OrdinalIgnoreCase))
            return BatchLink("Stock Adjustment", "/inventory/stock-adjustment/view/", sourceNo);
        if (string.Equals(type, IvTrxTypes.CustomerReturn, StringComparison.OrdinalIgnoreCase))
            return BatchLink("Customer Return", "/inventory/stock-return/view/", sourceNo);
        if (string.Equals(type, IvTrxTypes.GoodsReceive, StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, IvTrxTypes.NonStockGoodsReceive, StringComparison.OrdinalIgnoreCase))
            return BatchLink("Goods Receipt", "/inventory/goods-receipts/view/", sourceNo);
        if (string.Equals(type, IvTrxTypes.IssueToProduction, StringComparison.OrdinalIgnoreCase))
            return BatchLink("Material Issue", "/planning/material-issues/view/", sourceNo);
        if (string.Equals(type, ProductionDocumentTypes.FinishedGoodReceipt, StringComparison.OrdinalIgnoreCase))
            return IdLink("Finished Good Receipt", "/planning/finished-good-receipts/", sourceId, "/view");
        if (string.Equals(type, ProductionDocumentTypes.ProductionOutput, StringComparison.OrdinalIgnoreCase))
            return IdLink("Daily Production", "/planning/daily-production/view/", sourceId, null);
        return string.IsNullOrWhiteSpace(ownerNo) ? null : null;
    }

    private static CostingSourceLink? BatchLink(string label, string prefix, string? batchNo)
    {
        if (!int.TryParse(batchNo, out var number))
            return null;
        return new CostingSourceLink($"{label} {number}", $"{prefix}{number}");
    }

    private static CostingSourceLink? DocumentLink(string label, string prefix, string? documentNo)
    {
        if (string.IsNullOrWhiteSpace(documentNo))
            return null;
        var no = documentNo.Trim();
        return new CostingSourceLink($"{label} {no}", $"{prefix}{Uri.EscapeDataString(no)}");
    }

    private static CostingSourceLink? IdLink(string label, string prefix, string? id, string? suffix)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        return new CostingSourceLink($"{label} {id.Trim()}", $"{prefix}{id.Trim()}{suffix}");
    }
}
