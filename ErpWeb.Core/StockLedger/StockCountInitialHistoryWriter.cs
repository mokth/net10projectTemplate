using ErpWeb.Core.Inventory;
using ErpWeb.Core.Numbering;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockCountInitialHistoryResult(
    int? AdjustmentBatchNo,
    IReadOnlyList<StockCountReplayGeneration> Generations);

/// <summary>
/// Appends the first count ADJ batch and histories inside an already prepared
/// V2 count root. The caller has applied each projected pile balance and
/// persisted the frozen cost bases. Valuation and root sealing follow this write.
/// </summary>
public static class StockCountInitialHistoryWriter
{
    public static async Task<StockCountInitialHistoryResult> AppendAsync(
        StockPostingContext context,
        StockCountSheetWritePermit permit,
        IRunningNumberService numbers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(permit);
        ArgumentNullException.ThrowIfNull(numbers);
        context.EnsureUnsealed();
        if (permit.CountStatus != IvStockCountStatuses.Counted
            || permit.RootPostingId != context.Posting.Id
            || permit.LedgerEpochId != context.Epoch.Id
            || permit.PostingSequence != context.Posting.PostingSequence
            || permit.CompanyCode != context.CompanyCode
            || permit.BranchCode != context.BranchCode
            || permit.EffectiveAt != context.Posting.EffectiveAt
            || context.Posting.DocumentRevision != 0
            || context.Posting.SourceModule != "INVENTORY"
            || context.Posting.SourceDocumentType != "IV_STOCK_COUNT"
            || context.Posting.SourceDocumentId != permit.CountHeaderId.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || permit.Lines.IsDefaultOrEmpty
            || string.IsNullOrWhiteSpace(permit.PlanHash))
            throw Invalid("The initial count history lacks a prepared root permit.");

        var db = context.Db;
        var header = await db.IvStockCountHdrs.SingleOrDefaultAsync(h =>
            h.Id == permit.CountHeaderId && h.CompanyCode == context.CompanyCode
            && h.BranchCode == context.BranchCode, cancellationToken);
        if (header is null || header.Status != IvStockCountStatuses.Counted
            || header.PostedBatchNo is not null
            || header.CountDate != header.CountDate.Date
            || StockReplayEventKey.CountEndOfDay(header.CountDate) != permit.EffectiveAt
            || header.CountNo != context.Posting.SourceDocumentNo)
            throw Invalid("The count header differs from its prepared root.");
        if (await db.IvTrxHistories.AsNoTracking().AnyAsync(h =>
                h.StockPostingId == context.Posting.Id, cancellationToken)
            || await db.StockCountReplayGenerations.AsNoTracking().AnyAsync(g =>
                g.WritePostingId == context.Posting.Id, cancellationToken))
            throw Invalid("The count root already owns Inventory history or generations.");

        var lineIds = permit.Lines.Select(p => p.LineId).ToArray();
        var pileIds = permit.Lines.Select(p => p.BalLocId).ToArray();
        if (lineIds.Distinct().Count() != lineIds.Length
            || pileIds.Distinct().Count() != pileIds.Length)
            throw Invalid("The count permit repeats a line or pile.");
        var lines = await db.IvStockCountLines.AsNoTracking()
            .Where(l => l.StockCountId == permit.CountHeaderId
                && l.PhysicalQty != null)
            .ToArrayAsync(cancellationToken);
        if (lines.Length != permit.Lines.Length)
            throw Invalid("The count sheet changed its counted lines.");
        var byLine = lines.ToDictionary(l => l.Id);
        var piles = await db.IvBalLocs.AsNoTracking()
            .Where(b => pileIds.Contains(b.Id)
                && b.CompanyCode == context.CompanyCode
                && b.BranchCode == context.BranchCode)
            .ToDictionaryAsync(b => b.Id, cancellationToken);
        if (piles.Count != pileIds.Length
            || permit.Lines.Any(p => !byLine.TryGetValue(p.LineId, out var line)
                || !piles.TryGetValue(p.BalLocId, out var pile)
                || line.BalLocId != p.BalLocId
                || line.PhysicalQty != p.PhysicalQty
                || pile.StdQty != p.FinalQuantity
                || !StockReplayCountSheetPreviewReader.Matches(line, pile)
                || IvQty.Round(p.SignedAdjustmentQty) != p.SignedAdjustmentQty))
            throw Invalid("The count's posted pile differs from its physical projection.");

        var bases = await db.StockCountCostBases.AsNoTracking()
            .Where(b => lineIds.Contains(b.StockCountLineId))
            .ToArrayAsync(cancellationToken);
        var positiveIds = lines.Where(l => l.PhysicalQty > 0m)
            .Select(l => l.Id).Order().ToArray();
        if (!bases.Select(b => b.StockCountLineId).Order().SequenceEqual(positiveIds)
            || bases.Any(b => b.CompanyCode != context.CompanyCode
                || b.BranchCode != context.BranchCode
                || !StockCountAnchorCommandFactory.ValidBasis(b))
            || !StockCountAnchorCommandFactory.MatchesPersistedBasisSnapshot(
                context, bases))
            throw Invalid("The count lacks its exact frozen cost basis.");
        var basisByLine = bases.ToDictionary(b => b.StockCountLineId);

        var nonzero = permit.Lines.Where(p => p.SignedAdjustmentQty != 0m)
            .OrderBy(p => byLine[p.LineId].LineNumber)
            .ThenBy(p => p.LineId).ToArray();
        if (nonzero.Length > short.MaxValue)
            throw Invalid("The count has too many ADJ detail lines.");
        int? batchNo = null;
        IvTrxBatch? batch = null;
        var now = DateTime.UtcNow;
        if (nonzero.Length > 0)
        {
            batchNo = await numbers.GetNextAsync(db, context.CompanyCode,
                RunningNumberKeys.IvBatch, cancellationToken);
            if (await db.IvTrxBatches.AsNoTracking().AnyAsync(b =>
                b.CompanyCode == context.CompanyCode
                && b.BranchCode == context.BranchCode
                && b.BatchNo == batchNo, cancellationToken))
                throw Invalid($"Count ADJ batch number {batchNo} is already used.");
            batch = new IvTrxBatch
            {
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                BatchNo = batchNo.Value,
                TrxDtTime = header.CountDate,
                TrxType = IvTrxTypes.StockAdjustment,
                BatchStatus = IvBatchStatuses.Posted,
                RefNo = header.CountNo,
                Remarks = $"STOCK COUNT {header.CountNo}",
                PostedDate = now,
                PostedBy = context.UserId,
                PostedCount = 1,
                CreatedDate = now,
                CreatedBy = context.UserId
            };
            db.IvTrxBatches.Add(batch);
            await db.SaveChangesAsync(cancellationToken);
        }

        for (var index = 0; index < nonzero.Length; index++)
        {
            var projection = nonzero[index];
            var line = byLine[projection.LineId];
            var pile = piles[projection.BalLocId];
            var inbound = projection.SignedAdjustmentQty > 0m;
            var quantity = Math.Abs(projection.SignedAdjustmentQty);
            var uom = line.StdUom ?? pile.StdUom;
            if (string.IsNullOrWhiteSpace(uom))
                throw Invalid($"Count line {line.Id} has no adjustment UOM.");
            var basis = basisByLine.GetValueOrDefault(line.Id);
            if (inbound && basis is null)
                throw Invalid($"Count line {line.Id} has no inbound cost basis.");
            var evidenceType = inbound ? EvidenceType(basis!) : null;
            decimal? unitPrice = inbound ? IvQty.Round(basis!.FrozenFallbackUnitCost) : null;
            var remarks = IvStockAdjustmentLineInvariant.CombineRemarks(
                IvAdjustmentReasons.Count, header.CountNo);
            var history = new IvTrxHistory
            {
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                BatchNo = batchNo!.Value,
                TrxLineNo = checked((short)(index + 1)),
                TrxDtTime = header.CountDate,
                TrxType = IvTrxTypes.StockAdjustment,
                BatchStatus = IvBatchStatuses.Posted,
                RefNo = header.CountNo,
                ICode = line.ICode,
                IDesc = line.IDesc,
                FromBalLocId = inbound ? null : pile.Id,
                ToBalLocId = inbound ? pile.Id : null,
                FromLotId = inbound ? null : pile.LotId,
                ToLotId = inbound ? pile.LotId : null,
                FrWarehouse = inbound ? null : pile.WhCode,
                ToWarehouse = inbound ? pile.WhCode : null,
                FrLocation = inbound ? null : pile.LocCode,
                ToLocation = inbound ? pile.LocCode : null,
                FrLotNo = inbound ? null : pile.LotNo,
                ToLotNo = inbound ? pile.LotNo : null,
                FrStdQty = inbound ? null : quantity,
                ToStdQty = inbound ? quantity : null,
                FrStdUom = inbound ? null : uom,
                ToStdUom = inbound ? uom : null,
                IStatus = pile.IStatus,
                StockCountCostBasisId = inbound ? basis!.Id : null,
                CostEvidenceType = evidenceType,
                CostOverrideReason = inbound ? basis!.CostOverrideReason : null,
                CostApprovedBy = inbound ? basis!.CostApprovedBy : null,
                CostApprovedAtUtc = inbound ? basis!.CostApprovedAtUtc : null,
                UnitPrice = unitPrice,
                Remarks = remarks,
                LedgerVersion = 2,
                LedgerEpochId = context.Epoch.Id,
                StockPostingId = context.Posting.Id,
                PostingLineNo = index + 1,
                DocumentRevision = 0,
                EntryRole = "ORIGINAL",
                ValuationStatus = StockValuationStatuses.Unvalued,
                CreatedDate = now,
                CreatedBy = context.UserId
            };
            db.IvTrxBatchDetails.Add(new IvTrxBatchDetail
            {
                BatchId = batch!.Id,
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                BatchNo = batchNo.Value,
                DocumentRevision = 0,
                TrxLineNo = history.TrxLineNo,
                TrxType = IvTrxTypes.StockAdjustment,
                ICode = line.ICode,
                IDesc = line.IDesc,
                IClassCode = line.IClassCode,
                ExpiryDate = line.ExpiryDate,
                FromBalLocId = history.FromBalLocId,
                ToBalLocId = history.ToBalLocId,
                FromLotId = history.FromLotId,
                ToLotId = history.ToLotId,
                FrWarehouse = history.FrWarehouse,
                ToWarehouse = history.ToWarehouse,
                FrLocation = history.FrLocation,
                ToLocation = history.ToLocation,
                FrLotNo = history.FrLotNo,
                ToLotNo = history.ToLotNo,
                FrStdQty = history.FrStdQty,
                ToStdQty = history.ToStdQty,
                FrStdUom = history.FrStdUom,
                ToStdUom = history.ToStdUom,
                IStatus = history.IStatus,
                StockCountCostBasisId = history.StockCountCostBasisId,
                CostEvidenceType = evidenceType,
                CostOverrideReason = history.CostOverrideReason,
                CostApprovedBy = history.CostApprovedBy,
                CostApprovedAtUtc = history.CostApprovedAtUtc,
                UnitPrice = unitPrice,
                Remarks = remarks
            });
            db.IvTrxHistories.Add(history);
        }
        await db.SaveChangesAsync(cancellationToken);

        header.Status = IvStockCountStatuses.Posted;
        header.PostedBatchNo = batchNo;
        header.PostedBy = context.UserId;
        header.PostedOn = now;
        header.PostedStaleLines = permit.Lines.Count(p =>
            byLine[p.LineId].SystemQty != p.PhysicalQty - p.SignedAdjustmentQty);
        header.ModifiedDate = now;
        header.ModifiedBy = context.UserId;
        await db.SaveChangesAsync(cancellationToken);
        var generations = await StockCountInitialGenerationWriter.AppendAsync(
            context, permit, batchNo, cancellationToken);
        return new StockCountInitialHistoryResult(batchNo, generations);
    }

    private static string EvidenceType(StockCountCostBasis basis) =>
        basis.CostBasisKind is StockCountCostBasisKinds.LedgerWac
            or StockCountCostBasisKinds.LedgerFifo
            ? InventoryCostEvidenceTypes.CountFrozenV2
            : basis.CostEvidenceType ?? string.Empty;

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
