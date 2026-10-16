using ErpWeb.Core.Inventory;
using ErpWeb.Core.Numbering;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockCountCorrectionPostingLines(
    int CountLineId, int ReversalPostingLineNo, int ReplacementPostingLineNo);

/// <summary>
/// Writes the ADJ audit evidence for changed physical count generations inside
/// the triggering replay root. The caller has already applied the projected
/// pile quantity and assigned root posting lines for each changed count.
/// </summary>
public static class StockCountCorrectionHistoryWriter
{
    public static async Task<IReadOnlyList<StockCountReplayGeneration>> AppendAsync(
        StockPostingContext context,
        StockCountCorrectionWritePermit permit,
        IReadOnlyCollection<StockCountCorrectionPostingLines> postingLines,
        IRunningNumberService numbers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(permit);
        ArgumentNullException.ThrowIfNull(postingLines);
        ArgumentNullException.ThrowIfNull(numbers);
        context.EnsureUnsealed();
        if (context.Posting.Id != permit.RootPostingId
            || context.Epoch.Id != permit.LedgerEpochId
            || context.CompanyCode != permit.CompanyCode
            || context.BranchCode != permit.BranchCode
            || permit.Lines.IsDefaultOrEmpty
            || postingLines.Count != permit.Lines.Length
            || postingLines.Select(p => p.CountLineId).Distinct().Count()
                != postingLines.Count)
            throw Invalid("Count correction histories lack a matching replay root permit.");

        var lineNumbers = postingLines.SelectMany(p =>
                new[] { p.ReversalPostingLineNo, p.ReplacementPostingLineNo })
            .ToArray();
        if (lineNumbers.Any(n => n <= 0)
            || lineNumbers.Distinct().Count() != lineNumbers.Length)
            throw Invalid("Count correction root posting lines are invalid or duplicated.");
        var lineMap = postingLines.ToDictionary(p => p.CountLineId);
        if (permit.Lines.Any(p => !lineMap.ContainsKey(p.LineId)))
            throw Invalid("Count correction root posting lines omit a count line.");

        var db = context.Db;
        var pile = await db.IvBalLocs.AsNoTracking().SingleOrDefaultAsync(b =>
            b.Id == permit.BalLocId && b.CompanyCode == context.CompanyCode
            && b.BranchCode == context.BranchCode, cancellationToken);
        if (pile is null || pile.StdQty != permit.FinalQuantity)
            throw Invalid("The corrected count pile does not match the projected live quantity.");
        var occupied = await db.IvTrxHistories.AsNoTracking().AnyAsync(h =>
            h.StockPostingId == context.Posting.Id && h.LedgerVersion == 2
            && lineNumbers.Contains(h.PostingLineNo ?? 0), cancellationToken);
        if (occupied || db.ChangeTracker.Entries<IvTrxHistory>().Any(e =>
                e.State == EntityState.Added
                && e.Entity.StockPostingId == context.Posting.Id
                && lineNumbers.Contains(e.Entity.PostingLineNo ?? 0)))
            throw Invalid("A count correction root history line is already used.");

        var countLineIds = permit.Lines.Select(p => p.LineId).ToArray();
        var active = await StockCountGenerationResolver.ResolveActiveAsync(
            db, context.CompanyCode, context.BranchCode,
            countLineIds, cancellationToken);
        var lines = await db.IvStockCountLines.AsNoTracking()
            .Where(l => countLineIds.Contains(l.Id))
            .Select(l => new
            {
                l.Id, l.BalLocId, l.ICode, l.IStatus,
                l.WHCode, l.LocCode, l.LotNo,
                l.PhysicalQty, l.StdUom,
                l.Header.CompanyCode, l.Header.BranchCode,
                l.Header.Status, l.Header.CountDate, l.Header.CountNo
            }).ToDictionaryAsync(l => l.Id, cancellationToken);
        var bases = await db.StockCountCostBases.AsNoTracking()
            .Where(b => countLineIds.Contains(b.StockCountLineId))
            .ToArrayAsync(cancellationToken);
        if (bases.GroupBy(b => b.StockCountLineId).Any(g => g.Count() != 1)
            || bases.Any(b => b.CompanyCode != context.CompanyCode
                || b.BranchCode != context.BranchCode))
            throw Invalid("A count correction has ambiguous or foreign cost evidence.");
        var basisByLine = bases.ToDictionary(b => b.StockCountLineId);
        var oldHistoryIds = active.Values
            .Where(g => g.AdjustmentHistoryId.HasValue)
            .Select(g => g.AdjustmentHistoryId!.Value).ToArray();
        var oldHistories = await db.IvTrxHistories.AsNoTracking()
            .Where(h => oldHistoryIds.Contains(h.Id))
            .ToDictionaryAsync(h => h.Id, cancellationToken);

        foreach (var projection in permit.Lines)
        {
            if (!active.TryGetValue(projection.LineId, out var generation)
                || !lines.TryGetValue(projection.LineId, out var line)
                || generation.Id != projection.PreviousGenerationId
                || generation.IsRemoval
                || generation.AnchorPostingId != projection.AnchorPostingId
                || generation.AnchorPostingSequence
                    != projection.AnchorPostingSequence
                || generation.EffectiveAt != projection.EffectiveAt
                || generation.SignedAdjustmentQty
                    != projection.OldSignedAdjustmentQty
                || line.CompanyCode != context.CompanyCode
                || line.BranchCode != context.BranchCode
                || line.Status != IvStockCountStatuses.Posted
                || line.BalLocId != permit.BalLocId
                || line.ICode != pile.ICode
                || line.IStatus != pile.IStatus
                || line.WHCode != pile.WhCode
                || (line.LocCode ?? "") != pile.LocCode
                || (line.LotNo ?? "") != pile.LotNo
                || line.PhysicalQty != projection.PhysicalQty
                || StockReplayEventKey.CountEndOfDay(line.CountDate)
                    != projection.EffectiveAt
                || projection.OldSignedAdjustmentQty
                    == projection.NewSignedAdjustmentQty
                || (generation.AdjustmentHistoryId.HasValue
                    != (generation.SignedAdjustmentQty != 0m))
                || generation.AdjustmentHistoryId is int oldId
                    && (!oldHistories.TryGetValue(oldId, out var old)
                        || old.CompanyCode != context.CompanyCode
                        || old.BranchCode != context.BranchCode
                        || old.StockPostingId != generation.WritePostingId
                        || old.LedgerVersion != 2
                        || old.LedgerEpochId != context.Epoch.Id
                        || old.PostingLineNo is not > 0
                        || old.EntryRole != "ORIGINAL"
                        || old.ValuationStatus != StockValuationStatuses.Valued
                        || old.TrxType != IvTrxTypes.StockAdjustment
                        || old.BatchStatus != IvBatchStatuses.Posted
                        || old.TrxDtTime != line.CountDate.Date
                        || old.RefNo != line.CountNo
                        || old.ICode != line.ICode
                        || IvQty.Round((old.ToBalLocId == permit.BalLocId
                                ? old.ToStdQty ?? 0m : 0m)
                            - (old.FromBalLocId == permit.BalLocId
                                ? old.FrStdQty ?? 0m : 0m))
                            != generation.SignedAdjustmentQty)
                || line.PhysicalQty > 0m
                    && (!basisByLine.TryGetValue(line.Id, out var basis)
                        || !StockCountAnchorCommandFactory.ValidBasis(basis))
                || line.PhysicalQty == 0m
                    && basisByLine.ContainsKey(line.Id)
                || projection.NewSignedAdjustmentQty != 0m
                    && string.IsNullOrWhiteSpace(line.StdUom ?? pile.StdUom))
                throw Invalid($"Count line {projection.LineId} lacks exact correction authority.");
        }

        var evidence = new List<StockCountCorrectionHistories>(permit.Lines.Length);
        foreach (var projection in permit.Lines)
        {
            var line = lines[projection.LineId];
            var prior = active[projection.LineId];
            var positions = lineMap[line.Id];
            var basis = basisByLine.GetValueOrDefault(line.Id);
            int? reversalId = null;
            int? replacementId = null;
            if (prior.AdjustmentHistoryId is int oldId)
            {
                var original = oldHistories[oldId];
                var reversal = new IvTrxHistory
                {
                    CompanyCode = context.CompanyCode,
                    BranchCode = context.BranchCode,
                    TrxLineNo = 1,
                    TrxDtTime = line.CountDate.Date,
                    TrxType = IvTrxTypes.StockAdjustment,
                    BatchStatus = IvBatchStatuses.Posted,
                    RefNo = line.CountNo,
                    ICode = line.ICode,
                    IDesc = original.IDesc,
                    FromBalLocId = original.ToBalLocId,
                    ToBalLocId = original.FromBalLocId,
                    FromLotId = original.ToLotId,
                    ToLotId = original.FromLotId,
                    FrWarehouse = original.ToWarehouse,
                    ToWarehouse = original.FrWarehouse,
                    FrLocation = original.ToLocation,
                    ToLocation = original.FrLocation,
                    FrLotNo = original.ToLotNo,
                    ToLotNo = original.FrLotNo,
                    FrStdQty = original.ToStdQty,
                    ToStdQty = original.FrStdQty,
                    FrStdUom = original.ToStdUom,
                    ToStdUom = original.FrStdUom,
                    FrPurQty = original.ToPurQty,
                    ToPurQty = original.FrPurQty,
                    FrPurUom = original.ToPurUom,
                    ToPurUom = original.FrPurUom,
                    IStatus = original.IStatus,
                    StockCountCostBasisId = original.StockCountCostBasisId,
                    LedgerVersion = 2,
                    LedgerEpochId = context.Epoch.Id,
                    StockPostingId = context.Posting.Id,
                    PostingLineNo = positions.ReversalPostingLineNo,
                    DocumentRevision = context.Posting.DocumentRevision,
                    EntryRole = "REVERSAL",
                    ReversesHistoryId = oldId,
                    ValuationStatus = StockValuationStatuses.Unvalued,
                    CreatedDate = DateTime.UtcNow,
                    CreatedBy = context.UserId
                };
                await AddLegAsync(reversal);
                reversalId = reversal.Id;
            }

            if (projection.NewSignedAdjustmentQty != 0m)
            {
                var inbound = projection.NewSignedAdjustmentQty > 0m;
                var quantity = Math.Abs(projection.NewSignedAdjustmentQty);
                var uom = line.StdUom ?? pile.StdUom;
                var replacement = new IvTrxHistory
                {
                    CompanyCode = context.CompanyCode,
                    BranchCode = context.BranchCode,
                    TrxLineNo = 1,
                    TrxDtTime = line.CountDate.Date,
                    TrxType = IvTrxTypes.StockAdjustment,
                    BatchStatus = IvBatchStatuses.Posted,
                    RefNo = line.CountNo,
                    ICode = line.ICode,
                    FromBalLocId = inbound ? null : permit.BalLocId,
                    ToBalLocId = inbound ? permit.BalLocId : null,
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
                    CostEvidenceType = inbound ? EvidenceType(basis!) : null,
                    CostOverrideReason = inbound ? basis!.CostOverrideReason : null,
                    CostApprovedBy = inbound ? basis!.CostApprovedBy : null,
                    CostApprovedAtUtc = inbound ? basis!.CostApprovedAtUtc : null,
                    UnitPrice = inbound
                        ? IvQty.Round(basis!.FrozenFallbackUnitCost) : null,
                    LedgerVersion = 2,
                    LedgerEpochId = context.Epoch.Id,
                    StockPostingId = context.Posting.Id,
                    PostingLineNo = positions.ReplacementPostingLineNo,
                    DocumentRevision = context.Posting.DocumentRevision,
                    EntryRole = "ORIGINAL",
                    ValuationStatus = StockValuationStatuses.Unvalued,
                    CreatedDate = DateTime.UtcNow,
                    CreatedBy = context.UserId
                };
                await AddLegAsync(replacement);
                replacementId = replacement.Id;
            }
            evidence.Add(new StockCountCorrectionHistories(line.Id,
                reversalId, replacementId));
        }
        return await StockCountCorrectionGenerationWriter.AppendAsync(
            context, permit, evidence, cancellationToken);

        async Task AddLegAsync(IvTrxHistory history)
        {
            var batchNo = await numbers.GetNextAsync(db, context.CompanyCode,
                RunningNumberKeys.IvBatch, cancellationToken);
            if (await db.IvTrxBatches.AsNoTracking().AnyAsync(b =>
                b.CompanyCode == context.CompanyCode
                && b.BranchCode == context.BranchCode
                && b.BatchNo == batchNo, cancellationToken))
                throw Invalid($"Count correction batch number {batchNo} is already used.");
            var now = DateTime.UtcNow;
            var batch = new IvTrxBatch
            {
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                BatchNo = batchNo,
                TrxDtTime = history.TrxDtTime,
                TrxType = IvTrxTypes.StockAdjustment,
                BatchStatus = IvBatchStatuses.Posted,
                RefNo = history.RefNo,
                PostedDate = now,
                PostedBy = context.UserId,
                PostedCount = 1,
                CreatedDate = now,
                CreatedBy = context.UserId
            };
            db.IvTrxBatches.Add(batch);
            await db.SaveChangesAsync(cancellationToken);
            history.BatchNo = batchNo;
            db.IvTrxBatchDetails.Add(new IvTrxBatchDetail
            {
                BatchId = batch.Id,
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                BatchNo = batchNo,
                DocumentRevision = context.Posting.DocumentRevision,
                TrxLineNo = 1,
                TrxType = IvTrxTypes.StockAdjustment,
                ICode = history.ICode,
                IDesc = history.IDesc,
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
                FrPurQty = history.FrPurQty,
                ToPurQty = history.ToPurQty,
                FrPurUom = history.FrPurUom,
                ToPurUom = history.ToPurUom,
                IStatus = history.IStatus,
                StockCountCostBasisId = history.StockCountCostBasisId,
                CostEvidenceType = history.CostEvidenceType,
                CostOverrideReason = history.CostOverrideReason,
                CostApprovedBy = history.CostApprovedBy,
                CostApprovedAtUtc = history.CostApprovedAtUtc,
                UnitPrice = history.UnitPrice
            });
            db.IvTrxHistories.Add(history);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static string EvidenceType(StockCountCostBasis basis) =>
        basis.CostBasisKind is StockCountCostBasisKinds.LedgerWac
            or StockCountCostBasisKinds.LedgerFifo
            ? InventoryCostEvidenceTypes.CountFrozenV2
            : basis.CostEvidenceType ?? string.Empty;

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
