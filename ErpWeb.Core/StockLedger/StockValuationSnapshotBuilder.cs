using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public static class StockValuationSnapshotBuilder
{
    private static readonly StockValuationMovementClassifier MovementClassifier = new();

    public static async Task<string?> AppendAsync(
        AppDbContext db,
        string company,
        string branch,
        DateTime periodFrom,
        DateTime periodTo,
        string createdBy,
        CancellationToken cancellationToken)
    {
        var epoch = await db.StockLedgerEpochs.SingleOrDefaultAsync(x =>
            x.CompanyCode == company && x.BranchCode == branch
            && x.Status == StockLedgerEpochStatuses.Active, cancellationToken);
        if (epoch is null)
            return null;

        var exclusiveEnd = periodTo.Date.AddDays(1);
        // A replay root can be dated after this period while its correction
        // belongs to an older, still-open business period. Include its sequence
        // when taking the snapshot watermark so the older fact is not omitted.
        var watermark = await db.StockPostings.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                        && x.SealedAtUtc != null
                        && (x.EffectiveAt < exclusiveEnd || db.StockValuationFacts.Any(f =>
                            f.CompanyCode == company && f.BranchCode == branch
                            && f.StockPostingId == x.Id && f.EffectiveAt < exclusiveEnd)))
            .MaxAsync(x => (long?)x.PostingSequence, cancellationToken) ?? 0L;

        var unvalued = await db.StockValuationFacts.AsNoTracking().CountAsync(x =>
            x.CompanyCode == company && x.BranchCode == branch
            && x.EffectiveAt < exclusiveEnd
            && x.StockPosting!.PostingSequence <= watermark
            && x.StockPosting.SealedAtUtc != null
            && x.ValuationStatus == StockValuationStatuses.Unvalued, cancellationToken);
        if (unvalued > 0)
            return $"Cannot close: {unvalued} required valuation fact(s) remain UNVALUED.";

        var facts = await db.StockValuationFacts.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                        && x.EffectiveAt < exclusiveEnd
                        && x.StockPosting!.PostingSequence <= watermark
                        && x.StockPosting.SealedAtUtc != null)
            .Select(x => new
            {
                x.Id, x.StockPostingId, x.InventoryHistoryId,
                x.ItemCode, x.BaseUom, x.CostMethod, x.EffectiveAt,
                x.Direction, x.BaseQty, x.CostAmount, x.MovementCode
            })
            .ToListAsync(cancellationToken);

        var fifoCorrectionRoots = facts.Where(f =>
                f.MovementCode == StockReplayFifoReversedIssueGenerationReader.ValueMovement)
            .Select(f => f.StockPostingId).Distinct().ToArray();
        if (fifoCorrectionRoots.Length != 0)
        {
            try
            {
                await StockReplayFifoReversedIssueGenerationReader.ValidateForSnapshotAsync(
                    db, company, branch, fifoCorrectionRoots, cancellationToken);
            }
            catch (StockLedgerException)
            {
                return "Cannot close: a reversed FIFO issue has incomplete correction evidence.";
            }
            catch (System.Data.Common.DbException)
            {
                return "Cannot close: reversed FIFO correction evidence is unavailable.";
            }
        }

        var saleFactRoots = facts.Where(f =>
                f.MovementCode == StockValuationMovementClassifier.ReversedSaleReplayValue)
            .Select(f => f.StockPostingId).Distinct().ToArray();
        var saleEffectRoots = await db.StockReplayEffects.AsNoTracking().Where(e =>
                e.CompanyCode == company && e.BranchCode == branch
                && e.EffectKind == StockReplayEffectTypes.SalesReversalValue
                && db.StockPostings.Any(p => p.Id == e.RootStockPostingId
                    && p.CompanyCode == company && p.BranchCode == branch
                    && p.SealedAtUtc != null && p.PostingSequence <= watermark
                    && p.EffectiveAt < exclusiveEnd))
            .Select(e => e.RootStockPostingId).Distinct().ToArrayAsync(cancellationToken);
        var undoRoots = facts.Where(f => f.MovementCode == StockValuationMovementClassifier.ReversedSaleReplayValueUndo)
            .Select(f => f.StockPostingId).Distinct().ToArray();
        var undoEffectRoots = await db.StockReplayEffects.AsNoTracking().Where(e =>
                e.CompanyCode == company && e.BranchCode == branch
                && e.EffectKind == StockReplayEffectTypes.SalesReversalValueUndo
                && db.StockPostings.Any(p => p.Id == e.RootStockPostingId
                    && p.CompanyCode == company && p.BranchCode == branch
                    && p.SealedAtUtc != null && p.PostingSequence <= watermark
                    && p.EffectiveAt < exclusiveEnd))
            .Select(e => e.RootStockPostingId).Distinct().ToArrayAsync(cancellationToken);
        var undoParents = new List<long>();
        foreach (var undoRoot in undoRoots.Concat(undoEffectRoots).Distinct())
        {
            var undoValues = await db.StockValuationFacts.AsNoTracking().Where(f =>
                f.StockPostingId == undoRoot && f.MovementCode == StockValuationMovementClassifier.ReversedSaleReplayValueUndo)
                .ToArrayAsync(cancellationToken);
            if (undoValues.Length != 2 || undoValues.Any(v => v.ReversesValuationFactId is not > 0))
                return "Cannot close: a reversed sale has incomplete cost correction evidence.";
            var targets = undoValues.Select(v => v.ReversesValuationFactId!.Value).ToArray();
            var parents = await db.StockValuationFacts.AsNoTracking().Where(f => targets.Contains(f.Id)
                && f.MovementCode == StockValuationMovementClassifier.ReversedSaleReplayValue)
                .Select(f => f.StockPostingId).ToArrayAsync(cancellationToken);
            if (parents.Length != 2 || parents.Distinct().Count() != 1)
                return "Cannot close: a reversed sale has incomplete cost correction evidence.";
            undoParents.Add(parents[0]);
        }
        if (!await ValidateReversedSaleCorrectionsAsync(db, company, branch,
                saleFactRoots.Concat(saleEffectRoots).Concat(undoParents).Distinct().ToArray(), cancellationToken))
            return "Cannot close: a reversed sale has incomplete cost correction evidence.";

        var revision = (await db.StockValuationPeriodSnapshotHdrs.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                        && x.PeriodKey == periodFrom.ToString("yyyy-MM"))
            .MaxAsync(x => (int?)x.Revision, cancellationToken) ?? 0) + 1;
        var header = new StockValuationPeriodSnapshotHdr
        {
            CompanyCode = company,
            BranchCode = branch,
            LedgerEpochId = epoch.Id,
            PeriodKey = periodFrom.ToString("yyyy-MM"),
            Revision = revision,
            PostingSequenceWatermark = watermark,
            SourceDataHash = new string('0', 64),
            ValuationStatus = "SEALED",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = createdBy
        };

        foreach (var group in facts.GroupBy(x => new { x.ItemCode, x.BaseUom, x.CostMethod })
                     .OrderBy(x => x.Key.ItemCode, StringComparer.OrdinalIgnoreCase))
        {
            var opening = group.Where(x => x.EffectiveAt < periodFrom).ToArray();
            var period = group.Where(x => x.EffectiveAt >= periodFrom).ToArray();
            var adjustments = period.Where(x => MovementClassifier.IsValueOnlyAdjustment(x.MovementCode)).ToArray();
            var normal = period.Except(adjustments).ToArray();
            var line = new StockValuationPeriodSnapshotLine
            {
                ItemCode = group.Key.ItemCode,
                BaseUom = group.Key.BaseUom,
                CostMethod = group.Key.CostMethod,
                OpeningQty = opening.Sum(x => x.BaseQty * x.Direction),
                OpeningValue = opening.Sum(x => x.CostAmount * x.Direction),
                InQty = normal.Where(x => x.Direction > 0).Sum(x => x.BaseQty),
                InValue = normal.Where(x => x.Direction > 0).Sum(x => x.CostAmount),
                AdjustmentQty = adjustments.Sum(x => x.BaseQty * x.Direction),
                AdjustmentValue = adjustments.Sum(x => x.CostAmount * x.Direction),
                OutQty = normal.Where(x => x.Direction < 0).Sum(x => x.BaseQty),
                OutValue = normal.Where(x => x.Direction < 0).Sum(x => x.CostAmount)
            };
            line.ClosingQty = line.OpeningQty + line.InQty + line.AdjustmentQty - line.OutQty;
            line.ClosingValue = line.OpeningValue + line.InValue + line.AdjustmentValue - line.OutValue;
            if (line.ClosingQty < 0m || line.ClosingValue < 0m)
                return $"Cannot close: valuation pool '{line.ItemCode}' has negative closing quantity/value.";
            if (line.ClosingQty == 0m && line.ClosingValue != 0m)
                return $"Cannot close: valuation pool '{line.ItemCode}' has zero quantity with residual value {line.ClosingValue}.";
            header.Lines.Add(line);
        }

        var hasLaterFacts = await db.StockValuationFacts.AsNoTracking().AnyAsync(x =>
            x.CompanyCode == company && x.BranchCode == branch && x.EffectiveAt >= exclusiveEnd
            && x.StockPosting!.SealedAtUtc != null, cancellationToken);
        if (!hasLaterFacts)
        {
            var states = await db.StockCostStates.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.BranchCode == branch)
                .ToListAsync(cancellationToken);
            foreach (var line in header.Lines)
            {
                var state = states.SingleOrDefault(x =>
                    x.ItemCode == line.ItemCode && x.CostMethod == line.CostMethod);
                if (state is null || state.OnHandBaseQty != line.ClosingQty || state.InventoryValue != line.ClosingValue)
                    return $"Cannot close: current cost state does not reconcile for item '{line.ItemCode}'.";
            }

            var fifoStates = states
                .Where(x => x.CostMethod == StockCostMethods.Fifo)
                .ToArray();
            if (fifoStates.Length > 0)
            {
                var fifoItemCodes = fifoStates.Select(x => x.ItemCode).ToArray();
                var fifoLayers = await db.StockFifoLayers.AsNoTracking()
                    .Where(x => x.CompanyCode == company
                                && x.BranchCode == branch
                                && fifoItemCodes.Contains(x.ItemCode)
                                && x.Status == StockFifoLayerStatuses.Open)
                    .ToListAsync(cancellationToken);
                foreach (var state in fifoStates)
                {
                    var qty = decimal.Round(
                        fifoLayers.Where(x => x.ItemCode == state.ItemCode).Sum(x => x.RemainingQty),
                        6, MidpointRounding.AwayFromZero);
                    var value = decimal.Round(
                        fifoLayers.Where(x => x.ItemCode == state.ItemCode).Sum(x => x.RemainingValue),
                        6, MidpointRounding.AwayFromZero);
                    if (qty != state.OnHandBaseQty)
                        return $"Cannot close: FIFO_LAYER_QTY_MISMATCH for item '{state.ItemCode}'.";
                    if (value != state.InventoryValue)
                        return $"Cannot close: FIFO_LAYER_VALUE_MISMATCH for item '{state.ItemCode}'.";
                }

                var fifoIssueFacts = facts
                    .Where(x => x.CostMethod == StockCostMethods.Fifo
                                && x.Direction < 0
                                && x.BaseQty > 0m
                                && !string.Equals(x.MovementCode, "COST_METHOD_CUTOVER_OUT", StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.Id)
                    .ToArray();
                if (fifoIssueFacts.Length > 0)
                {
                    var activeIssueFacts = await db.StockValuationFacts.AsNoTracking()
                        .Where(x => fifoIssueFacts.Contains(x.Id)
                                    // A reversed receipt has an outbound direction, but it is not an
                                    // issue and never consumes a FIFO layer. Only original active
                                    // issue generations require consumption evidence.
                                    && x.ReversesValuationFactId == null
                                    && !db.StockValuationFacts.Any(reversal =>
                                        reversal.ReversesValuationFactId == x.Id))
                        .Select(x => new { x.Id, x.BaseQty, x.CostAmount })
                        .ToListAsync(cancellationToken);
                    var consumptionTotals = await db.StockFifoLayerConsumptions.AsNoTracking()
                        .Where(x => fifoIssueFacts.Contains(x.IssueValuationFactId)
                                    && x.ReversesConsumptionId == null)
                        .GroupBy(x => x.IssueValuationFactId)
                        .Select(x => new
                        {
                            IssueFactId = x.Key,
                            Qty = x.Sum(y => y.ConsumedQty),
                            Value = x.Sum(y => y.ConsumedValue)
                        })
                        .ToDictionaryAsync(x => x.IssueFactId, cancellationToken);
                    foreach (var issue in activeIssueFacts)
                    {
                        if (!consumptionTotals.TryGetValue(issue.Id, out var total)
                            || decimal.Round(total.Qty, 6, MidpointRounding.AwayFromZero)
                                != decimal.Round(issue.BaseQty, 6, MidpointRounding.AwayFromZero)
                            || decimal.Round(total.Value, 6, MidpointRounding.AwayFromZero)
                                != decimal.Round(issue.CostAmount, 6, MidpointRounding.AwayFromZero))
                            return $"Cannot close: FIFO_CONSUMPTION_MISMATCH for valuation fact {issue.Id}.";
                    }
                }
            }
        }

        var standardVarianceRows = await db.ProductionStandardCostVariances.AsNoTracking()
            .Where(x => x.CompanyCode == company
                        && x.BranchCode == branch
                        && x.EffectiveAt < exclusiveEnd)
            .ToListAsync(cancellationToken);
        foreach (var variance in standardVarianceRows)
        {
            if (decimal.Round(variance.ActualProductionValue, 6, MidpointRounding.AwayFromZero)
                != decimal.Round(variance.StandardInventoryValue + variance.VarianceAmount,
                    6, MidpointRounding.AwayFromZero))
                return $"Cannot close: PRODUCTION_STANDARD_VARIANCE_MISMATCH for item '{variance.ItemCode}'.";
        }

        var standardFgFactIds = facts
            .Where(x => x.CostMethod == StockCostMethods.Standard
                        && x.Direction > 0
                        && string.Equals(x.MovementCode, "FINISHED_GOOD_IN", StringComparison.OrdinalIgnoreCase)
                        && x.InventoryHistoryId is not null)
            .Select(x => x.Id)
            .ToArray();
        if (standardFgFactIds.Length > 0)
        {
            var requiredStandardFgFacts = await db.StockValuationFacts.AsNoTracking()
                .Where(x => standardFgFactIds.Contains(x.Id)
                            && db.IvTrxHistories.Any(h => h.Id == x.InventoryHistoryId
                                && h.TrxType == IvTrxTypes.FinishedGoods
                                && h.ExactTransferredValue != null))
                .Select(x => x.Id)
                .ToArrayAsync(cancellationToken);
            var varianceFactIds = await db.ProductionStandardCostVariances.AsNoTracking()
                .Where(x => requiredStandardFgFacts.Contains(x.InventoryValuationFactId))
                .Select(x => x.InventoryValuationFactId)
                .Distinct()
                .ToArrayAsync(cancellationToken);
            if (requiredStandardFgFacts.Any(x => !varianceFactIds.Contains(x)))
                return "Cannot close: a STANDARD finished-good receipt has no ProductionStandardCostVariance evidence.";
        }

        var quantitySnapshot = db.ChangeTracker.Entries<StockPeriodSnapshotHdr>()
            .Where(x => x.State == EntityState.Added
                        && x.Entity.CompanyCode == company
                        && x.Entity.BranchCode == branch
                        && x.Entity.PeriodKey == header.PeriodKey)
            .Select(x => x.Entity)
            .SingleOrDefault();
        if (quantitySnapshot is not null)
        {
            var physicalByItem = quantitySnapshot.Lines
                .Where(x => x.LedgerArea == "INVENTORY")
                .GroupBy(x => x.ItemCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Sum(y => y.BaseQty), StringComparer.OrdinalIgnoreCase);
            foreach (var line in header.Lines.Where(x => x.ClosingQty != 0m))
            {
                if (physicalByItem.GetValueOrDefault(line.ItemCode) != line.ClosingQty)
                    return $"Cannot close: physical and valued quantity do not reconcile for item '{line.ItemCode}'.";
            }
            foreach (var physical in physicalByItem.Where(x => x.Value != 0m))
            {
                if (!header.Lines.Any(x => string.Equals(x.ItemCode, physical.Key, StringComparison.OrdinalIgnoreCase)))
                    return $"Cannot close: physical stock for item '{physical.Key}' has no valuation facts.";
            }
        }

        header.SourceDataHash = StockPostingFingerprint.Hash(StockPostingFingerprint.Canonicalize(
            System.Text.Json.JsonSerializer.Serialize(new
            {
                company, branch, header.PeriodKey, revision, watermark,
                lines = header.Lines.OrderBy(x => x.ItemCode, StringComparer.Ordinal)
                    .Select(x => new
                    {
                        x.ItemCode, x.BaseUom, x.CostMethod,
                        x.OpeningQty, x.OpeningValue, x.InQty, x.InValue,
                        x.AdjustmentQty, x.AdjustmentValue, x.OutQty, x.OutValue,
                        x.ClosingQty, x.ClosingValue
                    }).ToArray()
            })));
        db.StockValuationPeriodSnapshotHdrs.Add(header);
        return null;
    }

    internal static async Task<bool> ValidateReversedSaleCorrectionsAsync(
        AppDbContext db, string company, string branch, IReadOnlyCollection<long> rootIds, CancellationToken ct)
    {
        var pendingRoots = new Queue<long>(rootIds);
        var checkedRoots = new HashSet<long>();
        var checkedPairs = new HashSet<(long Sale, long Reversal)>();
        while (pendingRoots.TryDequeue(out var rootId))
        {
            if (!checkedRoots.Add(rootId))
                continue;
            var root = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(p => p.Id == rootId, ct);
            var runs = await db.StockReplayRuns.AsNoTracking().Where(r => r.RootStockPostingId == rootId).ToArrayAsync(ct);
            var values = await db.StockValuationFacts.AsNoTracking().Where(f => f.StockPostingId == rootId
                && f.MovementCode == StockValuationMovementClassifier.ReversedSaleReplayValue).ToArrayAsync(ct);
            if (root is null || root.CompanyCode != company || root.BranchCode != branch || root.SealedAtUtc is null
                || root.SourceModule != "INVENTORY" || root.SourceDocumentType != IvTrxTypes.MiscellaneousReceipt
                || root.PostingRole is not ("PRIMARY" or "REVERSAL")
                || root.PostingRole == "PRIMARY" && root.ReversesPostingId is not null
                || root.PostingRole == "REVERSAL" && root.ReversesPostingId is not > 0
                || runs.Length != 1 || runs[0].CompanyCode != company || runs[0].BranchCode != branch
                || runs[0].RootRequestId != root.RequestId || runs[0].RootDocumentType != root.SourceDocumentType
                || runs[0].RootDocumentId != root.SourceDocumentId
                || runs[0].AffectedItemCount != 1 || runs[0].AffectedProductionMovementCount != 0
                || runs[0].PlanHash.Length != 64 || runs[0].EarliestEffectiveAt != root.EffectiveAt
                || values.Length != 2 || values.Any(v => v.OriginalValuationFactId is not > 0)
                || values.Select(v => v.OriginalValuationFactId).Distinct().Count() != 2)
                return false;
            var sourceIds = values.Select(v => v.OriginalValuationFactId!.Value).ToArray();
            var sources = await db.StockValuationFacts.AsNoTracking().Where(f => sourceIds.Contains(f.Id)).ToArrayAsync(ct);
            if (sources.Length != 2)
                return false;
            var sales = sources.Where(f => f.MovementCode == "SALE_OUT" && f.Direction == -1).ToArray();
            var reversals = sources.Where(f => f.MovementCode == "SALE_OUT_REVERSAL" && f.Direction == 1).ToArray();
            if (sales.Length != 1 || reversals.Length != 1)
                return false;
            var sale = sales[0];
            var reverse = reversals[0];
            if (sale.InventoryHistoryId is not > 0 || reverse.InventoryHistoryId is not > 0
                || sale.CompanyCode != company || sale.BranchCode != branch || reverse.CompanyCode != company || reverse.BranchCode != branch
                || sale.LedgerEpochId != root.LedgerEpochId || reverse.LedgerEpochId != root.LedgerEpochId
                || sale.SourceDocumentType != "SA_INVOICE" || reverse.SourceDocumentType != sale.SourceDocumentType
                || reverse.SourceDocumentId != sale.SourceDocumentId || reverse.SourceDocumentNo != sale.SourceDocumentNo
                || reverse.SourceDocumentLine != sale.SourceDocumentLine || sale.CostMethod != StockCostMethods.MovingAverage
                || reverse.CostMethod != sale.CostMethod || sale.BaseQty <= 0m || reverse.BaseQty != sale.BaseQty
                || sale.ItemCode != reverse.ItemCode || sale.BaseUom != reverse.BaseUom
                || sale.BaseCurrency != reverse.BaseCurrency || sale.CostAmount < 0m || reverse.CostAmount != sale.CostAmount
                || reverse.UnitCost != sale.UnitCost
                || sale.BaseCostAmount != sale.CostAmount || reverse.BaseCostAmount != reverse.CostAmount
                || sale.TransactionCurrency is not null || sale.TransactionCostAmount is not null || sale.ExchangeRate is not null
                || reverse.TransactionCurrency is not null || reverse.TransactionCostAmount is not null || reverse.ExchangeRate is not null
                || sale.ValuationStatus != StockValuationStatuses.Valued || reverse.ValuationStatus != StockValuationStatuses.Reversed
                || sale.ValuationSource != StockValuationSources.MovingAverage
                || reverse.ValuationSource != StockValuationSources.OriginalReversal
                || reverse.ReversesValuationFactId != sale.Id || reverse.OriginalValuationFactId != sale.Id
                || root.EffectiveAt >= sale.EffectiveAt || sale.EffectiveAt > reverse.EffectiveAt
                || root.EffectiveAt.Year != sale.EffectiveAt.Year || root.EffectiveAt.Month != sale.EffectiveAt.Month
                || sale.EffectiveAt.Year != reverse.EffectiveAt.Year || sale.EffectiveAt.Month != reverse.EffectiveAt.Month
                || runs[0].EarliestEffectiveAt > sale.EffectiveAt || runs[0].LatestEffectiveAt < reverse.EffectiveAt
                || await db.StockValuationFacts.AsNoTracking().CountAsync(f => f.ReversesValuationFactId == sale.Id, ct) != 1)
                return false;
            var histories = await db.IvTrxHistories.AsNoTracking().Where(h =>
                h.Id == sale.InventoryHistoryId || h.Id == reverse.InventoryHistoryId).ToArrayAsync(ct);
            var saleHistory = histories.SingleOrDefault(h => h.Id == sale.InventoryHistoryId);
            var reverseHistory = histories.SingleOrDefault(h => h.Id == reverse.InventoryHistoryId);
            var sourceOwnerIds = new[] { sale.StockPostingId, reverse.StockPostingId };
            var owners = await db.StockPostings.AsNoTracking().Where(p => sourceOwnerIds.Contains(p.Id)).ToArrayAsync(ct);
            if (saleHistory is null || reverseHistory is null || histories.Length != 2 || owners.Length != 2
                || saleHistory.CompanyCode != company || saleHistory.BranchCode != branch
                || reverseHistory.CompanyCode != company || reverseHistory.BranchCode != branch
                || saleHistory.TrxType != IvTrxTypes.SalesOut || saleHistory.EntryRole != "ORIGINAL"
                || reverseHistory.EntryRole != "REVERSAL" || reverseHistory.ReversesHistoryId != saleHistory.Id
                || saleHistory.StockPostingId != sale.StockPostingId || reverseHistory.StockPostingId != reverse.StockPostingId
                || saleHistory.TrxDtTime != sale.EffectiveAt || reverseHistory.TrxDtTime != reverse.EffectiveAt
                || owners.Any(p => p.CompanyCode != company || p.BranchCode != branch
                    || p.LedgerEpochId != root.LedgerEpochId || p.SealedAtUtc is null))
                return false;
            var saleOwner = owners.Single(p => p.Id == sale.StockPostingId);
            var reverseOwner = owners.Single(p => p.Id == reverse.StockPostingId);
            if (saleOwner.PostingRole != "PRIMARY" || reverseOwner.PostingRole != "REVERSAL"
                || saleOwner.SourceModule != "INVENTORY" || reverseOwner.SourceModule != "INVENTORY"
                || saleOwner.SourceDocumentType != IvTrxTypes.SalesOut || reverseOwner.SourceDocumentType != IvTrxTypes.SalesOut
                || sale.EffectiveAt == reverse.EffectiveAt && saleOwner.PostingSequence >= reverseOwner.PostingSequence
                || runs[0].PlanWatermark < Math.Max(saleOwner.PostingSequence, reverseOwner.PostingSequence)
                || runs[0].PlanWatermark >= root.PostingSequence
                || reverseOwner.ReversesPostingId != saleOwner.Id)
                return false;
            var valueIds = values.Select(v => v.Id).ToArray();
            var effects = await db.StockReplayEffects.AsNoTracking().Where(e =>
                e.AffectedEntityType == StockReplayEffectTypes.StockValuationFact
                && valueIds.Contains(e.AffectedEntityId)).ToArrayAsync(ct);
            var receiptRoots = await db.IvTrxHistories.AsNoTracking().Where(h => h.StockPostingId == rootId).ToArrayAsync(ct);
            if (receiptRoots.Length != 1)
                return false;
            if (!await ValidateReversedSaleRootIssueAsync(db, company, branch, root, receiptRoots[0],
                    sale, reverse, runs[0], ct))
                return false;
            var receiptBatchId = await db.IvTrxBatches.AsNoTracking().Where(b => b.CompanyCode == company
                    && b.BranchCode == branch && b.BatchNo == receiptRoots[0].BatchNo
                    && b.TrxType == IvTrxTypes.MiscellaneousReceipt)
                .Select(b => (int?)b.Id).SingleOrDefaultAsync(ct);
            if (receiptBatchId is not int batchId
                || batchId.ToString(System.Globalization.CultureInfo.InvariantCulture) != root.SourceDocumentId
                || receiptRoots[0].TrxType != IvTrxTypes.MiscellaneousReceipt
                || receiptRoots[0].CompanyCode != company || receiptRoots[0].BranchCode != branch
                || receiptRoots[0].LedgerEpochId != root.LedgerEpochId || receiptRoots[0].TrxDtTime != root.EffectiveAt
                || receiptRoots[0].BatchNo.ToString(System.Globalization.CultureInfo.InvariantCulture) != root.SourceDocumentNo
                || effects.Length != 2 || values.Any(v => !Matches(v, sources.Single(s => s.Id == v.OriginalValuationFactId)))
                || values.Sum(v => v.SignedValue) != 0m)
                return false;
            if (root.PostingRole == "REVERSAL")
            {
                if (receiptRoots[0].EntryRole != "REVERSAL" || receiptRoots[0].ReversesHistoryId is not int receiptId
                    || !await db.IvTrxHistories.AsNoTracking().AnyAsync(h => h.Id == receiptId
                        && h.CompanyCode == company && h.BranchCode == branch
                        && h.BatchNo == receiptRoots[0].BatchNo && h.TrxType == IvTrxTypes.MiscellaneousReceipt
                        && h.EntryRole == "ORIGINAL" && h.StockPostingId == root.ReversesPostingId
                        && db.StockPostings.Any(p => p.Id == h.StockPostingId && p.CompanyCode == company
                            && p.BranchCode == branch && p.LedgerEpochId == root.LedgerEpochId && p.SealedAtUtc != null
                            && p.SourceModule == root.SourceModule && p.SourceDocumentType == root.SourceDocumentType
                            && p.SourceDocumentId == root.SourceDocumentId && p.SourceDocumentNo == root.SourceDocumentNo), ct))
                    return false;
            }
            else
            {
                var receiptFacts = await db.StockValuationFacts.AsNoTracking().Where(f =>
                    f.InventoryHistoryId == receiptRoots[0].Id).ToArrayAsync(ct);
                if (receiptRoots[0].EntryRole != "ORIGINAL" || receiptRoots[0].ReversesHistoryId is not null
                    || receiptRoots[0].ICode != sale.ItemCode || receiptRoots[0].ToBalLocId is not > 0
                    || receiptRoots[0].ToStdQty is not > 0m || receiptFacts.Length != 1
                    || receiptFacts[0].StockPostingId != rootId || receiptFacts[0].LedgerEpochId != root.LedgerEpochId
                    || receiptFacts[0].ItemCode != sale.ItemCode || receiptFacts[0].ToBalLocId != receiptRoots[0].ToBalLocId
                    || receiptFacts[0].BaseQty != receiptRoots[0].ToStdQty || receiptFacts[0].Direction != 1
                    || receiptFacts[0].CostMethod != StockCostMethods.MovingAverage
                    || receiptFacts[0].ValuationStatus != StockValuationStatuses.Valued)
                    return false;
            }
            if (checkedPairs.Add((sale.Id, reverse.Id)))
            {
                var relatedValues = await db.StockValuationFacts.AsNoTracking().Where(f =>
                    f.MovementCode == StockValuationMovementClassifier.ReversedSaleReplayValue
                    && f.OriginalValuationFactId.HasValue && sourceIds.Contains(f.OriginalValuationFactId.Value))
                    .ToArrayAsync(ct);
                var relatedIds = relatedValues.Select(v => v.Id).ToArray();
                var undoValues = await db.StockValuationFacts.AsNoTracking().Where(f =>
                    f.ReversesValuationFactId.HasValue && relatedIds.Contains(f.ReversesValuationFactId.Value)).ToArrayAsync(ct);
                var groups = relatedValues.GroupBy(v => v.StockPostingId).ToArray();
                var spans = new List<(long Start, long End)>();
                var activeCount = 0;
                foreach (var group in groups)
                {
                    var pair = group.ToArray();
                    if (pair.Length != 2 || pair.Select(v => v.OriginalValuationFactId).Distinct().Count() != 2)
                        return false;
                    var pairRoot = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(p => p.Id == group.Key, ct);
                    if (pairRoot is null || pairRoot.SourceDocumentId != root.SourceDocumentId
                        || pairRoot.SourceDocumentNo != root.SourceDocumentNo || pairRoot.PostingRole != root.PostingRole
                        || pairRoot.EffectiveAt != root.EffectiveAt)
                        return false;
                    pendingRoots.Enqueue(group.Key);
                    var children = undoValues.Where(v => pair.Any(p => p.Id == v.ReversesValuationFactId)).ToArray();
                    if (children.Length == 0)
                    {
                        activeCount++;
                        spans.Add((pairRoot.PostingSequence, long.MaxValue));
                    }
                    else
                    {
                        if (children.Length != 2 || children.Select(v => v.StockPostingId).Distinct().Count() != 1
                            || !await ValidateReversedSaleUndoAsync(db, company, branch, pairRoot, pair, sources, children, ct))
                            return false;
                        var undoSequence = await db.StockPostings.AsNoTracking().Where(p => p.Id == children[0].StockPostingId)
                            .Select(p => p.PostingSequence).SingleAsync(ct);
                        spans.Add((pairRoot.PostingSequence, undoSequence));
                    }
                }
                if (activeCount > 1 || spans.OrderBy(s => s.Start).Zip(spans.OrderBy(s => s.Start).Skip(1),
                        (previous, next) => previous.End >= next.Start).Any(overlap => overlap))
                    return false;
            }
            var saleValue = values.Single(v => v.OriginalValuationFactId == sale.Id);
            var reverseValue = values.Single(v => v.OriginalValuationFactId == reverse.Id);
            var correctedSaleCost = sale.CostAmount - saleValue.SignedValue;
            if (correctedSaleCost < 0m || reverse.CostAmount + reverseValue.SignedValue != correctedSaleCost)
                return false;

            bool Matches(StockValuationFact value, StockValuationFact source) =>
                value.CompanyCode == company && value.BranchCode == branch && value.LedgerEpochId == root.LedgerEpochId
                && value.InventoryHistoryId is null && value.BaseQty == 0m && value.Direction is -1 or 1
                && value.CostMethod == StockCostMethods.MovingAverage && value.CostAmount > 0m
                && value.BaseCostAmount == value.CostAmount && value.ValuationStatus == StockValuationStatuses.Valued
                && value.TransactionCurrency is null && value.TransactionCostAmount is null && value.ExchangeRate is null
                && value.ValuationSource == StockValuationSources.MovingAverage && value.ReversesValuationFactId is null
                && value.ValuationVersion == source.ValuationVersion + 1
                && value.UnitCost == Costing.StockLedgerPrecision.Money(value.CostAmount / sale.BaseQty)
                && value.EffectiveAt == source.EffectiveAt && value.BusinessDate == source.BusinessDate
                && value.PeriodKey == source.PeriodKey && value.ItemCode == source.ItemCode && value.BaseUom == source.BaseUom
                && value.WarehouseCode == source.WarehouseCode && value.LocationCode == source.LocationCode
                && value.LotId == source.LotId && value.LotNo == source.LotNo && value.ItemStatus == source.ItemStatus
                && value.FromBalLocId == source.FromBalLocId && value.ToBalLocId == source.ToBalLocId
                && value.BaseCurrency == source.BaseCurrency && value.SourceLineId == source.SourceLineId
                && value.SourceDocumentType == source.SourceDocumentType && value.SourceDocumentId == source.SourceDocumentId
                && value.SourceDocumentNo == source.SourceDocumentNo && value.SourceDocumentLine == source.SourceDocumentLine
                && effects.Count(e => e.CompanyCode == company && e.BranchCode == branch && e.ReplayRunId == runs[0].Id
                    && e.RootStockPostingId == rootId && e.BusinessOwnerPostingId == source.StockPostingId
                    && e.EffectKind == StockReplayEffectTypes.SalesReversalValue
                    && e.AffectedEntityId == value.Id && e.OriginalEntityId == source.Id) == 1;
        }
        return true;
    }

    private static async Task<bool> ValidateReversedSaleRootIssueAsync(
        AppDbContext db, string company, string branch, StockPosting root, IvTrxHistory receipt,
        StockValuationFact sale, StockValuationFact reverse, StockReplayRun run, CancellationToken ct)
    {
        var rootFacts = await db.StockValuationFacts.AsNoTracking().Where(f => f.StockPostingId == root.Id).ToArrayAsync(ct);
        var receiptFacts = rootFacts.Where(f => f.InventoryHistoryId == receipt.Id).ToArray();
        var saleValues = rootFacts.Where(f => f.MovementCode is
            StockValuationMovementClassifier.ReversedSaleReplayValue or StockValuationMovementClassifier.ReversedSaleReplayValueUndo).ToArray();
        if (receiptFacts.Length != 1 || saleValues.Length != 2)
            return false;
        var other = rootFacts.Where(f => f.InventoryHistoryId != receipt.Id
            && f.MovementCode is not (StockValuationMovementClassifier.ReversedSaleReplayValue
                or StockValuationMovementClassifier.ReversedSaleReplayValueUndo)).ToArray();
        if (other.Length == 0)
            return rootFacts.Length == 3 && run.AffectedHistoryCount == 2;
        if (rootFacts.Length != 5 || other.Length != 2 || run.AffectedHistoryCount != 3
            || other.Any(f => f.InventoryHistoryId is not > 0)
            || other.Select(f => f.InventoryHistoryId).Distinct().Count() != 1)
            return false;
        var issueId = other[0].InventoryHistoryId!.Value;
        var issue = await db.IvTrxHistories.AsNoTracking().SingleOrDefaultAsync(h => h.Id == issueId, ct);
        var reversal = other.SingleOrDefault(f => f.ReversesValuationFactId is not null);
        var replacement = other.SingleOrDefault(f => f.ReversesValuationFactId is null);
        if (issue is null || reversal?.ReversesValuationFactId is not long priorId || replacement is null
            || issue.StockPostingId is not > 0)
            return false;
        var prior = await db.StockValuationFacts.AsNoTracking().SingleOrDefaultAsync(f => f.Id == priorId, ct);
        var issueOwner = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(p => p.Id == issue.StockPostingId, ct);
        var reverseOwner = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(p => p.Id == reverse.StockPostingId, ct);
        if (prior is null || issueOwner is null || reverseOwner is null)
            return false;
        var priorOwner = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(p => p.Id == prior.StockPostingId, ct);
        var originId = prior.OriginalValuationFactId ?? prior.Id;
        var origin = await db.StockValuationFacts.AsNoTracking().SingleOrDefaultAsync(f => f.Id == originId, ct);
        var issueBatchId = await db.IvTrxBatches.AsNoTracking().Where(b => b.CompanyCode == company
                && b.BranchCode == branch && b.BatchNo == issue.BatchNo && b.TrxType == IvTrxTypes.MiscellaneousIssue)
            .Select(b => (int?)b.Id).SingleOrDefaultAsync(ct);
        if (priorOwner is null || origin is null || issueBatchId is not int batchId
            || issue.CompanyCode != company || issue.BranchCode != branch
            || issue.LedgerEpochId != root.LedgerEpochId || issue.TrxType != IvTrxTypes.MiscellaneousIssue
            || issue.EntryRole != "ORIGINAL" || issue.BatchStatus != IvBatchStatuses.Posted
            || issue.ICode != sale.ItemCode || issue.FromBalLocId != sale.FromBalLocId
            || issue.FrStdQty is not > 0m || issue.ToBalLocId is not null
            || issue.TrxDtTime < reverse.EffectiveAt
            || issue.TrxDtTime == reverse.EffectiveAt && issueOwner.PostingSequence <= reverseOwner.PostingSequence
            || issue.TrxDtTime.Year != root.EffectiveAt.Year || issue.TrxDtTime.Month != root.EffectiveAt.Month
            || issueOwner.CompanyCode != company || issueOwner.BranchCode != branch
            || issueOwner.LedgerEpochId != root.LedgerEpochId || issueOwner.SealedAtUtc is null
            || issueOwner.SourceModule != "INVENTORY" || issueOwner.SourceDocumentType != IvTrxTypes.MiscellaneousIssue
            || issueOwner.PostingRole != "PRIMARY" || issueOwner.EffectiveAt != issue.TrxDtTime
            || issueOwner.CommandType != $"IV_POST_{IvTrxTypes.MiscellaneousIssue}"
            || issueOwner.SourceDocumentId != batchId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || issueOwner.SourceDocumentNo != issue.BatchNo.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || priorOwner.CompanyCode != company || priorOwner.BranchCode != branch
            || priorOwner.LedgerEpochId != root.LedgerEpochId || priorOwner.SealedAtUtc is null
            || priorOwner.PostingSequence >= root.PostingSequence
            || priorOwner.Id != issueOwner.Id && (priorOwner.SourceModule != root.SourceModule
                || priorOwner.SourceDocumentType != root.SourceDocumentType
                || priorOwner.SourceDocumentId != root.SourceDocumentId || priorOwner.SourceDocumentNo != root.SourceDocumentNo
                || priorOwner.EffectiveAt != root.EffectiveAt)
            || origin.StockPostingId != issueOwner.Id || origin.InventoryHistoryId != issue.Id
            || origin.CompanyCode != company || origin.BranchCode != branch || origin.LedgerEpochId != root.LedgerEpochId
            || origin.ItemCode != issue.ICode || origin.BaseUom != issue.FrStdUom
            || origin.FromBalLocId != issue.FromBalLocId || origin.BaseQty != issue.FrStdQty
            || origin.Direction != -1 || origin.CostMethod != StockCostMethods.MovingAverage
            || origin.CostAmount < 0m || origin.BaseCostAmount != origin.CostAmount
            || origin.TransactionCurrency is not null || origin.TransactionCostAmount is not null || origin.ExchangeRate is not null
            || origin.EffectiveAt != issue.TrxDtTime || origin.BusinessDate != issueOwner.BusinessDate
            || origin.PeriodKey != issueOwner.PeriodKey || origin.MovementCode != "ISSUE_OUT"
            || origin.SourceDocumentType != issueOwner.SourceDocumentType || origin.SourceDocumentId != issueOwner.SourceDocumentId
            || origin.SourceDocumentNo != issueOwner.SourceDocumentNo
            || origin.SourceDocumentLine != issue.TrxLineNo.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || origin.PostingLineNo != issue.PostingLineNo || origin.SplitOrdinal != 0 || origin.ValuationVersion != 1
            || origin.ValuationStatus != StockValuationStatuses.Valued || origin.ValuationSource != StockValuationSources.MovingAverage
            || origin.OriginalValuationFactId is not null || origin.ReversesValuationFactId is not null
            || prior.InventoryHistoryId != issue.Id || prior.CompanyCode != company || prior.BranchCode != branch
            || prior.LedgerEpochId != root.LedgerEpochId || prior.CostMethod != StockCostMethods.MovingAverage
            || prior.ItemCode != issue.ICode || prior.FromBalLocId != issue.FromBalLocId
            || prior.BaseQty != issue.FrStdQty || prior.Direction != -1 || prior.CostAmount < 0m
            || prior.BaseCostAmount != prior.CostAmount || prior.ValuationStatus != StockValuationStatuses.Valued
            || prior.ValuationSource != StockValuationSources.MovingAverage || prior.ReversesValuationFactId is not null
            || prior.TransactionCurrency is not null || prior.TransactionCostAmount is not null || prior.ExchangeRate is not null
            || prior.SourceLineId != origin.SourceLineId || prior.SourceDocumentType != origin.SourceDocumentType
            || prior.SourceDocumentId != origin.SourceDocumentId || prior.SourceDocumentNo != origin.SourceDocumentNo
            || prior.SourceDocumentLine != origin.SourceDocumentLine || prior.MovementCode != origin.MovementCode
            || prior.BaseUom != origin.BaseUom || prior.BaseCurrency != origin.BaseCurrency
            || prior.WarehouseCode != origin.WarehouseCode || prior.LocationCode != origin.LocationCode
            || prior.LotId != origin.LotId || prior.LotNo != origin.LotNo || prior.ItemStatus != origin.ItemStatus
            || prior.ToBalLocId != origin.ToBalLocId
            || prior.EffectiveAt != issue.TrxDtTime || prior.BusinessDate != origin.BusinessDate || prior.PeriodKey != origin.PeriodKey
            || run.PlanWatermark < issueOwner.PostingSequence
            || run.PlanWatermark < priorOwner.PostingSequence || run.LatestEffectiveAt < issue.TrxDtTime
            || await db.StockValuationFacts.AsNoTracking().CountAsync(f => f.ReversesValuationFactId == prior.Id, ct) != 1)
            return false;

        bool MatchesIdentity(StockValuationFact fact) =>
            fact.CompanyCode == company && fact.BranchCode == branch && fact.LedgerEpochId == root.LedgerEpochId
            && fact.InventoryHistoryId == issue.Id && fact.ItemCode == prior.ItemCode && fact.BaseUom == prior.BaseUom
            && fact.StockPostingId == root.Id && fact.EffectiveAt == prior.EffectiveAt && fact.BusinessDate == prior.BusinessDate
            && fact.PeriodKey == prior.PeriodKey && fact.BaseQty == prior.BaseQty && fact.CostMethod == prior.CostMethod
            && fact.FromBalLocId == prior.FromBalLocId && fact.ToBalLocId == prior.ToBalLocId
            && fact.WarehouseCode == prior.WarehouseCode && fact.LocationCode == prior.LocationCode
            && fact.LotId == prior.LotId && fact.LotNo == prior.LotNo && fact.ItemStatus == prior.ItemStatus
            && fact.SourceLineId == prior.SourceLineId && fact.SourceDocumentType == prior.SourceDocumentType
            && fact.SourceDocumentId == prior.SourceDocumentId && fact.SourceDocumentNo == prior.SourceDocumentNo
            && fact.SourceDocumentLine == prior.SourceDocumentLine && fact.MovementCode == prior.MovementCode
            && fact.BaseCurrency == prior.BaseCurrency && fact.TransactionCurrency is null
            && fact.TransactionCostAmount is null && fact.ExchangeRate is null && fact.BaseCostAmount == fact.CostAmount
            && fact.OriginalValuationFactId == origin.Id && fact.SplitOrdinal == 0;
        if (!MatchesIdentity(reversal) || !MatchesIdentity(replacement)
            || reversal.Direction != 1 || reversal.CostAmount != prior.CostAmount || reversal.UnitCost != prior.UnitCost
            || reversal.ValuationSource != StockValuationSources.OriginalReversal
            || reversal.ValuationStatus != StockValuationStatuses.Reversed || reversal.ValuationVersion != prior.ValuationVersion
            || replacement.Direction != -1 || replacement.CostAmount < 0m || replacement.CostAmount == prior.CostAmount
            || replacement.UnitCost != Costing.StockLedgerPrecision.Money(replacement.CostAmount / replacement.BaseQty)
            || replacement.ValuationSource != StockValuationSources.MovingAverage
            || replacement.ValuationStatus != StockValuationStatuses.Valued
            || replacement.ValuationVersion != prior.ValuationVersion + 1 || replacement.ReversesValuationFactId is not null)
            return false;
        var ids = new[] { reversal.Id, replacement.Id };
        var effects = await db.StockReplayEffects.AsNoTracking().Where(e =>
            e.AffectedEntityType == StockReplayEffectTypes.StockValuationFact && ids.Contains(e.AffectedEntityId)).ToArrayAsync(ct);
        return effects.Length == 2 && effects.Count(e => e.CompanyCode == company && e.BranchCode == branch
            && e.ReplayRunId == run.Id && e.RootStockPostingId == root.Id && e.BusinessOwnerPostingId == issueOwner.Id
            && e.OriginalEntityId == prior.Id && e.AffectedEntityId == reversal.Id
            && e.EffectKind == StockReplayEffectTypes.WacIssueReversal) == 1
            && effects.Count(e => e.CompanyCode == company && e.BranchCode == branch
                && e.ReplayRunId == run.Id && e.RootStockPostingId == root.Id && e.BusinessOwnerPostingId == issueOwner.Id
                && e.OriginalEntityId == prior.Id && e.AffectedEntityId == replacement.Id
                && e.EffectKind == StockReplayEffectTypes.WacIssueReplacement) == 1;
    }

    private static async Task<bool> ValidateReversedSaleUndoAsync(
        AppDbContext db, string company, string branch, StockPosting priorRoot,
        StockValuationFact[] priorValues, StockValuationFact[] sources,
        StockValuationFact[] undoValues, CancellationToken ct)
    {
        var undoId = undoValues[0].StockPostingId;
        var undoRoot = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(p => p.Id == undoId, ct);
        var runs = await db.StockReplayRuns.AsNoTracking().Where(r => r.RootStockPostingId == undoId).ToArrayAsync(ct);
        var priorWasPrimary = priorRoot.PostingRole == "PRIMARY";
        if (undoRoot is null || undoRoot.CompanyCode != company || undoRoot.BranchCode != branch
            || undoRoot.LedgerEpochId != priorRoot.LedgerEpochId || undoRoot.SealedAtUtc is null
            || undoRoot.SourceModule != "INVENTORY" || undoRoot.SourceDocumentType != IvTrxTypes.MiscellaneousReceipt
            || undoRoot.SourceDocumentId != priorRoot.SourceDocumentId || undoRoot.SourceDocumentNo != priorRoot.SourceDocumentNo
            || priorRoot.PostingRole is not ("PRIMARY" or "REVERSAL")
            || undoRoot.PostingRole != (priorWasPrimary ? "REVERSAL" : "PRIMARY")
            || (priorWasPrimary ? undoRoot.ReversesPostingId != priorRoot.Id : undoRoot.ReversesPostingId is not null)
            || undoRoot.EffectiveAt != priorRoot.EffectiveAt || undoRoot.PostingSequence <= priorRoot.PostingSequence
            || runs.Length != 1 || runs[0].CompanyCode != company || runs[0].BranchCode != branch
            || runs[0].RootRequestId != undoRoot.RequestId || runs[0].RootDocumentType != undoRoot.SourceDocumentType
            || runs[0].RootDocumentId != undoRoot.SourceDocumentId
            || runs[0].AffectedItemCount != 1 || runs[0].AffectedProductionMovementCount != 0
            || runs[0].PlanHash.Length != 64 || runs[0].EarliestEffectiveAt != undoRoot.EffectiveAt
            || runs[0].LatestEffectiveAt < sources.Max(s => s.EffectiveAt)
            || runs[0].PlanWatermark < priorRoot.PostingSequence || runs[0].PlanWatermark >= undoRoot.PostingSequence
            || undoValues.Length != 2 || undoValues.Any(v => v.StockPostingId != undoId)
            || undoValues.Select(v => v.ReversesValuationFactId).Distinct().Count() != 2
            || undoValues.Sum(v => v.SignedValue) != 0m)
            return false;

        var priorHistory = await db.IvTrxHistories.AsNoTracking().SingleOrDefaultAsync(h => h.StockPostingId == priorRoot.Id, ct);
        var undoHistory = await db.IvTrxHistories.AsNoTracking().SingleOrDefaultAsync(h => h.StockPostingId == undoId, ct);
        if (priorHistory is null || undoHistory is null)
            return false;
        var originalId = priorWasPrimary ? priorHistory.Id : priorHistory.ReversesHistoryId;
        if (originalId is not int originalReceiptId)
            return false;
        var originalHistory = await db.IvTrxHistories.AsNoTracking().SingleOrDefaultAsync(h => h.Id == originalReceiptId, ct);
        var oldFacts = await db.StockValuationFacts.AsNoTracking().Where(f => f.InventoryHistoryId == originalReceiptId).ToArrayAsync(ct);
        var rootFacts = await db.StockValuationFacts.AsNoTracking().Where(f => f.StockPostingId == undoId).ToArrayAsync(ct);
        var receiptFacts = rootFacts.Where(f => f.InventoryHistoryId == undoHistory.Id).ToArray();
        if (originalHistory is null || originalHistory.StockPostingId != (priorWasPrimary ? priorRoot.Id : priorRoot.ReversesPostingId)
            || originalHistory.CompanyCode != company || originalHistory.BranchCode != branch
            || undoHistory.CompanyCode != company || undoHistory.BranchCode != branch
            || undoHistory.TrxType != IvTrxTypes.MiscellaneousReceipt
            || undoHistory.EntryRole != (priorWasPrimary ? "REVERSAL" : "ORIGINAL")
            || (priorWasPrimary ? undoHistory.ReversesHistoryId != originalHistory.Id : undoHistory.ReversesHistoryId is not null)
            || undoHistory.BatchNo != originalHistory.BatchNo || undoHistory.TrxLineNo != originalHistory.TrxLineNo
            || undoHistory.TrxDtTime != originalHistory.TrxDtTime || undoHistory.ICode != originalHistory.ICode
            || (priorWasPrimary
                ? undoHistory.FromBalLocId != originalHistory.ToBalLocId || undoHistory.FrStdQty != originalHistory.ToStdQty
                    || undoHistory.FrStdUom != originalHistory.ToStdUom || undoHistory.FromLotId != originalHistory.ToLotId
                    || undoHistory.FrLotNo != originalHistory.ToLotNo
                : undoHistory.ToBalLocId != originalHistory.ToBalLocId || undoHistory.ToStdQty != originalHistory.ToStdQty
                    || undoHistory.ToStdUom != originalHistory.ToStdUom || undoHistory.ToLotId != originalHistory.ToLotId
                    || undoHistory.ToLotNo != originalHistory.ToLotNo)
            || undoHistory.IStatus != originalHistory.IStatus
            || oldFacts.Length != 1 || receiptFacts.Length != 1
            || receiptFacts[0].SignedQty != (priorWasPrimary ? -oldFacts[0].SignedQty : oldFacts[0].SignedQty)
            || receiptFacts[0].SignedValue != (priorWasPrimary ? -oldFacts[0].SignedValue : oldFacts[0].SignedValue)
            || receiptFacts[0].UnitCost != oldFacts[0].UnitCost
            || receiptFacts[0].ItemCode != oldFacts[0].ItemCode || receiptFacts[0].BaseUom != oldFacts[0].BaseUom
            || (priorWasPrimary ? receiptFacts[0].FromBalLocId : receiptFacts[0].ToBalLocId) != oldFacts[0].ToBalLocId
            || receiptFacts[0].CostMethod != oldFacts[0].CostMethod
            || receiptFacts[0].BaseCurrency != oldFacts[0].BaseCurrency)
            return false;
        var saleSource = sources.Single(s => s.MovementCode == "SALE_OUT");
        var reverseSource = sources.Single(s => s.MovementCode == "SALE_OUT_REVERSAL");
        if (!await ValidateReversedSaleRootIssueAsync(db, company, branch, undoRoot, undoHistory,
                saleSource, reverseSource, runs[0], ct))
            return false;

        var undoIds = undoValues.Select(v => v.Id).ToArray();
        var effects = await db.StockReplayEffects.AsNoTracking().Where(e =>
            e.AffectedEntityType == StockReplayEffectTypes.StockValuationFact && undoIds.Contains(e.AffectedEntityId)).ToArrayAsync(ct);
        if (effects.Length != 2)
            return false;
        foreach (var value in undoValues)
        {
            var parent = priorValues.SingleOrDefault(p => p.Id == value.ReversesValuationFactId);
            var source = sources.SingleOrDefault(s => s.Id == parent?.OriginalValuationFactId);
            if (parent is null || source is null || value.CompanyCode != company || value.BranchCode != branch
                || value.LedgerEpochId != parent.LedgerEpochId || value.InventoryHistoryId is not null || value.BaseQty != 0m
                || value.MovementCode != StockValuationMovementClassifier.ReversedSaleReplayValueUndo
                || value.Direction != -parent.Direction || value.CostAmount != parent.CostAmount
                || value.BaseCostAmount != parent.BaseCostAmount || value.UnitCost != parent.UnitCost
                || value.CostMethod != parent.CostMethod || value.BaseCurrency != parent.BaseCurrency
                || value.TransactionCurrency is not null || value.TransactionCostAmount is not null || value.ExchangeRate is not null
                || value.ValuationStatus != StockValuationStatuses.Reversed || value.ValuationSource != StockValuationSources.OriginalReversal
                || value.ValuationVersion != parent.ValuationVersion || value.OriginalValuationFactId != source.Id
                || value.EffectiveAt != parent.EffectiveAt || value.BusinessDate != parent.BusinessDate || value.PeriodKey != parent.PeriodKey
                || value.ItemCode != parent.ItemCode || value.BaseUom != parent.BaseUom || value.SourceLineId != parent.SourceLineId
                || value.SourceDocumentType != parent.SourceDocumentType || value.SourceDocumentId != parent.SourceDocumentId
                || value.SourceDocumentNo != parent.SourceDocumentNo || value.SourceDocumentLine != parent.SourceDocumentLine
                || value.WarehouseCode != parent.WarehouseCode || value.LocationCode != parent.LocationCode
                || value.LotId != parent.LotId || value.LotNo != parent.LotNo || value.ItemStatus != parent.ItemStatus
                || value.FromBalLocId != parent.FromBalLocId || value.ToBalLocId != parent.ToBalLocId
                || effects.Count(e => e.CompanyCode == company && e.BranchCode == branch && e.ReplayRunId == runs[0].Id
                    && e.RootStockPostingId == undoId && e.BusinessOwnerPostingId == source.StockPostingId
                    && e.EffectKind == StockReplayEffectTypes.SalesReversalValueUndo
                    && e.AffectedEntityId == value.Id && e.OriginalEntityId == source.Id) != 1)
                return false;
        }
        return true;
    }
}
