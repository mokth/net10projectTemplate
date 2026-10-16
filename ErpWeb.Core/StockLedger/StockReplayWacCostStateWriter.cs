using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Applies one validated item's final WAC state only after the root's physical
/// balance and signed valuation facts account for the entire projected delta.
/// Other module reconciliations still run before the root can be sealed.
/// </summary>
public static class StockReplayWacCostStateWriter
{
    public static async Task ApplyAsync(
        StockPostingContext context,
        StockReplayWacWritePermit permit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(permit);
        context.EnsureUnsealed();
        if (context.CompanyCode != permit.CompanyCode
            || context.BranchCode != permit.BranchCode
            || context.Epoch.Id != permit.LedgerEpochId
            || context.Posting.Id != permit.RootPostingId
            || context.Posting.PostingSequence != permit.RootPostingSequence
            || context.CostMethod != StockCostMethods.MovingAverage
            || string.IsNullOrWhiteSpace(permit.PlanHash))
            throw Invalid("The WAC state permit does not belong to this replay root.");

        var db = context.Db;
        // All preceding physical and fact mutations must be visible to the
        // verification queries. They are still inside the caller's transaction.
        await db.SaveChangesAsync(cancellationToken);
        var state = await db.StockCostStates.SingleOrDefaultAsync(s =>
            s.CompanyCode == context.CompanyCode
            && s.BranchCode == context.BranchCode
            && s.ItemCode == permit.ItemCode, cancellationToken);
        if (state is null || state.CostMethod != StockCostMethods.MovingAverage
            || state.OnHandBaseQty != permit.CurrentQuantity
            || state.InventoryValue != permit.CurrentValue)
            throw Invalid($"Item {permit.ItemCode} WAC state changed after root preparation.");
        var piles = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == context.CompanyCode
                && b.BranchCode == context.BranchCode
                && b.ICode == permit.ItemCode)
            .Select(b => new { b.Id, b.StdQty })
            .ToArrayAsync(cancellationToken);
        if (piles.Any(p => p.StdQty < 0m
                || IvQty.Round(p.StdQty) != p.StdQty)
            || permit.ChangedPiles.Count == 0
            || permit.ChangedPiles.Any(changed => !piles.Any(p =>
                p.Id == changed.Key && p.StdQty == changed.Value))
            || StockLedgerPrecision.Quantity(piles.Sum(p => p.StdQty))
                != permit.FinalQuantity)
            throw Invalid($"Item {permit.ItemCode} physical piles do not match WAC replay.");

        var rootFacts = await db.StockValuationFacts.AsNoTracking()
            .Where(f => f.StockPostingId == context.Posting.Id
                && f.ItemCode == permit.ItemCode)
            .ToArrayAsync(cancellationToken);
        if (rootFacts.Any(f => f.CompanyCode != context.CompanyCode
                || f.BranchCode != context.BranchCode
                || f.LedgerEpochId != context.Epoch.Id
                || f.CostMethod != StockCostMethods.MovingAverage
                || f.ValuationStatus is not (
                    StockValuationStatuses.Valued or StockValuationStatuses.Reversed)
                || f.Direction is not (-1 or 1)
                || f.BaseQty < 0m || f.CostAmount < 0m
                || IvQty.Round(f.BaseQty) != f.BaseQty
                || StockLedgerPrecision.Money(f.CostAmount) != f.CostAmount))
            throw Invalid($"Item {permit.ItemCode} has an invalid replay valuation fact.");
        var signedQty = StockLedgerPrecision.Quantity(
            rootFacts.Sum(f => f.SignedQty));
        var signedValue = StockLedgerPrecision.Money(
            rootFacts.Sum(f => f.SignedValue));
        if (signedQty != StockLedgerPrecision.Quantity(
                permit.FinalQuantity - permit.CurrentQuantity)
            || signedValue != StockLedgerPrecision.Money(
                permit.FinalValue - permit.CurrentValue))
            throw Invalid($"Item {permit.ItemCode} replay facts do not explain its WAC delta.");
        if (rootFacts.Length == 0)
            return;

        state.OnHandBaseQty = permit.FinalQuantity;
        state.InventoryValue = permit.FinalValue;
        state.AverageUnitCost = permit.FinalQuantity == 0m
            ? 0m : StockLedgerPrecision.Money(
                permit.FinalValue / permit.FinalQuantity);
        state.CurrentUnitCost = state.AverageUnitCost;
        var last = rootFacts.OrderByDescending(f => f.PostingLineNo)
            .ThenByDescending(f => f.SplitOrdinal).First();
        state.LastValuationFactId = last.Id;
        state.LastPostingSequence = context.Posting.PostingSequence;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
