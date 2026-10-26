using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Writes a single approved MR or bounded GR into an existing WAC pile and
/// reprices supported Inventory descendants. The caller owns transaction and seal.
/// </summary>
public static class StockReplayWacMrRootWriter
{
    public static async Task<IReadOnlyList<int>> InsertAsync(
        StockPostingContext context, StockReplayWacInsertionPlan plan,
        int batchNo, CancellationToken cancellationToken = default,
        bool goodsReceipt = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        context.EnsureUnsealed();
        var physical = plan.PhysicalPlan;
        var authority = goodsReceipt
            ? await StockReplayGrReceiptAuthorityReader.ReadAsync(
                context.Db, context.CompanyCode, context.BranchCode, batchNo,
                physical.BalLocId, cancellationToken)
            : await StockReplayFifoMrReceiptAuthorityReader.ReadAsync(
                context.Db, context.CompanyCode, context.BranchCode, batchNo,
                physical.BalLocId, cancellationToken, allowExistingLot: true);
        var receiptType = goodsReceipt
            ? IvTrxTypes.GoodsReceive : IvTrxTypes.MiscellaneousReceipt;
        if (context.CostMethod != StockCostMethods.MovingAverage
            || context.Posting.SourceModule != "INVENTORY"
            || context.Posting.SourceDocumentType != receiptType
            || context.Posting.SourceDocumentId != authority.BatchId.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || context.Posting.SourceDocumentNo != batchNo.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || context.Posting.PostingRole != "PRIMARY"
            || context.Posting.DocumentRevision != authority.DocumentRevision
            || context.Posting.PostingSequence != physical.Candidate.Key.OriginalPostingSequence
            || context.Posting.EffectiveAt != authority.EffectiveAt
            || plan.ItemCode != authority.ItemCode
            || physical.Candidate.SignedDelta != authority.Quantity
            || plan.Candidate.FixedSignedValue != authority.Value
            || plan.Projection.Candidate.SignedValue != authority.Value
            || plan.Candidate.ReplaySignedQuantity != authority.Quantity)
            throw Invalid("The WAC receipt root differs from its approved draft or projection.");

        var permit = await StockReplayWacInsertionPlanner.PrepareWithinRootAsync(
            context, plan, cancellationToken);
        var db = context.Db;
        var batch = await db.IvTrxBatches.SingleAsync(b =>
            b.Id == authority.BatchId, cancellationToken);
        var detail = await db.IvTrxBatchDetails.SingleAsync(d =>
            d.Id == authority.DetailId, cancellationToken);
        var balance = await db.IvBalLocs.SingleAsync(b =>
            b.Id == authority.BalLocId && b.CompanyCode == context.CompanyCode
            && b.BranchCode == context.BranchCode, cancellationToken);
        db.ChangeTracker.DetectChanges();
        if (batch.BatchStatus != IvBatchStatuses.New
            || detail.DocumentRevision != authority.DocumentRevision
            || (goodsReceipt
                ? string.IsNullOrWhiteSpace(detail.PoNo) || detail.PoRelNo is null
                : !string.IsNullOrWhiteSpace(detail.PoNo) || detail.PoRelNo is not null)
            || db.Entry(batch).State != EntityState.Unchanged
            || db.Entry(detail).State != EntityState.Unchanged
            || balance.StdQty != physical.CurrentQuantity
            || balance.LotId != authority.LotId
            || !string.Equals(balance.LotNo ?? string.Empty,
                authority.LotNo ?? string.Empty,
                StringComparison.OrdinalIgnoreCase)
            || !balance.RowVersion.SequenceEqual(physical.BalanceRowVersion)
            || await db.IvTrxHistories.AsNoTracking().AnyAsync(h =>
                h.CompanyCode == context.CompanyCode
                && h.BranchCode == context.BranchCode
                && ((h.BatchNo == batchNo
                        && h.DocumentRevision == authority.DocumentRevision)
                    || h.StockPostingId == context.Posting.Id),
                cancellationToken))
            throw Invalid("The approved receipt draft, pile, or replay root changed.");

        var now = DateTime.UtcNow;
        var user = context.UserId.Length > 10 ? context.UserId[..10] : context.UserId;
        if (balance.StdQty == 0m)
        {
            balance.UnitPrice = authority.UnitCost;
            balance.Cost = authority.UnitCost;
            balance.PriceEvidence = detail.PriceEvidence;
        }
        else if (balance.UnitPrice != authority.UnitCost)
            balance.PriceEvidence = null;
        balance.StdQty = physical.Projection.FinalQuantity;
        balance.TransDate = physical.LaterEvents.Count == 0
            ? authority.EffectiveAt : physical.LaterEvents.Max(e => e.Key.EffectiveAt);
        balance.ModifiedDate = now;
        detail.ToBalLocId = balance.Id;
        detail.ToLotId = authority.LotId;
        var history = new IvTrxHistory
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
            BatchNo = batchNo, TrxLineNo = detail.TrxLineNo,
            TrxType = receiptType,
            BatchStatus = IvBatchStatuses.Posted, RefNo = batch.RefNo,
            ProdCode = detail.ProdCode ?? detail.ICode,
            ProdDesc = detail.ProdDesc ?? detail.IDesc,
            ICode = authority.ItemCode, IDesc = detail.IDesc,
            ToWarehouse = detail.ToWarehouse, ToLocation = detail.ToLocation,
            ToLotNo = detail.ToLotNo, ToStdQty = detail.ToStdQty,
            ToStdUom = detail.ToStdUom, ToPurQty = detail.ToPurQty,
            ToPurUom = detail.ToPurUom, IStatus = detail.IStatus,
            DoNo = detail.DoNo, PoNo = detail.PoNo, PoRelNo = detail.PoRelNo,
            PoLineNo = detail.PoLineNo, Remarks = detail.Remarks,
            PriceEvidence = detail.PriceEvidence,
            CostEvidenceType = detail.CostEvidenceType,
            CostOverrideReason = detail.CostOverrideReason,
            CostApprovedBy = detail.CostApprovedBy,
            CostApprovedAtUtc = detail.CostApprovedAtUtc,
            UnitPrice = detail.UnitPrice, Cost = detail.Cost,
            CostPrice = detail.CostPrice, BaseUnitPrices = detail.BaseUnitPrices,
            LocationCode = detail.LocationCode, ToBalLocId = balance.Id,
            ToLotId = authority.LotId,
            CreatedDate = now, CreatedBy = user
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

        if (history.ToStdQty != authority.Quantity
            || history.TrxDtTime != authority.EffectiveAt
            || history.StockPostingId != context.Posting.Id
            || history.LedgerEpochId != context.Epoch.Id
            || history.EntryRole != "ORIGINAL")
            throw Invalid("The receipt history does not match its replay candidate.");
        var baseCurrency = await db.Companies.AsNoTracking()
            .Where(c => c.CompanyCode == context.CompanyCode)
            .Select(c => c.CurrencyCode)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(baseCurrency))
            throw Invalid("The receipt company has no base currency.");
        var sourceLine = history.TrxLineNo.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        db.StockValuationFacts.Add(new StockValuationFact
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
            LedgerEpochId = context.Epoch.Id,
            StockPostingId = context.Posting.Id, PostingLineNo = 1,
            SplitOrdinal = 0,
            SourceLineId = $"{context.Posting.SourceDocumentType}:{context.Posting.SourceDocumentId}:{sourceLine}:1:0",
            SourceDocumentType = context.Posting.SourceDocumentType,
            SourceDocumentId = context.Posting.SourceDocumentId,
            SourceDocumentNo = context.Posting.SourceDocumentNo,
            SourceDocumentLine = sourceLine,
            EffectiveAt = authority.EffectiveAt,
            BusinessDate = context.Posting.BusinessDate,
            PeriodKey = context.Posting.PeriodKey,
            ItemCode = authority.ItemCode,
            WarehouseCode = BlankToNull(history.ToWarehouse),
            LocationCode = BlankToNull(history.ToLocation),
            LotId = authority.LotId, LotNo = authority.LotNo,
            ItemStatus = BlankToNull(history.IStatus),
            BaseUom = authority.BaseUom, MovementCode = "RECEIPT_IN",
            Direction = 1, BaseQty = authority.Quantity,
            CostMethod = StockCostMethods.MovingAverage,
            UnitCost = authority.UnitCost, CostAmount = authority.Value,
            BaseCurrency = baseCurrency, BaseCostAmount = authority.Value,
            ValuationSource = StockValuationSources.MovingAverage,
            ValuationStatus = StockValuationStatuses.Valued,
            ValuationVersion = 1, InventoryHistoryId = history.Id,
            ToBalLocId = balance.Id, CreatedAtUtc = now,
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
