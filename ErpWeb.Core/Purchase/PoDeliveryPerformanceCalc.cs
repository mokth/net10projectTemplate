namespace ErpWeb.Core.Purchase;

/// <summary>
/// Pure delivery-performance calculation contract (AsOf-clipped GR walk + current BalanceQty).
/// </summary>
public static class PoDeliveryPerformanceCalc
{
    public readonly record struct ReceiptMovement(int BatchNo, DateTime TrxDtTime, short TrxLineNo, int DetailId, decimal Qty);

    public sealed class GrWalkResult
    {
        public DateTime? FirstGrnDate { get; init; }
        public DateTime? LastGrnDate { get; init; }
        public DateTime? FullyReceivedDate { get; init; }
        public decimal OnTimeReceivedQty { get; init; }
        public decimal LateReceivedQty { get; init; }
        public int GrBatchCount { get; init; }
        public int? SingleBatchNo { get; init; }
        public IReadOnlyList<PoDeliveryPerformanceReceiptRow> Receipts { get; init; } = [];
    }

    /// <summary>
    /// Walk posted GR/NG movements (already AsOf-clipped and ordered). Caps OTIF qty at PoPurQty.
    /// </summary>
    public static GrWalkResult WalkReceipts(
        decimal poPurQty,
        DateTime etaDate,
        IReadOnlyList<ReceiptMovement> movements)
    {
        if (movements.Count == 0 || poPurQty <= 0m)
        {
            return new GrWalkResult();
        }

        var ordered = movements
            .OrderBy(x => x.TrxDtTime)
            .ThenBy(x => x.BatchNo)
            .ThenBy(x => x.TrxLineNo)
            .ThenBy(x => x.DetailId)
            .ToList();

        var eta = etaDate.Date;
        decimal remaining = poPurQty;
        decimal onTime = 0m;
        decimal late = 0m;
        decimal cumulative = 0m;
        DateTime? fully = null;
        var receipts = new List<PoDeliveryPerformanceReceiptRow>(ordered.Count);
        var batchNos = new HashSet<int>();

        foreach (var m in ordered)
        {
            var qty = PoOrderCalc.RoundQty(m.Qty);
            if (qty <= 0m)
            {
                continue;
            }

            var date = m.TrxDtTime.Date;
            batchNos.Add(m.BatchNo);
            cumulative = PoOrderCalc.RoundQty(cumulative + qty);

            var fulfill = remaining > 0m ? Math.Min(qty, remaining) : 0m;
            if (fulfill > 0m)
            {
                if (date <= eta)
                {
                    onTime = PoOrderCalc.RoundQty(onTime + fulfill);
                }
                else
                {
                    late = PoOrderCalc.RoundQty(late + fulfill);
                }

                remaining = PoOrderCalc.RoundQty(remaining - fulfill);
            }

            if (fully is null && cumulative >= poPurQty)
            {
                fully = date;
            }

            var cappedCum = Math.Min(cumulative, poPurQty);
            var remainingQty = Math.Max(0m, PoOrderCalc.RoundQty(poPurQty - cappedCum));
            var daysVsEta = (date - eta).Days;
            receipts.Add(new PoDeliveryPerformanceReceiptRow
            {
                BatchNo = m.BatchNo,
                TrxDtTime = m.TrxDtTime,
                Qty = qty,
                CumulativeQty = cumulative,
                RemainingQty = remainingQty,
                DaysVsEta = daysVsEta,
                OnTimeOrLate = date <= eta
                    ? PoDeliveryPerformanceStatuses.OnTime
                    : PoDeliveryPerformanceStatuses.Late
            });
        }

        return new GrWalkResult
        {
            FirstGrnDate = ordered[0].TrxDtTime.Date,
            LastGrnDate = ordered[^1].TrxDtTime.Date,
            FullyReceivedDate = fully,
            OnTimeReceivedQty = onTime,
            LateReceivedQty = late,
            GrBatchCount = batchNos.Count,
            SingleBatchNo = batchNos.Count == 1 ? batchNos.First() : null,
            Receipts = receipts
        };
    }

    public static string ResolveStatus(
        decimal poPurQty,
        decimal balanceQty,
        decimal netReceivedQty,
        DateTime? etaDate,
        DateTime? fullyReceivedDate,
        DateTime asOf)
    {
        if (poPurQty <= 0m)
        {
            return PoDeliveryPerformanceStatuses.Exception;
        }

        var asOfDate = asOf.Date;
        if (balanceQty <= 0m)
        {
            if (fullyReceivedDate is DateTime fully && etaDate is DateTime eta)
            {
                var f = fully.Date;
                var e = eta.Date;
                if (f < e)
                {
                    return PoDeliveryPerformanceStatuses.Early;
                }

                if (f == e)
                {
                    return PoDeliveryPerformanceStatuses.OnTime;
                }

                return PoDeliveryPerformanceStatuses.Late;
            }

            return PoDeliveryPerformanceStatuses.Exception;
        }

        if (netReceivedQty > 0m)
        {
            return PoDeliveryPerformanceStatuses.PartiallyReceived;
        }

        if (etaDate is DateTime due && due.Date < asOfDate)
        {
            return PoDeliveryPerformanceStatuses.Overdue;
        }

        return PoDeliveryPerformanceStatuses.NotDue;
    }

    public static int? ComputeOpenDaysLate(decimal balanceQty, DateTime? etaDate, DateTime asOf)
    {
        if (balanceQty <= 0m || etaDate is not DateTime eta || eta.Date >= asOf.Date)
        {
            return null;
        }

        return (asOf.Date - eta.Date).Days;
    }

    public static int? ComputeCompletionDaysLate(string status, DateTime? fullyReceivedDate, DateTime? etaDate)
    {
        if (!IsCompletedOtifStatus(status)
            || fullyReceivedDate is not DateTime fully
            || etaDate is not DateTime eta)
        {
            return null;
        }

        return Math.Max(0, (fully.Date - eta.Date).Days);
    }

    public static bool IsCompletedOtifStatus(string? status) =>
        string.Equals(status, PoDeliveryPerformanceStatuses.Early, StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, PoDeliveryPerformanceStatuses.OnTime, StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, PoDeliveryPerformanceStatuses.Late, StringComparison.OrdinalIgnoreCase);

    public static bool MatchesPreset(
        string? preset,
        decimal poPurQty,
        decimal balanceQty,
        decimal netReceivedQty,
        DateTime? etaDate,
        DateTime? fullyReceivedDate,
        DateTime asOf)
    {
        if (poPurQty <= 0m)
        {
            return false;
        }

        var key = (preset ?? string.Empty).Trim().ToUpperInvariant();
        if (key.Length == 0 || key == PoInquiryWorkbenchPresets.AllOpen)
        {
            return balanceQty > 0m;
        }

        return key switch
        {
            PoInquiryWorkbenchPresets.All => true,
            PoInquiryWorkbenchPresets.Overdue =>
                balanceQty > 0m && etaDate is DateTime e && e.Date < asOf.Date,
            PoInquiryWorkbenchPresets.Partial =>
                netReceivedQty > 0m && balanceQty > 0m,
            PoInquiryWorkbenchPresets.LateCompletion =>
                balanceQty <= 0m && fullyReceivedDate is DateTime f && etaDate is DateTime eta
                && f.Date > eta.Date,
            PoInquiryWorkbenchPresets.OnTimeCompleted =>
                balanceQty <= 0m && fullyReceivedDate is DateTime f2 && etaDate is DateTime eta2
                && f2.Date <= eta2.Date,
            _ => balanceQty > 0m
        };
    }

    public static bool MatchesStatusFilter(string? statusFilter, string deliveryStatus)
    {
        var filter = (statusFilter ?? string.Empty).Trim();
        if (filter.Length == 0)
        {
            return true;
        }

        return string.Equals(filter, deliveryStatus, StringComparison.OrdinalIgnoreCase);
    }
}
