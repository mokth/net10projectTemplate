using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.StockLedger;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayFifoHistoricalPool(
    IReadOnlyList<StockReplayFifoLayerPosition> Layers,
    IReadOnlyList<StockReplayFifoConsumptionPosition> LaterConsumptions,
    IReadOnlyList<StockReplayFifoLayerPosition> LaterReceipts,
    decimal Quantity, decimal Value);

/// <summary>
/// Rewinds the verified active layer snapshot to immediately before one
/// canonical event. Transfer allocations are audit evidence only: both legs
/// leave the branch/item FIFO pool unchanged.
/// </summary>
public static class StockReplayFifoLayerRewinder
{
    public static StockReplayFifoHistoricalPool RewindBefore(
        StockReplayFifoActiveLayerSnapshot snapshot,
        StockReplayEventKey before)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.SnapshotHash)
            || snapshot.Layers.Any(l => l.ReceiptKey is null)
            || snapshot.Layers.Select(l => l.LayerId).Distinct().Count()
                != snapshot.Layers.Count
            || snapshot.ActiveConsumptions.Select(c => c.ConsumptionId)
                .Distinct().Count() != snapshot.ActiveConsumptions.Count
            || StockLedgerPrecision.Quantity(snapshot.Layers.Sum(l => l.RemainingQuantity))
                != snapshot.CurrentQuantity
            || StockLedgerPrecision.Money(snapshot.Layers.Sum(l => l.RemainingValue))
                != snapshot.CurrentValue)
            throw Invalid("FIFO rewind needs a verified active-layer snapshot.");

        var byId = snapshot.Layers.ToDictionary(l => l.LayerId);
        foreach (var consumption in snapshot.ActiveConsumptions)
        {
            if (!byId.TryGetValue(consumption.LayerId, out var layer)
                || consumption.Quantity <= 0m || consumption.Value < 0m
                || consumption.IssueKey.CompareTo(layer.ReceiptKey!.Value) <= 0)
                throw Invalid($"FIFO consumption {consumption.ConsumptionId} has no earlier layer.");
        }
        var later = snapshot.ActiveConsumptions
            .Where(c => c.IssueKey.CompareTo(before) >= 0)
            .OrderBy(c => c.IssueKey).ThenBy(c => c.SplitOrdinal)
            .ThenBy(c => c.ConsumptionId).ToArray();
        var laterDepletions = later.Where(c => c.DepletesPool)
            .GroupBy(c => c.LayerId)
            .ToDictionary(g => g.Key, g => g.ToArray());
        var restored = snapshot.Layers.Select(layer =>
        {
            var effectiveValue = StockLedgerPrecision.Money(
                layer.OriginalValue + layer.AccumulatedAdjustment);
            var allocations = laterDepletions.GetValueOrDefault(layer.LayerId);
            var quantity = StockLedgerPrecision.Quantity(layer.RemainingQuantity
                + (allocations?.Sum(c => c.Quantity) ?? 0m));
            var value = StockLedgerPrecision.Money(layer.RemainingValue
                + (allocations?.Sum(c => c.Value) ?? 0m));
            if (effectiveValue < 0m || quantity > layer.OriginalQuantity
                || value > effectiveValue
                || quantity == 0m && value != 0m)
                throw Invalid($"FIFO layer {layer.LayerId} over-restores during rewind.");
            return layer with
            {
                RemainingQuantity = quantity,
                RemainingValue = value,
                CurrentUnitCost = quantity == 0m ? 0m
                    : StockLedgerPrecision.Money(value / quantity),
                Status = quantity == 0m
                    ? StockFifoLayerStatuses.Closed
                    : StockFifoLayerStatuses.Open
            };
        }).ToArray();
        var laterReceipts = restored
            .Where(l => l.ReceiptKey!.Value.CompareTo(before) >= 0)
            .OrderBy(l => l.ReceiptKey).ThenBy(l => l.LayerId).ToArray();
        if (laterReceipts.Any(l => l.RemainingQuantity != l.OriginalQuantity
                || l.RemainingValue != StockLedgerPrecision.Money(
                    l.OriginalValue + l.AccumulatedAdjustment)))
            throw Invalid("A later FIFO receipt was consumed before the rewind boundary.");
        var historical = restored
            .Where(l => l.ReceiptKey!.Value.CompareTo(before) < 0)
            .OrderBy(l => l.ReceiptKey).ThenBy(l => l.LayerId).ToArray();
        var quantityBefore = StockLedgerPrecision.Quantity(
            historical.Sum(l => l.RemainingQuantity));
        var valueBefore = StockLedgerPrecision.Money(
            historical.Sum(l => l.RemainingValue));
        if (quantityBefore != StockLedgerPrecision.Quantity(
                snapshot.CurrentQuantity
                + later.Where(c => c.DepletesPool).Sum(c => c.Quantity)
                - laterReceipts.Sum(l => l.OriginalQuantity))
            || valueBefore != StockLedgerPrecision.Money(
                snapshot.CurrentValue
                + later.Where(c => c.DepletesPool).Sum(c => c.Value)
                - laterReceipts.Sum(l => l.OriginalValue + l.AccumulatedAdjustment)))
            throw Invalid("FIFO rewind does not conserve quantity and value.");
        return new(historical, later, laterReceipts,
            quantityBefore, valueBefore);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
