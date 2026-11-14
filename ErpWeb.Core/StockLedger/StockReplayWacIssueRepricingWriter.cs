using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

internal sealed record StockReplayWacIssueRepricingRequest(
    int HistoryId, StockReplayWacEvent ActiveEvent, StockReplayWacStep ProjectedStep,
    int ReversalPostingLineNo, int ReplacementPostingLineNo);

internal sealed record StockReplayWacIssueRepricingResult(
    int HistoryId, IReadOnlyList<StockValuationFact> Reversals, IReadOnlyList<StockValuationFact> Replacements);

/// <summary>
/// Appends exact old-generation reversals and complete replacements for
/// quantity-unchanged WAC issues. The caller owns the locked replay plan,
/// final cost state, and dependent Procurement/Production/Sales costs.
/// </summary>
public static class StockReplayWacIssueRepricingWriter
{
    public static async Task<IReadOnlyList<StockValuationFact>> AppendAsync(
        StockPostingContext context,
        int activeHistoryId,
        StockReplayWacEvent activeEvent,
        StockReplayWacStep projectedStep,
        int reversalPostingLineNo,
        int replacementPostingLineNo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activeEvent);
        ArgumentNullException.ThrowIfNull(projectedStep);
        var results = await AppendManyAsync(context,
            [new(activeHistoryId, activeEvent, projectedStep, reversalPostingLineNo, replacementPostingLineNo)],
            cancellationToken);
        return results.Single().Replacements;
    }

    internal static async Task<IReadOnlyList<StockReplayWacIssueRepricingResult>> AppendManyAsync(
        StockPostingContext context,
        IReadOnlyCollection<StockReplayWacIssueRepricingRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requests);
        context.EnsureUnsealed();
        if (context.CostMethod != StockCostMethods.MovingAverage || requests.Count == 0
            || requests.Select(r => r.HistoryId).Distinct().Count() != requests.Count
            || requests.Any(r => r.HistoryId <= 0 || r.ActiveEvent is null || r.ProjectedStep is null
                || r.ReversalPostingLineNo <= 0 || r.ReplacementPostingLineNo <= 0
                || r.ReversalPostingLineNo == r.ReplacementPostingLineNo
                || r.ActiveEvent.EventId != r.HistoryId || r.ProjectedStep.EventId != r.HistoryId))
            throw Invalid("WAC issue replacement has invalid root or event identity.");

        var db = context.Db;
        var historyIds = requests.Select(r => r.HistoryId).ToArray();
        var histories = await db.IvTrxHistories.AsNoTracking().Where(h => historyIds.Contains(h.Id)
            && h.CompanyCode == context.CompanyCode && h.BranchCode == context.BranchCode)
            .ToArrayAsync(cancellationToken);
        if (histories.Length != historyIds.Length || histories.Any(h => h.LedgerVersion != 2
                || h.LedgerEpochId != context.Epoch.Id || h.EntryRole != "ORIGINAL" || h.BatchStatus != "POSTED"
                || h.ValuationStatus != StockValuationStatuses.Valued || h.FromBalLocId is not > 0
                || h.FrStdQty is not > 0m || h.ToStdQty is > 0m))
            throw Invalid("A WAC replay history is not an active V2 issue.");
        var byHistory = histories.ToDictionary(h => h.Id);
        var active = await StockReplayValuationGenerationResolver.ResolveAsync(
            db, context.CompanyCode, context.BranchCode, histories, cancellationToken);
        var prepared = new List<(StockReplayWacIssueRepricingRequest Request, StockValuationFact[] Originals, decimal[] Amounts)>();
        foreach (var request in requests)
        {
            var history = byHistory[request.HistoryId];
            var originals = active[request.HistoryId].OrderBy(f => f.PostingLineNo).ThenBy(f => f.SplitOrdinal).ToArray();
            var movement = request.ActiveEvent;
            var step = request.ProjectedStep;
            if (movement.Key.LineIdentity.Length != 11 || movement.Key.LineIdentity[0] != 'H'
                || !int.TryParse(movement.Key.LineIdentity.AsSpan(1), out var ownerHistoryId) || ownerHistoryId <= 0
                || StockReplayWacHistoryEventFactory.Create(history, ownerHistoryId, movement.Key, originals) != movement)
                throw Invalid($"History {history.Id} WAC event differs from its active valuation.");
            if (originals.Any(f => f.CostMethod != StockCostMethods.MovingAverage || f.Direction != -1
                    || f.ValuationSource != StockValuationSources.MovingAverage || f.TransactionCurrency is not null
                    || f.TransactionCostAmount is not null || f.ExchangeRate is not null || f.BaseCostAmount != f.CostAmount)
                || originals.Length > 1000)
                throw Invalid($"History {history.Id} has unsupported WAC issue splits.");

            var oldQty = -StockLedgerPrecision.Quantity(originals.Sum(f => f.BaseQty));
            var oldValue = -StockLedgerPrecision.Money(originals.Sum(f => f.CostAmount));
            if (movement.OldSignedQuantity != oldQty || movement.OldSignedValue != oldValue
                || movement.ReplaySignedQuantity != oldQty || movement.AbsoluteCountQty.HasValue
                || movement.FixedSignedValue.HasValue || step.QuantityBefore < -oldQty || step.ValueBefore < 0m
                || IvQty.Round(step.QuantityBefore) != step.QuantityBefore
                || StockLedgerPrecision.Money(step.ValueBefore) != step.ValueBefore)
                throw Invalid($"History {history.Id} issue projection does not match its active facts.");
            var replayed = StockReplayWacSimulator.SimulateInsertion(step.QuantityBefore, step.ValueBefore,
                movement with { OldSignedQuantity = 0m, OldSignedValue = 0m }, []);
            if (step != replayed.Candidate)
                throw Invalid($"History {history.Id} has an invalid replayed WAC issue amount.");
            var expectedAmount = -replayed.Candidate.SignedValue;
            if (expectedAmount == -oldValue)
                continue;

            var amounts = new decimal[originals.Length];
            decimal allocated = 0m;
            for (var i = 0; i < originals.Length; i++)
            {
                amounts[i] = i == originals.Length - 1
                    ? StockLedgerPrecision.Money(expectedAmount - allocated)
                    : StockLedgerPrecision.Money(expectedAmount * originals[i].BaseQty / -oldQty);
                allocated = StockLedgerPrecision.Money(allocated + amounts[i]);
            }
            if (allocated != expectedAmount || amounts.Any(x => x < 0m))
                throw Invalid("The WAC issue split cannot represent the projected value.");
            prepared.Add((request, originals, amounts));
        }

        if (prepared.Count == 0)
            return requests.Select(r => new StockReplayWacIssueRepricingResult(r.HistoryId, [], [])).ToArray();
        var requiredLines = prepared.SelectMany(p => new[]
            { p.Request.ReversalPostingLineNo, p.Request.ReplacementPostingLineNo }).ToArray();
        if (requiredLines.Distinct().Count() != requiredLines.Length
            || await db.StockValuationFacts.AsNoTracking().AnyAsync(f => f.StockPostingId == context.Posting.Id
                && requiredLines.Contains(f.PostingLineNo), cancellationToken)
            || db.ChangeTracker.Entries<StockValuationFact>().Any(e => e.State == EntityState.Added
                && e.Entity.StockPostingId == context.Posting.Id && requiredLines.Contains(e.Entity.PostingLineNo)))
            throw Invalid("A WAC replay root valuation line is already used.");

        var reversalRequests = prepared.Select(p => new StockReplayValuationReversalRequest(p.Request.HistoryId,
            p.Originals.Select((f, i) => new StockReplayValuationReversalLine(
                f.Id, p.Request.ReversalPostingLineNo, i)).ToArray())).ToArray();
        var reversals = await StockReplayValuationReversalWriter.AppendManyAsync(context, reversalRequests, cancellationToken);
        var reversalsByHistory = reversals.ToLookup(f => f.InventoryHistoryId!.Value);
        var replacementsByHistory = new Dictionary<int, IReadOnlyList<StockValuationFact>>();
        var now = DateTime.UtcNow;
        foreach (var entry in prepared)
        {
            var replacements = entry.Originals.Select((original, i) => new StockValuationFact
            {
                CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
                LedgerEpochId = context.Epoch.Id, StockPostingId = context.Posting.Id,
                PostingLineNo = entry.Request.ReplacementPostingLineNo, SplitOrdinal = i,
                SourceLineId = original.SourceLineId, SourceDocumentType = original.SourceDocumentType,
                SourceDocumentId = original.SourceDocumentId, SourceDocumentNo = original.SourceDocumentNo,
                SourceDocumentLine = original.SourceDocumentLine, EffectiveAt = original.EffectiveAt,
                BusinessDate = original.BusinessDate, PeriodKey = original.PeriodKey,
                ItemCode = original.ItemCode, WarehouseCode = original.WarehouseCode,
                LocationCode = original.LocationCode, LotId = original.LotId, LotNo = original.LotNo,
                ItemStatus = original.ItemStatus, BaseUom = original.BaseUom, MovementCode = original.MovementCode,
                Direction = -1, BaseQty = original.BaseQty, CostMethod = StockCostMethods.MovingAverage,
                UnitCost = StockLedgerPrecision.Money(entry.Amounts[i] / original.BaseQty),
                CostAmount = entry.Amounts[i], BaseCurrency = original.BaseCurrency, BaseCostAmount = entry.Amounts[i],
                ValuationSource = StockValuationSources.MovingAverage, ValuationStatus = StockValuationStatuses.Valued,
                ValuationVersion = checked(original.ValuationVersion + 1), InventoryHistoryId = original.InventoryHistoryId,
                FromBalLocId = original.FromBalLocId, ToBalLocId = original.ToBalLocId,
                ProductionMovementId = original.ProductionMovementId, WorkOrderId = original.WorkOrderId,
                WorkOrderOperationId = original.WorkOrderOperationId, ProductionPostingLinkId = original.ProductionPostingLinkId,
                OriginalValuationFactId = original.OriginalValuationFactId ?? original.Id, CreatedAtUtc = now, CreatedBy = context.UserId
            }).ToArray();
            db.StockValuationFacts.AddRange(replacements);
            replacementsByHistory.Add(entry.Request.HistoryId, replacements);
        }
        return requests.Select(r => new StockReplayWacIssueRepricingResult(r.HistoryId,
            reversalsByHistory[r.HistoryId].ToArray(), replacementsByHistory.GetValueOrDefault(r.HistoryId) ?? []))
            .ToArray();
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
