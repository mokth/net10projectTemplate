using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

internal sealed record ProductionFinishedGoodTerminalWacStatePlan(
    int ReceiptBatchId,
    long OriginalReceiptFactId,
    StockReplayWacWritePermit Permit);

/// <summary>
/// Prepares the shared WAC state writer for a terminal FG receipt on an item
/// that had no earlier on-hand stock. It must run before the receipt value delta
/// is appended, inside the same unsealed replay root. A later Inventory event
/// requires the general financial-tail replay instead.
/// </summary>
internal static class ProductionFinishedGoodTerminalWacStatePlanner
{
    public static async Task<ProductionFinishedGoodTerminalWacStatePlan> PrepareWithinRootAsync(
        StockPostingContext context, int receiptBatchId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.EnsureUnsealed();
        if (context.CostMethod != StockCostMethods.MovingAverage)
            throw Invalid("Terminal FG cost state requires WAC valuation.");
        var db = context.Db;
        var projection = await ProductionFinishedGoodSourceCostAuthority.ReadWithinRootAsync(
            context, receiptBatchId, cancellationToken);
        var receipt = await db.StockValuationFacts.AsNoTracking()
            .SingleAsync(x => x.Id == projection.OriginalInventoryValuationFactId,
                cancellationToken);
        var history = await db.IvTrxHistories.AsNoTracking()
            .SingleAsync(x => x.Id == projection.InventoryHistoryId,
                cancellationToken);
        var owner = await db.StockPostings.AsNoTracking()
            .SingleAsync(x => x.Id == receipt.StockPostingId,
                cancellationToken);
        var state = await db.StockCostStates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CompanyCode == context.CompanyCode
                && x.BranchCode == context.BranchCode
                && x.ItemCode == receipt.ItemCode, cancellationToken);
        var piles = await db.IvBalLocs.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode
                && x.BranchCode == context.BranchCode && x.ICode == receipt.ItemCode)
            .Select(x => new { x.Id, x.StdQty }).ToArrayAsync(cancellationToken);
        var correction = StockLedgerPrecision.Money(
            projection.CorrectedFinishedGoodValue - receipt.CostAmount);
        if (state is null || state.CostMethod != StockCostMethods.MovingAverage
            || state.LastValuationFactId != receipt.Id
            || state.LastPostingSequence != owner.PostingSequence
            || state.OnHandBaseQty != receipt.BaseQty
            || state.InventoryValue != receipt.CostAmount
            || state.AverageUnitCost != StockLedgerPrecision.Money(
                state.InventoryValue / state.OnHandBaseQty)
            || state.CurrentUnitCost != state.AverageUnitCost
            || piles.Length != 1 || piles[0].Id != history.ToBalLocId
            || piles[0].StdQty != state.OnHandBaseQty
            || StockLedgerPrecision.Money(state.InventoryValue + correction) < 0m
            || await db.StockValuationFacts.AsNoTracking().AnyAsync(x =>
                x.StockPostingId == context.Posting.Id
                && x.ItemCode == receipt.ItemCode, cancellationToken))
            throw Invalid("FG receipt is not the terminal WAC event of an empty item.");

        var key = new StockReplayEventKey(owner.EffectiveAt,
            StockReplayEventKey.OrdinaryClass, owner.PostingSequence,
            $"H{history.Id:D10}", 0);
        var tail = await StockReplayWacFinancialTailReader.ReadLaterAsync(db,
            context.CompanyCode, context.BranchCode, receipt.ItemCode,
            key, cancellationToken);
        if (tail.LaterEvents.Count != 0
            || tail.CurrentQuantity != state.OnHandBaseQty
            || tail.CurrentValue != state.InventoryValue
            || tail.CurrentQuantityByPile.Count != 1
            || !tail.CurrentQuantityByPile.TryGetValue(piles[0].Id,
                out var pileQty) || pileQty != piles[0].StdQty)
            throw Invalid("FG Inventory has a later WAC effect requiring tail replay.");

        var hash = StockPostingFingerprint.Create(
            new { RootPostingId = context.Posting.Id, receiptBatchId,
                ReceiptId = receipt.Id },
            new { StateId = state.Id, state.OnHandBaseQty, state.InventoryValue,
                state.LastValuationFactId, state.LastPostingSequence,
                ReceiptId = receipt.Id, projection.CorrectedFinishedGoodValue,
                PileId = piles[0].Id, piles[0].StdQty }).SourceSnapshotHash;
        var permit = new StockReplayWacWritePermit(context, receipt.ItemCode,
            hash, state.OnHandBaseQty, state.InventoryValue,
            state.OnHandBaseQty,
            StockLedgerPrecision.Money(state.InventoryValue + correction),
            piles[0].Id, piles[0].StdQty);
        return new(receiptBatchId, receipt.Id, permit);
    }

    public static async Task ApplyWithinRootAsync(
        StockPostingContext context, ProductionFinishedGoodTerminalWacStatePlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        context.EnsureUnsealed();
        var db = context.Db;
        var projection = await ProductionFinishedGoodSourceCostAuthority.ReadWithinRootAsync(
            context, plan.ReceiptBatchId, cancellationToken);
        var receipt = await db.StockValuationFacts.AsNoTracking()
            .SingleAsync(x => x.Id == plan.OriginalReceiptFactId,
                cancellationToken);
        var effective = (await ProductionEffectiveCostResolver.ResolveAsync(db,
            context.CompanyCode, context.BranchCode,
            [projection.FinishedGoodMovementId], cancellationToken))
            [projection.FinishedGoodMovementId];
        var rootFacts = await db.StockValuationFacts.AsNoTracking()
            .Where(x => x.StockPostingId == context.Posting.Id
                && x.ItemCode == receipt.ItemCode).ToArrayAsync(cancellationToken);
        var correction = StockLedgerPrecision.Money(
            projection.CorrectedFinishedGoodValue - receipt.CostAmount);
        if (projection.OriginalInventoryValuationFactId != plan.OriginalReceiptFactId
            || plan.Permit.CompanyCode != context.CompanyCode
            || plan.Permit.BranchCode != context.BranchCode
            || plan.Permit.LedgerEpochId != context.Epoch.Id
            || plan.Permit.RootPostingId != context.Posting.Id
            || plan.Permit.ItemCode != receipt.ItemCode
            || plan.Permit.FinalValue != StockLedgerPrecision.Money(
                plan.Permit.CurrentValue + correction)
            || effective.TotalCost != projection.CorrectedFinishedGoodValue
            || correction != 0m && effective.RestatementFactId is null
            || correction == 0m && rootFacts.Length != 0
            || correction != 0m && (rootFacts.Length != 1
                || rootFacts[0].OriginalValuationFactId != receipt.Id
                || rootFacts[0].InventoryHistoryId is not null
                || rootFacts[0].Direction != Math.Sign(correction)
                || rootFacts[0].CostAmount != Math.Abs(correction)
                || rootFacts[0].BaseQty != 0m
                || rootFacts[0].ItemCode != receipt.ItemCode
                || rootFacts[0].EffectiveAt != receipt.EffectiveAt
                || rootFacts[0].BusinessDate != receipt.BusinessDate
                || rootFacts[0].PeriodKey != receipt.PeriodKey
                || rootFacts[0].SourceLineId != receipt.SourceLineId
                || rootFacts[0].SourceDocumentType != receipt.SourceDocumentType
                || rootFacts[0].SourceDocumentId != receipt.SourceDocumentId
                || rootFacts[0].SourceDocumentNo != receipt.SourceDocumentNo
                || rootFacts[0].SourceDocumentLine != receipt.SourceDocumentLine))
            throw Invalid("FG Production and Inventory cost corrections do not match the WAC state plan.");
        if (rootFacts.Length == 1)
        {
            var state = await db.StockCostStates.AsNoTracking()
                .SingleAsync(x => x.CompanyCode == context.CompanyCode
                    && x.BranchCode == context.BranchCode
                    && x.ItemCode == receipt.ItemCode, cancellationToken);
            if (state.OnHandBaseQty == plan.Permit.FinalQuantity
                && state.InventoryValue == plan.Permit.FinalValue
                && state.LastValuationFactId == rootFacts[0].Id
                && state.LastPostingSequence == context.Posting.PostingSequence
                && state.AverageUnitCost == StockLedgerPrecision.Money(
                    state.InventoryValue / state.OnHandBaseQty)
                && state.CurrentUnitCost == state.AverageUnitCost)
                return;
        }
        await StockReplayWacCostStateWriter.ApplyAsync(context,
            plan.Permit, cancellationToken);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
