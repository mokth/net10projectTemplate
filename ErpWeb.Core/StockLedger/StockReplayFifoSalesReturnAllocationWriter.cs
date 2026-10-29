using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Relinks one FIFO return and appends its value-only correction.
/// </summary>
internal static class StockReplayFifoSalesReturnAllocationWriter
{
    internal readonly record struct ReturnSlice(int Ordinal, decimal Quantity, decimal Value);

    internal static IReadOnlyList<ReturnSlice> Allocate(
        IReadOnlyList<(decimal Quantity, decimal Value)> saleSplits, decimal returnedQuantity)
    {
        var remaining = StockLedgerPrecision.Quantity(returnedQuantity);
        if (remaining <= 0m || remaining != returnedQuantity || saleSplits.Count == 0)
            throw Invalid("FIFO return allocation needs a positive exact quantity and sale splits.");
        var result = new List<ReturnSlice>();
        for (var i = 0; i < saleSplits.Count && remaining > 0m; i++)
        {
            var (quantity, value) = saleSplits[i];
            if (quantity <= 0m || value < 0m || StockLedgerPrecision.Quantity(quantity) != quantity
                || StockLedgerPrecision.Money(value) != value)
                throw Invalid("FIFO return allocation has an invalid sale split.");
            var take = StockLedgerPrecision.Quantity(Math.Min(remaining, quantity));
            var amount = take == quantity ? value : StockLedgerPrecision.Money(value * take / quantity);
            result.Add(new(i, take, amount));
            remaining = StockLedgerPrecision.Quantity(remaining - take);
        }
        if (remaining != 0m)
            throw Invalid("FIFO return quantity exceeds its sale allocations.");
        return result;
    }

    internal static string Signature(IEnumerable<SalesReturnCostAllocation> allocations) =>
        string.Join("|", allocations.Select(a => string.Join(",",
            a.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            a.OriginalValuationFactId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            a.ReturnedBaseQty.ToString(System.Globalization.CultureInfo.InvariantCulture),
            a.ReturnedCostAmount.ToString(System.Globalization.CultureInfo.InvariantCulture))));

    public static async Task<bool> AppendIfRequiredAsync(StockPostingContext context,
        StockReplayFifoActiveLayerSnapshot snapshot, StockReplayFifoTailProjection projection,
        StockReplayFifoProjectedIssue issue, IReadOnlyList<StockValuationFact> replacements,
        int adjustmentLine, CancellationToken cancellationToken)
    {
        var historyId = StockReplayFifoMiRootWriter.HistoryId(issue.Key);
        var affected = snapshot.Layers.Where(l => l.SalesReturn?.SaleHistoryId == historyId).ToArray();
        if (affected.Length == 0)
            return false;
        if (affected.Length != 1 || affected[0].SalesReturn is not { } binding
            || issue.PreviousConsumptions.Count == 0 || issue.Allocations.Count == 0
            || replacements.Count != issue.Allocations.Count
            || replacements.Where((fact, ordinal) => fact.StockPostingId != context.Posting.Id
                || fact.InventoryHistoryId != historyId || fact.SplitOrdinal != ordinal
                || fact.BaseQty != issue.Allocations[ordinal].Quantity
                || fact.CostAmount != issue.Allocations[ordinal].Value).Any()
            || issue.PreviousConsumptions.OrderBy(c => c.SplitOrdinal).First().IssueFactId != binding.SaleFactId)
            throw Invalid("FIFO return relinking needs its exact sale generation.");
        var before = affected[0];
        var final = projection.FinalLayers.SingleOrDefault(l => l.LayerId == before.LayerId);
        var db = context.Db;
        var origin = await db.StockValuationFacts.AsNoTracking()
            .SingleOrDefaultAsync(f => f.Id == before.OriginFactId, cancellationToken);
        var saleOrder = issue.PreviousConsumptions.ToDictionary(c => c.IssueFactId, c => c.SplitOrdinal);
        var allocations = await db.SalesReturnCostAllocations.AsNoTracking()
            .Where(a => a.CompanyCode == context.CompanyCode && a.BranchCode == context.BranchCode
                && a.ReturnValuationFactId == before.OriginFactId && a.ReversesAllocationId == null
                && !db.SalesReturnCostAllocations.Any(r => r.ReversesAllocationId == a.Id))
            .ToArrayAsync(cancellationToken);
        var active = allocations.OrderBy(a => saleOrder.GetValueOrDefault(a.OriginalValuationFactId, int.MaxValue))
            .ThenBy(a => a.Id).ToArray();
        var slices = Allocate(issue.Allocations.Select(a => (a.Quantity, a.Value)).ToArray(), binding.ReturnedQuantity);
        var returnedValue = StockLedgerPrecision.Money(slices.Sum(s => s.Value));
        if (final is null || final.OriginalValue != before.OriginalValue
            || final.OriginalQuantity != before.OriginalQuantity
            || origin is null || origin.MovementCode != "SALE_RETURN_IN"
            || origin.CostMethod != StockCostMethods.Fifo
            || StockLedgerPrecision.Money(origin.CostAmount + before.AccumulatedAdjustment) != binding.ReturnedValue
            || final.AccumulatedAdjustment != StockLedgerPrecision.Money(returnedValue - origin.CostAmount)
            || active.Length == 0 || active[0].Id != binding.AllocationId
            || Signature(active) != binding.AllocationSignature
            || active.Any(a => !saleOrder.ContainsKey(a.OriginalValuationFactId)
                || a.ReturnValuationFactId != origin.Id)
            || StockLedgerPrecision.Quantity(active.Sum(a => a.ReturnedBaseQty)) != binding.ReturnedQuantity
            || StockLedgerPrecision.Money(active.Sum(a => a.ReturnedCostAmount)) != binding.ReturnedValue)
            throw Invalid("FIFO return allocation changed after the receipt was planned.");

        var now = DateTime.UtcNow;
        SalesReturnCostAllocation Copy(SalesReturnCostAllocation allocation) => new()
        {
            CompanyCode = allocation.CompanyCode, BranchCode = allocation.BranchCode,
            ReturnDocumentType = allocation.ReturnDocumentType,
            ReturnDocumentNo = allocation.ReturnDocumentNo,
            ReturnDocumentLine = allocation.ReturnDocumentLine,
            ReturnCostingRevision = allocation.ReturnCostingRevision,
            OriginalOwnerType = allocation.OriginalOwnerType,
            OriginalOwnerDocumentNo = allocation.OriginalOwnerDocumentNo,
            OriginalOwnerDocumentLine = allocation.OriginalOwnerDocumentLine,
            ReturnedBaseQty = allocation.ReturnedBaseQty,
            ReturnedCostAmount = allocation.ReturnedCostAmount,
            StockPostingId = context.Posting.Id,
            ReturnValuationFactId = origin.Id,
            CreatedAtUtc = now, CreatedBy = context.UserId
        };
        foreach (var allocation in active)
        {
            var reversal = Copy(allocation);
            reversal.OriginalValuationFactId = allocation.OriginalValuationFactId;
            reversal.ReversesAllocationId = allocation.Id;
            db.SalesReturnCostAllocations.Add(reversal);
        }
        foreach (var slice in slices)
        {
            var replacement = Copy(active[0]);
            replacement.OriginalValuationFact = replacements[slice.Ordinal];
            replacement.ReturnedBaseQty = slice.Quantity;
            replacement.ReturnedCostAmount = slice.Value;
            db.SalesReturnCostAllocations.Add(replacement);
        }
        var delta = StockLedgerPrecision.Money(final.AccumulatedAdjustment - before.AccumulatedAdjustment);
        if (delta != 0m)
            db.StockValuationFacts.Add(StockReplayFifoSalesReturnValueAdjustment.Create(
                context, origin, delta, adjustmentLine));
        return delta != 0m;
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
