using ErpWeb.Core.Inventory;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Core.Costing;

/// <summary>
/// Decides the authoritative repair owner from stored evidence. A raw inventory
/// transaction type is not enough: sales and purchase documents post through shared batches.
/// </summary>
public static class CostingRepairOwnershipRules
{
    public static CostingRepairOwnershipResult Resolve(CostingRepairEvidence evidence)
    {
        var physical = evidence.PhysicalSourceDocumentType.Trim();

        if (evidence.ForceClosed && string.Equals(physical, IvTrxTypes.SalesOut, StringComparison.OrdinalIgnoreCase))
        {
            return Unproven("Force Close is a lifecycle tombstone. It is not a costing rollback or a repost candidate.", evidence);
        }

        if (string.Equals(physical, IvTrxTypes.SalesOut, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(evidence.DoNo))
                return Proven(CostingRepairOwnerTypes.SalesDeliveryOrder, evidence.DoNo.Trim(), evidence);
            if (!string.IsNullOrWhiteSpace(evidence.InvNo))
                return Proven(CostingRepairOwnerTypes.SalesInvoice, evidence.InvNo.Trim(), evidence);
            return Unproven("Sales-out posting has no delivery-order number or invoice number.", evidence);
        }

        if (string.Equals(physical, IvTrxTypes.CustomerReturn, StringComparison.OrdinalIgnoreCase))
        {
            if (evidence.SalesCreditNoteProven && !string.IsNullOrWhiteSpace(evidence.SalesCreditNoteNo))
                return Proven(CostingRepairOwnerTypes.SalesCreditNote, evidence.SalesCreditNoteNo.Trim(), evidence);
            return Proven(CostingRepairOwnerTypes.CustomerReturn, evidence.PhysicalSourceDocumentNo, evidence);
        }

        if (string.Equals(physical, IvTrxTypes.VendorReturn, StringComparison.OrdinalIgnoreCase))
        {
            if (evidence.PurchaseCreditNoteProven && !string.IsNullOrWhiteSpace(evidence.PurchaseCreditNoteNo))
                return Proven(CostingRepairOwnerTypes.PurchaseCreditNote, evidence.PurchaseCreditNoteNo.Trim(), evidence);
            return Proven(CostingRepairOwnerTypes.VendorReturn, evidence.PhysicalSourceDocumentNo, evidence);
        }

        if (string.Equals(physical, IvTrxTypes.GoodsReceive, StringComparison.OrdinalIgnoreCase)
            || string.Equals(physical, IvTrxTypes.NonStockGoodsReceive, StringComparison.OrdinalIgnoreCase))
        {
            return Proven(CostingRepairOwnerTypes.PurchaseGoodsReceipt, evidence.PhysicalSourceDocumentNo, evidence);
        }

        if (evidence.ProductionLinkProven)
        {
            var productionType = evidence.ProductionDocumentType?.Trim();
            var productionNo = string.IsNullOrWhiteSpace(evidence.ProductionDocumentNo)
                ? evidence.PhysicalSourceDocumentNo
                : evidence.ProductionDocumentNo.Trim();
            if (string.Equals(physical, ProductionDocumentTypes.FinishedGoodReceipt, StringComparison.OrdinalIgnoreCase)
                || string.Equals(productionType, ProductionDocumentTypes.FinishedGoodReceipt, StringComparison.OrdinalIgnoreCase))
                return Proven(CostingRepairOwnerTypes.ProductionFinishedGood, productionNo, evidence);
            if (string.Equals(physical, ProductionDocumentTypes.ProductionOutput, StringComparison.OrdinalIgnoreCase)
                || string.Equals(productionType, ProductionDocumentTypes.ProductionOutput, StringComparison.OrdinalIgnoreCase))
                return Proven(CostingRepairOwnerTypes.ProductionOutput, productionNo, evidence);
            if (string.Equals(physical, IvTrxTypes.IssueToProduction, StringComparison.OrdinalIgnoreCase)
                || string.Equals(productionType, ProductionDocumentTypes.MaterialIssue, StringComparison.OrdinalIgnoreCase))
                return Proven(CostingRepairOwnerTypes.ProductionMaterialIssue, productionNo, evidence);
        }

        if (string.Equals(physical, IvTrxTypes.IssueToProduction, StringComparison.OrdinalIgnoreCase))
            return Unproven("Issue to Production has no production posting link.", evidence);

        if (string.Equals(physical, IvTrxTypes.FinishedGoods, StringComparison.OrdinalIgnoreCase)
            || string.Equals(physical, ProductionDocumentTypes.FinishedGoodReceipt, StringComparison.OrdinalIgnoreCase))
            return Unproven("Finished-good receipt has no production posting link.", evidence);

        if (string.Equals(physical, ProductionDocumentTypes.ProductionOutput, StringComparison.OrdinalIgnoreCase))
            return Unproven("Daily Production has no production posting link.", evidence);

        var direct = physical.ToUpperInvariant() switch
        {
            IvTrxTypes.MiscellaneousReceipt => CostingRepairOwnerTypes.MiscReceipt,
            IvTrxTypes.MiscellaneousIssue => CostingRepairOwnerTypes.MiscIssue,
            IvTrxTypes.Scrap => CostingRepairOwnerTypes.Scrap,
            IvTrxTypes.StockTransfer => CostingRepairOwnerTypes.Transfer,
            IvTrxTypes.StockAdjustment => CostingRepairOwnerTypes.Adjustment,
            _ => null
        };
        return direct is null
            ? Unproven($"No authoritative repair owner is proven for physical type '{physical}'.", evidence)
            : Proven(direct, evidence.PhysicalSourceDocumentNo, evidence);
    }

    private static CostingRepairOwnershipResult Proven(string ownerType, string documentNo, CostingRepairEvidence evidence) =>
        new(
            true,
            null,
            new CostingRepairOwner(
                ownerType,
                documentNo,
                evidence.PhysicalSourceDocumentType,
                evidence.PhysicalSourceDocumentId,
                evidence.PhysicalSourceDocumentNo,
                evidence.StockPostingId));

    private static CostingRepairOwnershipResult Unproven(string reason, CostingRepairEvidence evidence) =>
        new(
            false,
            reason,
            new CostingRepairOwner(
                string.Empty,
                evidence.PhysicalSourceDocumentNo,
                evidence.PhysicalSourceDocumentType,
                evidence.PhysicalSourceDocumentId,
                evidence.PhysicalSourceDocumentNo,
                evidence.StockPostingId));
}
