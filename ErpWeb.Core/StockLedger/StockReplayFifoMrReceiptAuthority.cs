using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayFifoMrReceiptAuthority(
    int BatchId, int BatchNo, int DetailId, int DocumentRevision,
    string ItemCode, int BalLocId, DateTime EffectiveAt,
    decimal Quantity, string BaseUom, decimal UnitCost, decimal Value);

/// <summary>
/// Derives a fixed, company-base FIFO receipt cost from a single approved
/// draft MR line. A replayed receipt cannot take an arbitrary caller value.
/// </summary>
public static class StockReplayFifoMrReceiptAuthorityReader
{
    public static async Task<StockReplayFifoMrReceiptAuthority> ReadAsync(
        AppDbContext db, string companyCode, string branchCode,
        int batchNo, int balLocId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (batchNo <= 0 || balLocId <= 0)
            throw Invalid("FIFO MR authority needs a batch and existing pile.");
        var batches = await db.IvTrxBatches.AsNoTracking()
            .Where(b => b.CompanyCode == companyCode
                && b.BranchCode == branchCode && b.BatchNo == batchNo)
            .ToArrayAsync(cancellationToken);
        if (batches.Length != 1)
            throw Invalid("FIFO MR authority has no unique draft batch.");
        var batch = batches[0];
        if (batch.TrxType != IvTrxTypes.MiscellaneousReceipt
            || batch.BatchStatus != IvBatchStatuses.New
            || batch.DeletedAtUtc is not null || batch.ForceCloseDate is not null)
            throw Invalid("FIFO MR authority needs an active NEW receipt.");
        var details = await db.IvTrxBatchDetails.AsNoTracking()
            .Where(d => d.BatchId == batch.Id).ToArrayAsync(cancellationToken);
        if (details.Length != 1)
            throw Invalid("FIFO MR replay currently needs one approved receipt line.");
        var detail = details[0];
        if (detail.CompanyCode != companyCode
            || detail.BranchCode != branchCode
            || detail.BatchNo != batchNo
            || detail.TrxType != IvTrxTypes.MiscellaneousReceipt
            || detail.DocumentRevision < 0
            || detail.TrxLineNo <= 0
            || string.IsNullOrWhiteSpace(detail.ICode)
            || string.IsNullOrWhiteSpace(detail.ToStdUom)
            || detail.ToStdQty is not decimal quantity || quantity <= 0m
            || IvQty.Round(quantity) != quantity
            || detail.FromBalLocId is not null
            || detail.FrStdQty is not null
            || detail.ToBalLocId is not null
            || detail.ToLotId is not null
            || !string.IsNullOrWhiteSpace(detail.ToLotNo)
            || string.IsNullOrWhiteSpace(detail.Reason))
            throw Invalid("The draft MR line is not one supported non-lot inbound leg.");
        var evidence = InventoryCostEvidenceTypes.Normalize(
            detail.CostEvidenceType);
        var expectedPriceEvidence = evidence switch
        {
            InventoryCostEvidenceTypes.ManualApproved =>
                "EXPLICIT_COMPANY_BASE_PRICE",
            InventoryCostEvidenceTypes.ZeroCostApproved =>
                InventoryCostEvidenceTypes.ZeroCostApproved,
            InventoryCostEvidenceTypes.OpeningApproved =>
                InventoryCostEvidenceTypes.OpeningApproved,
            _ => null
        };
        if (expectedPriceEvidence is null
            || detail.PriceEvidence != expectedPriceEvidence
            || string.IsNullOrWhiteSpace(detail.CostApprovedBy)
            || detail.CostApprovedAtUtc is null
            || string.IsNullOrWhiteSpace(detail.CostOverrideReason)
            || detail.UnitPrice is not decimal unit || unit < 0m
            || StockLedgerPrecision.Money(unit) != unit
            || evidence == InventoryCostEvidenceTypes.ManualApproved
                && unit <= 0m
            || evidence == InventoryCostEvidenceTypes.ZeroCostApproved
                && unit != 0m
            || detail.BaseUnitPrices is not null
            || detail.Cost is not null || detail.CostPrice is not null)
            throw Invalid("The MR has no unambiguous approved company-base cost.");
        var balances = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == companyCode
                && b.BranchCode == branchCode && b.Id == balLocId)
            .ToArrayAsync(cancellationToken);
        if (balances.Length != 1)
            throw Invalid("The FIFO MR destination pile is missing.");
        var balance = balances[0];
        var masters = await db.IvStockMasters.AsNoTracking()
            .Where(m => m.CompanyCode == companyCode
                && m.ICode == detail.ICode).ToArrayAsync(cancellationToken);
        if (masters.Length != 1 || !masters[0].IsActive
            || !masters[0].StockControl || masters[0].LotControl
            || balance.ICode != detail.ICode
            || balance.LotId is not null || !string.IsNullOrWhiteSpace(balance.LotNo)
            || balance.WhCode != detail.ToWarehouse
            || balance.LocCode != (detail.ToLocation ?? string.Empty)
            || balance.IStatus != detail.IStatus
            || balance.StdUom != detail.ToStdUom)
            throw Invalid("The approved MR line does not match its non-lot destination pile.");
        var value = StockLedgerPrecision.Money(quantity * unit);
        return new(batch.Id, batchNo, detail.Id, detail.DocumentRevision,
            detail.ICode!, balLocId, batch.TrxDtTime, quantity,
            detail.ToStdUom!, unit, value);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
