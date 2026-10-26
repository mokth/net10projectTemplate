using System.Globalization;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Resolves the frozen provisional PO cost for bounded historical GR routes.
/// A changed PO or receipt cost must be reviewed through the normal draft flow.
/// </summary>
public static class StockReplayGrReceiptAuthorityReader
{
    public static async Task<StockReplayFifoMrReceiptAuthority> ReadAsync(
        AppDbContext db, string companyCode, string branchCode,
        int batchNo, int balLocId, CancellationToken cancellationToken = default,
        bool allowInvoiced = false)
    {
        if (batchNo <= 0 || balLocId <= 0)
            throw Invalid("Historical GR authority needs a batch and existing pile.");
        var batch = await db.IvTrxBatches.AsNoTracking().SingleOrDefaultAsync(b =>
            b.CompanyCode == companyCode && b.BranchCode == branchCode
            && b.BatchNo == batchNo, cancellationToken);
        if (batch is null || batch.TrxType != IvTrxTypes.GoodsReceive
            || batch.BatchStatus != IvBatchStatuses.New
            || batch.DeletedAtUtc is not null || batch.ForceCloseDate is not null)
            throw Invalid("Historical GR authority needs an active NEW goods receipt.");
        var details = await db.IvTrxBatchDetails.AsNoTracking()
            .Where(d => d.BatchId == batch.Id).ToArrayAsync(cancellationToken);
        if (details.Length != 1)
            throw Invalid("Backdated GR currently needs one stock line.");
        var detail = details[0];
        if (detail.CompanyCode != companyCode || detail.BranchCode != branchCode
            || detail.BatchNo != batchNo || detail.TrxType != IvTrxTypes.GoodsReceive
            || detail.DocumentRevision < 0 || detail.TrxLineNo <= 0
            || string.IsNullOrWhiteSpace(detail.ICode)
            || string.IsNullOrWhiteSpace(detail.ToStdUom)
            || detail.ToStdQty is not decimal quantity || quantity <= 0m
            || IvQty.Round(quantity) != quantity
            || detail.ToPurQty is not decimal purchaseQty || purchaseQty <= 0m
            || string.IsNullOrWhiteSpace(detail.ToPurUom)
            || string.IsNullOrWhiteSpace(detail.PoNo)
            || detail.PoRelNo is null || detail.PoLineNo is null
            || detail.FromBalLocId is not null || detail.FrStdQty is not null
            || detail.ToBalLocId is not null || detail.ToLotId is not null
            || detail.BaseUnitPrices is not decimal unit || unit < 0m
            || StockLedgerPrecision.Money(unit) != unit
            || detail.CostPrice is not decimal purchaseUnit || purchaseUnit < 0m
            || detail.UnitPrice is null || detail.Currency is null
            || detail.CostEvidenceType is not null)
            throw Invalid("The draft GR line has no exact PO, quantity, or provisional cost.");

        var po = await db.PoOrders.AsNoTracking().SingleOrDefaultAsync(p =>
            p.CompanyCode == companyCode && p.BranchCode == branchCode
            && p.PoNo == detail.PoNo && p.PoRelNo == detail.PoRelNo,
            cancellationToken);
        var poLine = await db.PoOrderDetails.AsNoTracking().SingleOrDefaultAsync(p =>
            p.CompanyCode == companyCode && p.BranchCode == branchCode
            && p.PoNo == detail.PoNo && p.PoRelNo == detail.PoRelNo
            && p.Line == detail.PoLineNo, cancellationToken);
        if (po is null || poLine is null || poLine.ICode != detail.ICode
            || poLine.PoPurQty <= 0m
            || !allowInvoiced && poLine.InvoicedQty != 0m
            || poLine.PurchaseUom != detail.ToPurUom
            || poLine.StdUom != detail.ToStdUom
            || poLine.PoUnitPrice != detail.UnitPrice)
            throw Invalid("The GR purchase order changed or has invoice settlement; historical settlement replay is required.");

        var currency = string.IsNullOrWhiteSpace(poLine.CurCode)
            ? po.CurCode : poLine.CurCode;
        var homeCurrency = await db.Companies.AsNoTracking()
            .Where(c => c.CompanyCode == companyCode)
            .Select(c => c.CurrencyCode).SingleOrDefaultAsync(cancellationToken);
        currency = string.IsNullOrWhiteSpace(currency) ? homeCurrency : currency.Trim();
        if (string.IsNullOrWhiteSpace(currency)
            || !string.Equals(detail.Currency, currency, StringComparison.OrdinalIgnoreCase))
            throw Invalid("The GR currency no longer matches its purchase order.");
        var prefix = $"PO_PROVISIONAL|PO={detail.PoNo}/{detail.PoRelNo}/{detail.PoLineNo}|CUR={currency}|RATE=";
        if (detail.PriceEvidence is null
            || !detail.PriceEvidence.StartsWith(prefix, StringComparison.Ordinal)
            || !decimal.TryParse(detail.PriceEvidence[prefix.Length..],
                NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                out var rate) || rate <= 0m)
            throw Invalid("The GR has no valid frozen provisional exchange rate.");
        var approvedRate = await new CompanyCurrencyRateResolver().ResolveAsync(
            db, companyCode, currency, batch.TrxDtTime, cancellationToken);
        if (!approvedRate.Succeeded || approvedRate.Rate != rate)
            throw Invalid("The GR provisional exchange rate no longer matches the approved rate for its transaction date.");
        var expectedPurchaseUnit = decimal.Round(poLine.NetAmount / poLine.PoPurQty,
            6, MidpointRounding.AwayFromZero);
        var expectedBaseUnit = decimal.Round(
            expectedPurchaseUnit * purchaseQty * rate / quantity,
            6, MidpointRounding.AwayFromZero);
        if (purchaseUnit != expectedPurchaseUnit || unit != expectedBaseUnit)
            throw Invalid("The GR provisional PO cost changed after draft approval.");

        var balance = await db.IvBalLocs.AsNoTracking().SingleOrDefaultAsync(b =>
            b.CompanyCode == companyCode && b.BranchCode == branchCode
            && b.Id == balLocId, cancellationToken);
        var master = await db.IvStockMasters.AsNoTracking().SingleOrDefaultAsync(m =>
            m.CompanyCode == companyCode && m.ICode == detail.ICode,
            cancellationToken);
        if (balance is null || master is null || !master.IsActive
            || !master.StockControl || balance.ICode != detail.ICode
            || balance.WhCode != detail.ToWarehouse
            || balance.LocCode != (detail.ToLocation ?? string.Empty)
            || balance.IStatus != detail.IStatus
            || balance.StdUom != detail.ToStdUom
            || master.LotControl != (balance.LotId is > 0)
            || !string.Equals(balance.LotNo ?? string.Empty,
                detail.ToLotNo ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            throw Invalid("The GR destination no longer matches its approved stock pile.");
        if (balance.LotId is int lotId
            && !await db.IvLots.AsNoTracking().AnyAsync(l =>
                l.Id == lotId && l.CompanyCode == companyCode
                && l.ICode == detail.ICode && l.LotNo == balance.LotNo
                && l.IsActive, cancellationToken))
            throw Invalid("The GR destination lot is no longer active.");
        if (!IvExpiryControlModes.TryNormalize(master.ExpiryControl,
                out var expiryControl)
            || !master.LotControl && expiryControl != IvExpiryControlModes.None
            || master.LotControl && expiryControl == IvExpiryControlModes.Required
                && detail.ExpiryDate is null
            || master.LotControl && expiryControl != IvExpiryControlModes.None
                && detail.ExpiryDate is DateTime expiry
                && expiry.Date < batch.TrxDtTime.Date)
            throw Invalid("The GR expiry date no longer satisfies the item policy.");
        if (!await db.IvWarehouses.AsNoTracking().AnyAsync(w =>
                w.CompanyCode == companyCode && w.BranchCode == branchCode
                && w.WarehouseCode == balance.WhCode && w.IsActive,
                cancellationToken)
            || !await db.IvStatuses.AsNoTracking().AnyAsync(s =>
                s.CompanyCode == companyCode && s.IStatus == balance.IStatus
                && s.IsActive, cancellationToken))
            throw Invalid("The GR destination warehouse or status is inactive.");
        if (!string.IsNullOrWhiteSpace(balance.LocCode)
            && !await db.IvLocations.AsNoTracking().AnyAsync(l =>
                l.CompanyCode == companyCode && l.BranchCode == branchCode
                && l.WarehouseCode == balance.WhCode
                && l.LocCode == balance.LocCode && l.IsActive,
                cancellationToken))
            throw Invalid("The GR destination location is inactive.");
        if (string.IsNullOrWhiteSpace(balance.LocCode)
            && await db.IvLocations.AsNoTracking().AnyAsync(l =>
                l.CompanyCode == companyCode && l.BranchCode == branchCode
                && l.WarehouseCode == balance.WhCode && l.IsActive,
                cancellationToken))
            throw Invalid("The GR destination warehouse requires a location.");
        return new(batch.Id, batchNo, detail.Id, detail.DocumentRevision,
            detail.ICode!, balLocId, batch.TrxDtTime, quantity,
            detail.ToStdUom!, unit, StockLedgerPrecision.Money(quantity * unit),
            balance.LotId, balance.LotNo);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
