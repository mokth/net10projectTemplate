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
        var watermark = await db.StockPostings.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                        && x.SealedAtUtc != null && x.EffectiveAt < exclusiveEnd)
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

}
