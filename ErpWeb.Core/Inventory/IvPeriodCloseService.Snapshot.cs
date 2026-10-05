using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvPeriodCloseService
{
    /// <summary>Blocking reconciliation finding codes (D9). UNEXPECTED_BALANCE is deliberately advisory.</summary>
    private static readonly HashSet<string> BlockingFindingCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "MISMATCH",
        "HISTORY_SLICE_MISMATCH",
        "ORPHAN_HISTORY",
        "DUPLICATE_SLICE",
        "STOCK_COUNT_BATCH_NOT_POSTED",
        "STOCK_COUNT_UNPOSTED_VARIANCE",
        "STOCK_COUNT_BATCH_STILL_POSTED"
    };

    /// <summary>
    /// Replays <c>IvTrxHistory</c> per 7-part stock slice and derives the closing snapshot. The replay
    /// is the self-audit: <c>OpeningQty</c> is always recomputed from the ledger, never copied from a
    /// prior snapshot. Leg classification is column-driven (D18): a transfer's two legs belong to two
    /// slices, so each history row is projected into an in-leg and/or an out-leg.
    /// </summary>
    private async Task<IvPeriodCloseSnapshotResult> BuildSnapshotAsync(
        AppDbContext db,
        string company,
        string branch,
        DateTime periodFrom,
        DateTime periodTo,
        IReadOnlyDictionary<IvStockSliceKey, decimal> priorClosingBySlice,
        bool firstClose,
        CancellationToken cancellationToken)
    {
        var piles = await db.IvBalLocs.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch)
            .ToListAsync(cancellationToken);

        var movements = await IvStockHistoryRepository.MovementsForBranch(db, company, branch)
            .ToListAsync(cancellationToken);

        var sliceById = new Dictionary<int, IvStockSliceKey>();
        var pileQtyBySlice = new Dictionary<IvStockSliceKey, decimal>();
        var pileUomBySlice = new Dictionary<IvStockSliceKey, string?>();
        foreach (var p in piles)
        {
            var slice = IvStockSliceKey.Create(
                p.CompanyCode, p.BranchCode, p.ICode, p.WhCode, p.LocCode, p.LotNo, p.IStatus);
            sliceById[p.Id] = slice;
            pileQtyBySlice[slice] = pileQtyBySlice.GetValueOrDefault(slice) + p.StdQty;
            pileUomBySlice.TryAdd(slice, p.StdUom);
        }

        // Historical money comes only from sealed valuation facts. Mutable balance/master prices
        // are intentionally excluded from the close calculation.
        var itemCodes = piles.Select(x => x.ICode)
            .Concat(movements.Select(x => x.ICode))
            .Distinct()
            .ToList();
        var valuedFacts = itemCodes.Count == 0
            ? []
            : await db.StockValuationFacts.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.BranchCode == branch
                            && itemCodes.Contains(x.ItemCode)
                            && x.EffectiveAt < periodTo.Date.AddDays(1)
                            && x.StockPosting!.SealedAtUtc != null)
                .Select(x => new { x.ItemCode, x.Direction, x.BaseQty, x.CostAmount })
                .ToListAsync(cancellationToken);
        var authoritativeUnitCost = valuedFacts.GroupBy(x => x.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x =>
            {
                var qty = x.Sum(y => y.BaseQty * y.Direction);
                var value = x.Sum(y => y.CostAmount * y.Direction);
                return qty == 0m ? 0m : decimal.Round(value / qty, 6, MidpointRounding.AwayFromZero);
            }, StringComparer.OrdinalIgnoreCase);

        var openingBySlice = new Dictionary<IvStockSliceKey, decimal>();
        var inBySlice = new Dictionary<IvStockSliceKey, decimal>();
        var outBySlice = new Dictionary<IvStockSliceKey, decimal>();
        var adjustBySlice = new Dictionary<IvStockSliceKey, decimal>();
        var postLegsBySlice = new Dictionary<IvStockSliceKey, decimal>();
        var legCountBySlice = new Dictionary<IvStockSliceKey, int>();

        static void Add(Dictionary<IvStockSliceKey, decimal> map, IvStockSliceKey slice, decimal qty) =>
            map[slice] = map.GetValueOrDefault(slice) + qty;

        foreach (var h in movements)
        {
            var date = h.TrxDtTime.Date;
            var toSlice = h.ToBalLocId is int toId && sliceById.TryGetValue(toId, out var ts)
                ? ts : (IvStockSliceKey?)null;
            var fromSlice = h.FromBalLocId is int fromId && sliceById.TryGetValue(fromId, out var fs)
                ? fs : (IvStockSliceKey?)null;

            var inQty = h.ToStdQty ?? 0m;
            var outQty = h.FrStdQty ?? 0m;

            if (date < periodFrom)
            {
                if (toSlice is { } to) Add(openingBySlice, to, inQty);
                if (fromSlice is { } from) Add(openingBySlice, from, -outQty);
            }
            else if (date > periodTo)
            {
                if (toSlice is { } to) Add(postLegsBySlice, to, inQty);
                if (fromSlice is { } from) Add(postLegsBySlice, from, -outQty);
            }
            else
            {
                if (toSlice is { } to)
                {
                    Add(inBySlice, to, inQty);
                    legCountBySlice[to] = legCountBySlice.GetValueOrDefault(to) + 1;
                }

                if (fromSlice is { } from)
                {
                    Add(outBySlice, from, outQty);
                    legCountBySlice[from] = legCountBySlice.GetValueOrDefault(from) + 1;
                }

                if (string.Equals(h.TrxType, IvTrxTypes.StockAdjustment, StringComparison.OrdinalIgnoreCase))
                {
                    if (toSlice is { } toAdj) Add(adjustBySlice, toAdj, inQty);
                    if (fromSlice is { } fromAdj) Add(adjustBySlice, fromAdj, -outQty);
                }
            }
        }

        // The reconciliation domain Ω — the union of both sides (D11 / S2).
        var omega = new SortedSet<IvStockSliceKey>(pileQtyBySlice.Keys);
        foreach (var slice in openingBySlice.Keys) omega.Add(slice);
        foreach (var slice in inBySlice.Keys) omega.Add(slice);
        foreach (var slice in outBySlice.Keys) omega.Add(slice);

        var lines = new List<IvPeriodCloseSnapshotLine>();
        var skippedZero = 0;
        var openingAdjustSlices = 0;
        var carryForwardMismatch = 0;
        var d11Mismatches = new List<IvPeriodCloseSnapshotLine>();
        var currentCheckApplies = true;

        foreach (var slice in omega)
        {
            var opening = IvQty.Round(openingBySlice.GetValueOrDefault(slice));
            var inQ = IvQty.Round(inBySlice.GetValueOrDefault(slice));
            var outQ = IvQty.Round(outBySlice.GetValueOrDefault(slice));
            var adjust = IvQty.Round(adjustBySlice.GetValueOrDefault(slice));
            var ledgerClosing = IvQty.Round(opening + inQ - outQ);
            var postLegs = IvQty.Round(postLegsBySlice.GetValueOrDefault(slice));
            var pileQty = IvQty.Round(pileQtyBySlice.GetValueOrDefault(slice));
            var anchorClosing = IvQty.Round(pileQty - postLegs);
            var sliceCheckApplies = postLegs == 0m;
            if (!sliceCheckApplies)
            {
                currentCheckApplies = false;
            }

            // Carry-forward against the immediately preceding closed period (D13: absent prior row = 0).
            var carryForwardOk = firstClose
                || priorClosingBySlice.GetValueOrDefault(slice) == opening;

            decimal openingAdjust;
            decimal closing;
            if (firstClose)
            {
                openingAdjust = IvQty.Round(anchorClosing - ledgerClosing);
                closing = anchorClosing;
            }
            else
            {
                openingAdjust = 0m;
                closing = ledgerClosing;
            }

            if (openingAdjust != 0m)
            {
                openingAdjustSlices++;
            }

            if (!carryForwardOk)
            {
                carryForwardMismatch++;
            }

            decimal? currentDelta = sliceCheckApplies
                ? IvQty.Round(ledgerClosing - pileQty)
                : (decimal?)null;

            var line = new IvPeriodCloseSnapshotLine
            {
                Slice = slice,
                OpeningQty = opening,
                OpeningAdjustQty = openingAdjust,
                InQty = inQ,
                OutQty = outQ,
                AdjustNetQty = adjust,
                ClosingQty = closing,
                StdUom = pileUomBySlice.GetValueOrDefault(slice),
                UnitPrice = authoritativeUnitCost.GetValueOrDefault(slice.ICode),
                LegCount = legCountBySlice.GetValueOrDefault(slice),
                CarryForwardOk = carryForwardOk,
                CurrentBalanceDelta = currentDelta
            };

            // D11: on a later close, the pile and the ledger must agree (only when no post-legs exist).
            if (!firstClose && sliceCheckApplies && anchorClosing != ledgerClosing)
            {
                d11Mismatches.Add(line);
            }

            // D13: store only non-zero closers.
            if (closing != 0m)
            {
                line.ClosingValue = IvQty.Round(closing * line.UnitPrice);
                lines.Add(line);
            }
            else
            {
                skippedZero++;
            }
        }

        return new IvPeriodCloseSnapshotResult
        {
            Lines = lines,
            SkippedZeroSlices = skippedZero,
            OpeningAdjustSlices = openingAdjustSlices,
            CarryForwardMismatchSlices = carryForwardMismatch,
            CurrentBalanceCheckApplies = currentCheckApplies,
            CurrentBalanceMismatchSlices = d11Mismatches.Count,
            D11Mismatches = d11Mismatches
        };
    }

    private sealed class IvPeriodCloseSnapshotLine
    {
        public IvStockSliceKey Slice { get; init; }
        public decimal OpeningQty { get; init; }
        public decimal OpeningAdjustQty { get; init; }
        public decimal InQty { get; init; }
        public decimal OutQty { get; init; }
        public decimal AdjustNetQty { get; init; }
        public decimal ClosingQty { get; set; }
        public string? StdUom { get; init; }
        public decimal UnitPrice { get; init; }
        public decimal ClosingValue { get; set; }
        public int LegCount { get; init; }
        public bool CarryForwardOk { get; init; }
        public decimal? CurrentBalanceDelta { get; init; }
    }

    private sealed class IvPeriodCloseSnapshotResult
    {
        public IReadOnlyList<IvPeriodCloseSnapshotLine> Lines { get; init; } = [];
        public int SkippedZeroSlices { get; init; }
        public int OpeningAdjustSlices { get; init; }
        public int CarryForwardMismatchSlices { get; init; }
        public bool CurrentBalanceCheckApplies { get; init; }
        public int CurrentBalanceMismatchSlices { get; init; }
        public IReadOnlyList<IvPeriodCloseSnapshotLine> D11Mismatches { get; init; } = [];
    }
}
