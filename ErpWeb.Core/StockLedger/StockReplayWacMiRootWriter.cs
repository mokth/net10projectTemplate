using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>Writes one non-lot Inventory MI at its historical WAC cost.</summary>
public static class StockReplayWacMiRootWriter
{
    public static async Task<IReadOnlyList<int>> InsertAsync(
        StockPostingContext context, StockReplayWacInsertionPlan plan,
        int batchNo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        context.EnsureUnsealed();
        var physical = plan.PhysicalPlan;
        var candidate = physical.Candidate;
        if (context.CostMethod != StockCostMethods.MovingAverage
            || candidate.SignedDelta >= 0m
            || plan.Projection.Candidate.SignedQuantity != candidate.SignedDelta
            || plan.Projection.Candidate.SignedValue > 0m
            || context.Posting.SourceModule != "INVENTORY"
            || context.Posting.SourceDocumentType != IvTrxTypes.MiscellaneousIssue
            || context.Posting.SourceDocumentNo != batchNo.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || context.Posting.PostingRole != "PRIMARY"
            || context.Posting.EffectiveAt != candidate.Key.EffectiveAt
            || context.Posting.PostingSequence != candidate.Key.OriginalPostingSequence)
            throw Invalid("The WAC MI root differs from its projected issue.");
        var permit = await StockReplayWacInsertionPlanner.PrepareWithinRootAsync(
            context, plan, cancellationToken);
        var db = context.Db;
        var batches = await db.IvTrxBatches.Where(b =>
            b.CompanyCode == context.CompanyCode && b.BranchCode == context.BranchCode
            && b.BatchNo == batchNo).ToArrayAsync(cancellationToken);
        if (batches.Length != 1)
            throw Invalid("The MI draft batch is missing or ambiguous.");
        var batch = batches[0];
        if (batch.TrxType != IvTrxTypes.MiscellaneousIssue
            || batch.BatchStatus != IvBatchStatuses.New
            || batch.DeletedAtUtc is not null || batch.ForceCloseDate is not null
            || batch.TrxDtTime != candidate.Key.EffectiveAt
            || context.Posting.SourceDocumentId != batch.Id.ToString(
                System.Globalization.CultureInfo.InvariantCulture))
            throw Invalid("The MI draft changed after replay planning.");
        var details = await db.IvTrxBatchDetails.Where(d =>
            d.BatchId == batch.Id).ToArrayAsync(cancellationToken);
        if (details.Length != 1)
            throw Invalid("WAC replay requires one MI detail.");
        var detail = details[0];
        var quantity = -candidate.SignedDelta;
        if (detail.CompanyCode != context.CompanyCode
            || detail.BranchCode != context.BranchCode
            || detail.BatchNo != batchNo
            || detail.DocumentRevision != context.Posting.DocumentRevision
            || detail.TrxLineNo <= 0
            || detail.TrxType != IvTrxTypes.MiscellaneousIssue
            || detail.ICode != plan.ItemCode
            || detail.FromBalLocId != physical.BalLocId
            || detail.FrStdQty != quantity
            || detail.ToBalLocId is not null || detail.ToStdQty is not null
            || IvQty.Round(quantity) != quantity
            || await db.IvTrxHistories.AsNoTracking().AnyAsync(h =>
                h.CompanyCode == context.CompanyCode
                && h.BranchCode == context.BranchCode
                && ((h.BatchNo == batchNo
                        && h.DocumentRevision == detail.DocumentRevision)
                    || h.StockPostingId == context.Posting.Id),
                cancellationToken))
            throw Invalid("The staged MI line differs from its replay candidate.");
        var master = await db.IvStockMasters.AsNoTracking().SingleOrDefaultAsync(m =>
            m.CompanyCode == context.CompanyCode && m.ICode == plan.ItemCode,
            cancellationToken);
        var balance = await db.IvBalLocs.SingleOrDefaultAsync(b =>
            b.CompanyCode == context.CompanyCode && b.BranchCode == context.BranchCode
            && b.Id == physical.BalLocId, cancellationToken);
        if (master is null || !master.IsActive || !master.StockControl
            || master.LotControl || balance is null
            || balance.ICode != plan.ItemCode
            || balance.LotId is not null || !string.IsNullOrWhiteSpace(balance.LotNo)
            || balance.WhCode != detail.FrWarehouse
            || balance.LocCode != (detail.FrLocation ?? string.Empty)
            || balance.IStatus != detail.IStatus
            || balance.StdUom != detail.FrStdUom
            || balance.StdQty != physical.CurrentQuantity
            || !balance.RowVersion.SequenceEqual(physical.BalanceRowVersion))
            throw Invalid("The non-lot MI source pile changed after replay planning.");
        var now = DateTime.UtcNow;
        var user = context.UserId.Length > 10 ? context.UserId[..10] : context.UserId;
        balance.StdQty = physical.Projection.FinalQuantity;
        balance.TransDate = physical.LaterEvents.Count == 0
            ? candidate.Key.EffectiveAt
            : physical.LaterEvents.Max(e => e.Key.EffectiveAt);
        balance.ModifiedDate = now;
        detail.FromLotId = balance.LotId;
        detail.PriceEvidence = balance.PriceEvidence;
        var history = new IvTrxHistory
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
            BatchNo = batchNo, TrxLineNo = detail.TrxLineNo,
            TrxType = detail.TrxType, BatchStatus = IvBatchStatuses.Posted,
            RefNo = batch.RefNo,
            ProdCode = detail.ProdCode ?? detail.ICode,
            ProdDesc = detail.ProdDesc ?? detail.IDesc,
            ICode = plan.ItemCode, IDesc = detail.IDesc,
            FrWarehouse = detail.FrWarehouse, FrLocation = detail.FrLocation,
            FrLotNo = detail.FrLotNo, FrStdQty = detail.FrStdQty,
            FrStdUom = detail.FrStdUom, FrPurQty = detail.FrPurQty,
            FrPurUom = detail.FrPurUom, IStatus = detail.IStatus,
            Remarks = detail.Remarks, PriceEvidence = detail.PriceEvidence,
            CostEvidenceType = detail.CostEvidenceType,
            CostOverrideReason = detail.CostOverrideReason,
            CostApprovedBy = detail.CostApprovedBy,
            CostApprovedAtUtc = detail.CostApprovedAtUtc,
            UnitPrice = detail.UnitPrice, Cost = detail.Cost,
            CostPrice = detail.CostPrice, BaseUnitPrices = detail.BaseUnitPrices,
            LocationCode = detail.LocationCode, FromBalLocId = balance.Id,
            FromLotId = balance.LotId, CreatedDate = now, CreatedBy = user
        };
        new IvInventoryHistoryWriter().StampGeneration(
            context, [history], context.Posting.DocumentRevision);
        db.IvTrxHistories.Add(history);
        batch.BatchStatus = IvBatchStatuses.Posted;
        batch.PostedDate = now;
        batch.PostedBy = user;
        batch.PostedCount += 1;
        batch.PostingOperationId = Guid.NewGuid();
        batch.ModifiedDate = now;
        batch.ModifiedBy = user;
        await db.SaveChangesAsync(cancellationToken);
        if (history.FrStdQty != quantity
            || history.TrxDtTime != candidate.Key.EffectiveAt
            || history.StockPostingId != context.Posting.Id
            || history.LedgerEpochId != context.Epoch.Id
            || history.EntryRole != "ORIGINAL")
            throw Invalid("The MI history does not match its replay candidate.");
        var baseCurrency = await db.Companies.AsNoTracking()
            .Where(c => c.CompanyCode == context.CompanyCode)
            .Select(c => c.CurrencyCode)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(baseCurrency)
            || string.IsNullOrWhiteSpace(history.FrStdUom))
            throw Invalid("The MI has no company currency or base UOM.");
        var amount = -plan.Projection.Candidate.SignedValue;
        var sourceLine = history.TrxLineNo.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        db.StockValuationFacts.Add(new StockValuationFact
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
            LedgerEpochId = context.Epoch.Id, StockPostingId = context.Posting.Id,
            PostingLineNo = 1, SplitOrdinal = 0,
            SourceLineId = $"{context.Posting.SourceDocumentType}:{context.Posting.SourceDocumentId}:{sourceLine}:1:0",
            SourceDocumentType = context.Posting.SourceDocumentType,
            SourceDocumentId = context.Posting.SourceDocumentId,
            SourceDocumentNo = context.Posting.SourceDocumentNo,
            SourceDocumentLine = sourceLine,
            EffectiveAt = candidate.Key.EffectiveAt,
            BusinessDate = context.Posting.BusinessDate,
            PeriodKey = context.Posting.PeriodKey,
            ItemCode = plan.ItemCode,
            WarehouseCode = BlankToNull(history.FrWarehouse),
            LocationCode = BlankToNull(history.FrLocation),
            ItemStatus = BlankToNull(history.IStatus),
            BaseUom = history.FrStdUom!, MovementCode = "ISSUE_OUT",
            Direction = -1, BaseQty = quantity,
            CostMethod = StockCostMethods.MovingAverage,
            UnitCost = StockLedgerPrecision.Money(amount / quantity),
            CostAmount = amount, BaseCurrency = baseCurrency,
            BaseCostAmount = amount,
            ValuationSource = StockValuationSources.MovingAverage,
            ValuationStatus = StockValuationStatuses.Valued,
            ValuationVersion = 1, InventoryHistoryId = history.Id,
            FromBalLocId = balance.Id, CreatedAtUtc = now,
            CreatedBy = context.UserId
        });
        history.ValuationStatus = StockValuationStatuses.Valued;
        await db.SaveChangesAsync(cancellationToken);
        var tail = await StockReplayWacTailRepricingWriter
            .AppendPreparedInsertionTailAsync(context, permit, plan, 2,
                cancellationToken);
        await StockReplayWacCostStateWriter.ApplyAsync(
            context, permit, cancellationToken);
        return new[] { history.Id }.Concat(tail.RepricedHistoryIds).ToArray();
    }

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
