using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

internal sealed record ProductionStockReturnTerminalWacStatePlan(
    int ReturnBatchId,
    int IssueHistoryId,
    long OriginalIssueFactId,
    long OriginalReturnFactId,
    long ReturnPostingSequence,
    StockReplayWacWritePermit Permit);

/// <summary>
/// Prepares one WAC state update for an isolated ISSUE and its terminal return.
/// Both Inventory valuation corrections must belong to the same replay root.
/// An item with a later Inventory event needs the general financial-tail replay.
/// </summary>
internal static class ProductionStockReturnTerminalWacStatePlanner
{
    public static async Task<ProductionStockReturnTerminalWacStatePlan> PrepareWithinRootAsync(
        StockPostingContext context, int returnBatchId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.EnsureUnsealed();
        if (context.CostMethod != StockCostMethods.MovingAverage)
            throw Invalid("Terminal return cost state requires WAC valuation.");
        var db = context.Db;
        var projection = await ProductionStockReturnSourceCostAuthority.ReadWithinRootAsync(
            context, returnBatchId, cancellationToken);
        var issue = (await ProductionIssueEffectiveCostReader.ReadWithinRootAsync(
            context, [projection.SourceIssueMovementId], cancellationToken))
            [projection.SourceIssueMovementId];
        var originalIssue = await db.StockValuationFacts.AsNoTracking()
            .SingleAsync(x => x.InventoryHistoryId == issue.InventoryHistoryId
                && x.OriginalValuationFactId == null
                && x.ReversesValuationFactId == null, cancellationToken);
        var originalReturn = await db.StockValuationFacts.AsNoTracking()
            .SingleAsync(x => x.InventoryHistoryId == projection.ReturnHistoryId
                && x.OriginalValuationFactId == null
                && x.ReversesValuationFactId == null, cancellationToken);
        var issueMovement = await db.ProductionBalLotMovements.AsNoTracking()
            .SingleAsync(x => x.Uid == projection.SourceIssueMovementId,
                cancellationToken);
        var returnFact = await db.ProductionStockReturnFacts.AsNoTracking()
            .SingleAsync(x => x.Id == projection.ReturnFactId,
                cancellationToken);
        var history = await db.IvTrxHistories.AsNoTracking()
            .SingleAsync(x => x.Id == projection.ReturnHistoryId, cancellationToken);
        var owner = await db.StockPostings.AsNoTracking()
            .SingleAsync(x => x.Id == originalReturn.StockPostingId,
                cancellationToken);
        var state = await db.StockCostStates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CompanyCode == context.CompanyCode
                && x.BranchCode == context.BranchCode
                && x.ItemCode == originalReturn.ItemCode, cancellationToken);
        var piles = await db.IvBalLocs.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode
                && x.BranchCode == context.BranchCode
                && x.ICode == originalReturn.ItemCode)
            .Select(x => new { x.Id, x.StdQty }).ToArrayAsync(cancellationToken);
        var returnPile = piles.SingleOrDefault(x => x.Id == history.ToBalLocId);
        var finalValue = StockLedgerPrecision.Money(
            (state?.InventoryValue ?? 0m) + originalIssue.CostAmount
            - projection.CorrectedSourceValue
            + projection.CorrectedReturnValue - originalReturn.CostAmount);
        if (state is null || state.CostMethod != StockCostMethods.MovingAverage
            || state.LastValuationFactId != originalReturn.Id
            || state.LastPostingSequence != owner.PostingSequence
            || state.OnHandBaseQty <= 0m || state.InventoryValue < 0m
            || state.AverageUnitCost != StockLedgerPrecision.Money(
                state.InventoryValue / state.OnHandBaseQty)
            || state.CurrentUnitCost != state.AverageUnitCost
            || originalIssue.CompanyCode != context.CompanyCode
            || originalIssue.BranchCode != context.BranchCode
            || originalIssue.LedgerEpochId != context.Epoch.Id
            || originalIssue.ItemCode != originalReturn.ItemCode
            || originalIssue.Direction != -1
            || originalIssue.CostMethod != StockCostMethods.MovingAverage
            || originalIssue.BaseQty != issue.BaseQty
            || originalIssue.CostAmount != issueMovement.TotalCost
            || issue.ActiveValuationFactIds.Count != 1
            || originalReturn.CompanyCode != context.CompanyCode
            || originalReturn.BranchCode != context.BranchCode
            || originalReturn.LedgerEpochId != context.Epoch.Id
            || originalReturn.Direction != 1
            || originalReturn.CostMethod != StockCostMethods.MovingAverage
            || originalReturn.ValuationStatus != StockValuationStatuses.Valued
            || originalReturn.CostAmount < 0m
            || originalReturn.CostAmount != returnFact.TotalValue
            || originalReturn.BaseQty != returnFact.BaseQty
            || originalReturn.InventoryHistoryId != returnFact.InventoryHistoryId
            || owner.LedgerEpochId != context.Epoch.Id
            || owner.SealedAtUtc is null
            || history.StockPostingId != owner.Id
            || returnPile is null || returnPile.StdQty < 0m
            || piles.Any(x => x.StdQty < 0m)
            || StockLedgerPrecision.Quantity(piles.Sum(x => x.StdQty))
                != state.OnHandBaseQty
            || finalValue < 0m)
            throw Invalid("Return is not the terminal WAC receipt of this item.");

        var preparedFacts = await ValidateRootFactsAsync(context, issue.InventoryHistoryId,
            originalIssue, originalReturn, projection.CorrectedSourceValue,
            projection.CorrectedReturnValue, false, cancellationToken);
        if (!preparedFacts.Any(x => x.Id == issue.ActiveValuationFactIds[0]
            && x.OriginalValuationFactId == originalIssue.Id))
            throw Invalid("Corrected ISSUE Inventory valuation is not active in this root.");
        var key = new StockReplayEventKey(owner.EffectiveAt,
            StockReplayEventKey.OrdinaryClass, owner.PostingSequence,
            $"H{history.Id:D10}", 0);
        var tail = await StockReplayWacFinancialTailReader.ReadLaterAsync(db,
            context.CompanyCode, context.BranchCode, originalReturn.ItemCode,
            key, cancellationToken);
        if (tail.LaterEvents.Count != 0
            || tail.CurrentQuantity != state.OnHandBaseQty
            || tail.CurrentValue != state.InventoryValue
            || tail.CurrentQuantityByPile.Count != piles.Length
            || piles.Any(x => !tail.CurrentQuantityByPile.TryGetValue(x.Id,
                out var quantity) || quantity != x.StdQty))
            throw Invalid("Return Inventory has a later WAC effect requiring tail replay.");

        var hash = StockPostingFingerprint.Create(
            new { RootPostingId = context.Posting.Id, returnBatchId,
                IssueFactId = originalIssue.Id, ReturnFactId = originalReturn.Id },
            new { StateId = state.Id, state.OnHandBaseQty,
                state.InventoryValue, state.LastValuationFactId,
                state.LastPostingSequence, CorrectedSource = projection.CorrectedSourceValue,
                CorrectedReturn = projection.CorrectedReturnValue,
                ReturnPileId = returnPile.Id, returnPile.StdQty }).SourceSnapshotHash;
        var permit = new StockReplayWacWritePermit(context, originalReturn.ItemCode,
            hash, state.OnHandBaseQty, state.InventoryValue,
            state.OnHandBaseQty, finalValue,
            returnPile.Id, returnPile.StdQty);
        return new(returnBatchId, issue.InventoryHistoryId, originalIssue.Id,
            originalReturn.Id, owner.PostingSequence, permit);
    }

    public static async Task ApplyWithinRootAsync(
        StockPostingContext context, ProductionStockReturnTerminalWacStatePlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        context.EnsureUnsealed();
        var db = context.Db;
        var projection = await ProductionStockReturnSourceCostAuthority.ReadWithinRootAsync(
            context, plan.ReturnBatchId, cancellationToken);
        var originalIssue = await db.StockValuationFacts.AsNoTracking()
            .SingleAsync(x => x.Id == plan.OriginalIssueFactId,
                cancellationToken);
        var originalReturn = await db.StockValuationFacts.AsNoTracking()
            .SingleAsync(x => x.Id == plan.OriginalReturnFactId,
                cancellationToken);
        var effectiveReturn = (await ProductionEffectiveCostResolver.ResolveAsync(db,
            context.CompanyCode, context.BranchCode,
            [projection.ReturnMovementId], cancellationToken))
            [projection.ReturnMovementId];
        var returnMovement = await db.ProductionBalLotMovements.AsNoTracking()
            .SingleAsync(x => x.Uid == projection.ReturnMovementId,
                cancellationToken);
        var pool = await db.ProductionBalLots.AsNoTracking()
            .SingleAsync(x => x.Uid == returnMovement.ProductionBalLotId,
                cancellationToken);
        var poolValuation = await db.ProductionPoolValuationRows.AsNoTracking()
            .SingleAsync(x => x.ProductionBalLotId == pool.Uid,
                cancellationToken);
        var returnDelta = StockLedgerPrecision.Money(
            projection.CorrectedReturnValue - originalReturn.CostAmount);
        if (plan.Permit.CompanyCode != context.CompanyCode
            || plan.Permit.BranchCode != context.BranchCode
            || plan.Permit.LedgerEpochId != context.Epoch.Id
            || plan.Permit.RootPostingId != context.Posting.Id
            || plan.Permit.ItemCode != originalReturn.ItemCode
            || plan.IssueHistoryId != originalIssue.InventoryHistoryId
            || projection.ReturnHistoryId != originalReturn.InventoryHistoryId
            || plan.Permit.CurrentQuantity != plan.Permit.FinalQuantity
            || plan.Permit.FinalValue != StockLedgerPrecision.Money(
                plan.Permit.CurrentValue + originalIssue.CostAmount
                - projection.CorrectedSourceValue + returnDelta)
            || effectiveReturn.TotalCost != projection.CorrectedReturnValue
            || pool.TotalCost != projection.ProjectedRemainingValue
            || poolValuation.TrackedValue != pool.TotalCost
            || poolValuation.TrackedBaseQty != pool.BaseQty
            || returnDelta != 0m && effectiveReturn.RestatementFactId is null
            || returnDelta == 0m && effectiveReturn.RestatementFactId is not null)
            throw Invalid("Return Production and Inventory corrections differ from the WAC state plan.");
        var rootFacts = await ValidateRootFactsAsync(context, plan.IssueHistoryId,
            originalIssue, originalReturn, projection.CorrectedSourceValue,
            projection.CorrectedReturnValue, true, cancellationToken);
        var state = await db.StockCostStates.AsNoTracking()
            .SingleAsync(x => x.CompanyCode == context.CompanyCode
                && x.BranchCode == context.BranchCode
                && x.ItemCode == originalReturn.ItemCode, cancellationToken);
        if (state.OnHandBaseQty == plan.Permit.FinalQuantity
            && state.InventoryValue == plan.Permit.FinalValue
            && state.LastValuationFactId == rootFacts.OrderByDescending(x =>
                x.PostingLineNo).ThenByDescending(x => x.SplitOrdinal).First().Id
            && state.LastPostingSequence == context.Posting.PostingSequence
            && state.AverageUnitCost == StockLedgerPrecision.Money(
                state.InventoryValue / state.OnHandBaseQty)
            && state.CurrentUnitCost == state.AverageUnitCost)
            return;
        if (state.LastValuationFactId != originalReturn.Id
            || state.LastPostingSequence != plan.ReturnPostingSequence)
            throw Invalid("Return WAC state changed after root preparation.");
        await StockReplayWacCostStateWriter.ApplyAsync(context,
            plan.Permit, cancellationToken);
    }

    private static async Task<StockValuationFact[]> ValidateRootFactsAsync(
        StockPostingContext context, int issueHistoryId,
        StockValuationFact originalIssue, StockValuationFact originalReturn,
        decimal correctedIssueValue, decimal correctedReturnValue,
        bool includeReturnDelta, CancellationToken cancellationToken)
    {
        var facts = await context.Db.StockValuationFacts.AsNoTracking()
            .Where(x => x.StockPostingId == context.Posting.Id
                && x.ItemCode == originalReturn.ItemCode)
            .ToArrayAsync(cancellationToken);
        var issueFacts = facts.Where(x => x.InventoryHistoryId == issueHistoryId)
            .ToArray();
        var reversals = issueFacts.Where(x =>
            x.ReversesValuationFactId == originalIssue.Id).ToArray();
        var replacements = issueFacts.Where(x =>
            x.OriginalValuationFactId == originalIssue.Id).ToArray();
        var reversal = reversals.Length == 1 ? reversals[0] : null;
        var replacement = replacements.Length == 1 ? replacements[0] : null;
        var returnDelta = StockLedgerPrecision.Money(
            correctedReturnValue - originalReturn.CostAmount);
        var returnFacts = facts.Where(x =>
            x.OriginalValuationFactId == originalReturn.Id).ToArray();
        var returnFact = returnFacts.Length == 1 ? returnFacts[0] : null;
        if (issueFacts.Length != 2 || reversal is null || replacement is null
            || reversal.Id == replacement.Id
            || reversal.Direction != 1
            || reversal.OriginalValuationFactId is not null
            || reversal.InventoryHistoryId != issueHistoryId
            || reversal.BaseQty != originalIssue.BaseQty
            || reversal.CostAmount != originalIssue.CostAmount
            || reversal.ValuationStatus != StockValuationStatuses.Reversed
            || reversal.ValuationVersion != originalIssue.ValuationVersion
            || reversal.UnitCost != originalIssue.UnitCost
            || replacement.Direction != -1
            || replacement.InventoryHistoryId != issueHistoryId
            || replacement.ReversesValuationFactId is not null
            || replacement.BaseQty != originalIssue.BaseQty
            || replacement.CostAmount != correctedIssueValue
            || replacement.ValuationStatus != StockValuationStatuses.Valued
            || replacement.ValuationVersion != originalIssue.ValuationVersion + 1
            || replacement.UnitCost != StockLedgerPrecision.Money(
                correctedIssueValue / originalIssue.BaseQty)
            || reversal.CostMethod != StockCostMethods.MovingAverage
            || replacement.CostMethod != StockCostMethods.MovingAverage
            || reversal.LedgerEpochId != context.Epoch.Id
            || replacement.LedgerEpochId != context.Epoch.Id
            || reversal.CompanyCode != context.CompanyCode
            || replacement.CompanyCode != context.CompanyCode
            || reversal.BranchCode != context.BranchCode
            || replacement.BranchCode != context.BranchCode
            || reversal.ItemCode != originalIssue.ItemCode
            || replacement.ItemCode != originalIssue.ItemCode
            || reversal.BaseCostAmount != reversal.CostAmount
            || replacement.BaseCostAmount != replacement.CostAmount
            || reversal.BusinessDate != originalIssue.BusinessDate
            || replacement.BusinessDate != originalIssue.BusinessDate
            || reversal.PeriodKey != originalIssue.PeriodKey
            || replacement.PeriodKey != originalIssue.PeriodKey
            || reversal.EffectiveAt != originalIssue.EffectiveAt
            || replacement.EffectiveAt != originalIssue.EffectiveAt
            || !MatchesOriginalEvent(reversal, originalIssue)
            || !MatchesOriginalEvent(replacement, originalIssue)
            || returnFacts.Length > (includeReturnDelta && returnDelta != 0m ? 1 : 0)
            || includeReturnDelta && returnDelta != 0m
                && (returnFact is null || returnFact.InventoryHistoryId is not null
                    || returnFact.ReversesValuationFactId is not null
                    || returnFact.Direction != Math.Sign(returnDelta)
                    || returnFact.BaseQty != 0m
                    || returnFact.CostAmount != Math.Abs(returnDelta)
                    || returnFact.BaseCostAmount != returnFact.CostAmount
                    || returnFact.UnitCost != StockLedgerPrecision.Money(
                        Math.Abs(returnDelta) / originalReturn.BaseQty)
                    || returnFact.ValuationVersion != originalReturn.ValuationVersion + 1
                    || returnFact.ValuationStatus != StockValuationStatuses.Valued
                    || returnFact.CostMethod != StockCostMethods.MovingAverage
                    || returnFact.LedgerEpochId != context.Epoch.Id
                    || returnFact.CompanyCode != context.CompanyCode
                    || returnFact.BranchCode != context.BranchCode
                    || returnFact.ItemCode != originalReturn.ItemCode
                    || returnFact.BusinessDate != originalReturn.BusinessDate
                    || returnFact.PeriodKey != originalReturn.PeriodKey
                    || returnFact.EffectiveAt != originalReturn.EffectiveAt
                    || !MatchesOriginalEvent(returnFact, originalReturn))
            || (!includeReturnDelta || returnDelta == 0m) && returnFact is not null
            || facts.Length != 2 + (includeReturnDelta && returnDelta != 0m ? 1 : 0))
            throw Invalid("Replay root has incomplete ISSUE or return WAC valuation facts.");
        return facts;
    }

    private static bool MatchesOriginalEvent(
        StockValuationFact correction, StockValuationFact original) =>
        correction.SourceLineId == original.SourceLineId
        && correction.SourceDocumentType == original.SourceDocumentType
        && correction.SourceDocumentId == original.SourceDocumentId
        && correction.SourceDocumentNo == original.SourceDocumentNo
        && correction.SourceDocumentLine == original.SourceDocumentLine
        && correction.MovementCode == original.MovementCode
        && correction.WarehouseCode == original.WarehouseCode
        && correction.LocationCode == original.LocationCode
        && correction.LotId == original.LotId
        && correction.LotNo == original.LotNo
        && correction.ItemStatus == original.ItemStatus
        && correction.BaseUom == original.BaseUom
        && correction.BaseCurrency == original.BaseCurrency
        && correction.FromBalLocId == original.FromBalLocId
        && correction.ToBalLocId == original.ToBalLocId;

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
