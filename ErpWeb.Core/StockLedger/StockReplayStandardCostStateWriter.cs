using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Applies the final Standard item state only after physical piles and every
/// fact written by this replay root explain the projected quantity and value.
/// The caller still reconciles dependent modules before sealing the root.
/// </summary>
public static class StockReplayStandardCostStateWriter
{
    public static async Task ApplyAsync(
        StockPostingContext context,
        StockReplayStandardWritePermit permit,
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
            || context.CostMethod != StockCostMethods.Standard
            || string.IsNullOrWhiteSpace(permit.PlanHash))
            throw Invalid("The Standard state permit belongs to another replay root.");

        var db = context.Db;
        await db.SaveChangesAsync(cancellationToken);
        var state = await db.StockCostStates.SingleOrDefaultAsync(s =>
            s.CompanyCode == context.CompanyCode
            && s.BranchCode == context.BranchCode
            && s.ItemCode == permit.ItemCode
            && s.CostMethod == StockCostMethods.Standard, cancellationToken);
        if (state is null || state.OnHandBaseQty != permit.CurrentQuantity
            || state.InventoryValue != permit.CurrentValue)
            throw Invalid($"Item {permit.ItemCode} Standard state changed after planning.");
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
            throw Invalid($"Item {permit.ItemCode} physical piles differ from Standard replay.");

        var rootFacts = await db.StockValuationFacts.AsNoTracking()
            .Where(f => f.StockPostingId == context.Posting.Id
                && f.ItemCode == permit.ItemCode)
            .ToArrayAsync(cancellationToken);
        if (rootFacts.Any(f => f.CompanyCode != context.CompanyCode
                || f.BranchCode != context.BranchCode
                || f.LedgerEpochId != context.Epoch.Id
                || f.CostMethod != StockCostMethods.Standard
                || f.ValuationStatus is not (
                    StockValuationStatuses.Valued or StockValuationStatuses.Reversed)
                || f.Direction is not (-1 or 1)
                || f.BaseQty < 0m || f.CostAmount < 0m
                || IvQty.Round(f.BaseQty) != f.BaseQty
                || StockLedgerPrecision.Money(f.CostAmount) != f.CostAmount))
            throw Invalid($"Item {permit.ItemCode} has an invalid Standard replay fact.");
        var signedQuantity = StockLedgerPrecision.Quantity(
            rootFacts.Sum(f => f.SignedQty));
        var signedValue = StockLedgerPrecision.Money(
            rootFacts.Sum(f => f.SignedValue));
        if (signedQuantity != StockLedgerPrecision.Quantity(
                permit.FinalQuantity - permit.CurrentQuantity)
            || signedValue != StockLedgerPrecision.Money(
                permit.FinalValue - permit.CurrentValue))
            throw Invalid($"Item {permit.ItemCode} Standard facts do not explain its replay delta.");
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
