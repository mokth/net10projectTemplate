using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Repositories.Inventory;

namespace ErpWeb.Core.Inventory;

internal static class IvSpShipmentAllocator
{
    internal sealed record Take(int SoLineNo, int FromBalLocId, decimal FrStdQty);

    internal sealed record Result(IReadOnlyList<IvSpLineResult> Lines, IReadOnlyList<Take> Takes);

    public static Result Allocate(
        IReadOnlyList<IvSpRequiredLine> requiredLines,
        IReadOnlyDictionary<int, IvBalLocLockResult> locked,
        Dictionary<int, decimal> remainingByBalLoc,
        string company,
        string branch,
        string location,
        DateTime invDate)
    {
        var lines = new List<IvSpLineResult>();
        var takes = new List<Take>();

        // Shared pool: remaining is mutated across lines in Line ascending order.
        foreach (var line in requiredLines.OrderBy(x => x.Line))
        {
            var requested = IvQty.Round(line.StdQty);
            var remaining = requested;
            var eligible = locked.Values
                .Where(row => IvSpFifoEligibility.MatchesCandidate(
                    row, company, branch, location, line.ICode, line.FrWarehouse, invDate))
                .OrderBy(row => row.TransDate)
                .ThenBy(row => row.LotNo)
                .ThenBy(row => row.Id)
                .ToList();

            var lineAvailable = IvQty.Round(eligible.Sum(row => Math.Max(0m, remainingByBalLoc.GetValueOrDefault(row.Id))));
            var allocated = 0m;

            foreach (var pile in eligible)
            {
                if (remaining <= 0m)
                {
                    break;
                }

                var avail = IvQty.Round(Math.Max(0m, remainingByBalLoc.GetValueOrDefault(pile.Id)));
                if (avail <= 0m)
                {
                    continue;
                }

                var take = IvQty.Round(Math.Min(remaining, avail));
                if (take <= 0m)
                {
                    continue;
                }

                takes.Add(new Take(line.Line, pile.Id, take));
                remainingByBalLoc[pile.Id] = IvQty.Round(avail - take);
                remaining = IvQty.Round(remaining - take);
                allocated = IvQty.Round(allocated + take);
            }

            IvSpLineStatus status;
            string? errorCode = null;
            if (allocated == requested)
            {
                status = IvSpLineStatus.Allocated;
            }
            else if (allocated == 0m && lineAvailable <= 0m)
            {
                status = IvSpLineStatus.NoStock;
                errorCode = "ST000051";
            }
            else
            {
                status = IvSpLineStatus.Insufficient;
                errorCode = "ST000059";
            }

            lines.Add(new IvSpLineResult
            {
                SoLineNo = line.Line,
                Status = status,
                ErrorCode = errorCode,
                RequestedStdQty = requested,
                AllocatedStdQty = allocated,
                CurrentAvailableQty = lineAvailable
            });
        }

        return new Result(lines, takes);
    }
}
