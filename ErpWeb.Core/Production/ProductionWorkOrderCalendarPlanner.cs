using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ErpWeb.Core.Planning;

namespace ErpWeb.Core.Production;

/// <summary>Canonical hash of normalized scheduling inputs (no random temp IDs).</summary>
public static class ProductionSchedulingInputHasher
{
    public static string Compute(
        string schedulingDirection,
        DateOnly plannedStart,
        DateOnly plannedCompletion,
        decimal plannedQty,
        IEnumerable<ProductionWorkOrderOperationVm> operations)
    {
        var sb = new StringBuilder();
        sb.Append(Normalize(schedulingDirection)).Append('|');
        sb.Append(plannedStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('|');
        sb.Append(plannedCompletion.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('|');
        sb.Append(plannedQty.ToString("0.####", CultureInfo.InvariantCulture)).Append('\n');

        foreach (var op in operations
                     .OrderBy(o => o.SequenceNo)
                     .ThenBy(o => o.OperationCode ?? "", StringComparer.OrdinalIgnoreCase)
                     .ThenBy(o => StableOpKey(o), StringComparer.Ordinal))
        {
            sb.Append(op.SequenceNo).Append(';')
                .Append(op.OperationCode ?? "").Append(';')
                .Append(StableOpKey(op)).Append('\n');

            foreach (var r in op.Resources
                         .Where(x => string.Equals(x.ResourceType, "MACHINE", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(x => x.ResourceCode ?? "", StringComparer.OrdinalIgnoreCase)
                         .ThenBy(x => x.SequenceNo)
                         .ThenBy(x => StableResKey(x), StringComparer.Ordinal))
            {
                sb.Append("MACHINE;").Append(r.ResourceCode ?? "").Append(';')
                    .Append(r.SequenceNo).Append(';')
                    .Append(StableResKey(r)).Append(';')
                    .Append(Fmt(r.SetupMinutes)).Append(';')
                    .Append(Fmt(r.RunMinutes)).Append(';')
                    .Append(Fmt(r.QueueMinutes)).Append('\n');
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string StableOpKey(ProductionWorkOrderOperationVm op) =>
        $"{op.SequenceNo}|{op.WorkCentreCode}|{op.OperationCode}";

    private static string StableResKey(ProductionWorkOrderResourceVm r) =>
        $"{r.SequenceNo}|{r.ResourceType}|{r.ResourceCode}";

    private static string Normalize(string? s) => (s ?? "").Trim().ToUpperInvariant();
    private static string Fmt(decimal d) => d.ToString("0.####", CultureInfo.InvariantCulture);
}

/// <summary>
/// Applies calendar-aware scheduling to WO operations (deferred DateOnly, SequenceNo parallel).
/// </summary>
public static class ProductionWorkOrderCalendarPlanner
{
    public static async Task<(IReadOnlyList<ProductionWorkOrderOperationVm> Operations, DateTime HeaderStart, DateTime HeaderCompletion)>
        ApplyAsync(
            IProductionCalendarScheduleDataLoader loader,
            string companyCode,
            IReadOnlyList<ProductionWorkOrderOperationVm> operations,
            DateOnly headerStart,
            DateOnly headerCompletion,
            string schedulingDirection,
            CancellationToken ct = default)
    {
        var direction = string.Equals(schedulingDirection, "Backward", StringComparison.OrdinalIgnoreCase)
            ? ScheduleDirection.Backward
            : ScheduleDirection.Forward;

        var ops = operations.Select(CloneOp).ToList();
        var machineOps = ops.Where(HasPositiveMachineDuration).ToList();
        if (machineOps.Count == 0)
        {
            // Skip calendar scheduler — stamp header dates
            var headerStartDt = headerStart.ToDateTime(TimeOnly.MinValue);
            var headerCompletionDt = headerCompletion.ToDateTime(TimeOnly.MinValue);
            foreach (var op in ops)
            {
                op.PlannedStartDate = headerStartDt;
                op.PlannedCompletionDate = headerCompletionDt;
            }
            return (ops, headerStartDt.Date, headerCompletionDt.Date);
        }

        var groups = direction == ScheduleDirection.Forward
            ? ops.GroupBy(o => o.SequenceNo).OrderBy(g => g.Key).ToList()
            : ops.GroupBy(o => o.SequenceNo).OrderByDescending(g => g.Key).ToList();

        DateOnly? pendingDateOnly = direction == ScheduleDirection.Forward ? headerStart : headerCompletion;
        DateTime? dateTimeAnchor = null;
        var leadingZero = new List<ProductionWorkOrderOperationVm>();
        DateTime? overallStart = null;
        DateTime? overallEnd = null;

        foreach (var group in groups)
        {
            var groupOps = group.ToList();
            var machineBacked = groupOps.Where(HasPositiveMachineDuration).ToList();
            if (machineBacked.Count == 0)
            {
                if (dateTimeAnchor is null)
                {
                    leadingZero.AddRange(groupOps);
                    continue;
                }
                foreach (var op in groupOps)
                {
                    op.PlannedStartDate = dateTimeAnchor;
                    op.PlannedCompletionDate = dateTimeAnchor;
                }
                continue;
            }

            // First machine-backed group resolves DateOnly
            DateTime? groupStart = null;
            DateTime? groupEnd = null;

            foreach (var op in machineBacked)
            {
                DateTime? opStart = null;
                DateTime? opEnd = null;
                foreach (var res in op.Resources.Where(r =>
                             string.Equals(r.ResourceType, "MACHINE", StringComparison.OrdinalIgnoreCase)
                             && (r.SetupMinutes + r.RunMinutes + r.QueueMinutes) > 0))
                {
                    var duration = TimeSpan.FromMinutes((double)decimal.Round(
                        res.SetupMinutes + res.RunMinutes + res.QueueMinutes, 4, MidpointRounding.AwayFromZero));

                    var horizonStart = (pendingDateOnly ?? headerStart).AddYears(-2);
                    var horizonEnd = (pendingDateOnly ?? headerCompletion).AddYears(2);
                    var data = await loader.LoadAsync(companyCode, res.ResourceCode, horizonStart, horizonEnd, ct);

                    DateTime anchor;
                    if (pendingDateOnly is DateOnly d0 && dateTimeAnchor is null)
                    {
                        anchor = direction == ScheduleDirection.Forward
                            ? ProductionCalendarScheduler.ResolveInitialForwardAnchor(data, d0)
                            : ProductionCalendarScheduler.ResolveInitialBackwardAnchor(data, d0);
                    }
                    else
                    {
                        anchor = dateTimeAnchor
                                 ?? (direction == ScheduleDirection.Forward
                                     ? ProductionCalendarScheduler.ResolveInitialForwardAnchor(data, headerStart)
                                     : ProductionCalendarScheduler.ResolveInitialBackwardAnchor(data, headerCompletion));
                    }

                    var result = ProductionCalendarScheduler.ScheduleMachine(data, anchor, duration, direction);
                    opStart = opStart is null ? result.ActualStart : Min(opStart.Value, result.ActualStart);
                    opEnd = opEnd is null ? result.ActualCompletion : Max(opEnd.Value, result.ActualCompletion);
                }

                op.PlannedStartDate = opStart;
                op.PlannedCompletionDate = opEnd;
                if (opStart is not null)
                    groupStart = groupStart is null ? opStart : Min(groupStart.Value, opStart.Value);
                if (opEnd is not null)
                    groupEnd = groupEnd is null ? opEnd : Max(groupEnd.Value, opEnd.Value);
            }

            // Stamp labour-only ops in this group at group boundary
            foreach (var op in groupOps.Where(o => !HasPositiveMachineDuration(o)))
            {
                var stamp = direction == ScheduleDirection.Forward ? groupStart : groupEnd;
                op.PlannedStartDate = stamp;
                op.PlannedCompletionDate = stamp;
            }

            // Stamp leading zeros once first machine group resolved
            if (leadingZero.Count > 0 && groupStart is not null && groupEnd is not null)
            {
                var stamp = direction == ScheduleDirection.Forward ? groupStart : groupEnd;
                foreach (var op in leadingZero)
                {
                    op.PlannedStartDate = stamp;
                    op.PlannedCompletionDate = stamp;
                }
                leadingZero.Clear();
            }

            pendingDateOnly = null;
            dateTimeAnchor = direction == ScheduleDirection.Forward ? groupEnd : groupStart;

            if (groupStart is not null)
                overallStart = overallStart is null ? groupStart : Min(overallStart.Value, groupStart.Value);
            if (groupEnd is not null)
                overallEnd = overallEnd is null ? groupEnd : Max(overallEnd.Value, groupEnd.Value);
        }

        var hs = (overallStart ?? headerStart.ToDateTime(TimeOnly.MinValue)).Date;
        var hc = (overallEnd ?? headerCompletion.ToDateTime(TimeOnly.MinValue)).Date;
        return (ops, hs, hc);
    }

    private static bool HasPositiveMachineDuration(ProductionWorkOrderOperationVm op) =>
        op.Resources.Any(r =>
            string.Equals(r.ResourceType, "MACHINE", StringComparison.OrdinalIgnoreCase)
            && (r.SetupMinutes + r.RunMinutes + r.QueueMinutes) > 0m);

    private static ProductionWorkOrderOperationVm CloneOp(ProductionWorkOrderOperationVm o) => new()
    {
        SequenceNo = o.SequenceNo,
        WorkCentreCode = o.WorkCentreCode,
        WorkCentreDescription = o.WorkCentreDescription,
        OperationCode = o.OperationCode,
        OperationDescription = o.OperationDescription,
        IsFinalOperation = o.IsFinalOperation,
        PlannedStartDate = o.PlannedStartDate,
        PlannedCompletionDate = o.PlannedCompletionDate,
        PlannedQty = o.PlannedQty,
        SetupLossQty = o.SetupLossQty,
        OperationLossQty = o.OperationLossQty,
        RemainingQty = o.RemainingQty,
        Resources = o.Resources.Select(r => new ProductionWorkOrderResourceVm
        {
            SequenceNo = r.SequenceNo,
            ResourceType = r.ResourceType,
            ResourceCode = r.ResourceCode,
            ResourceDescription = r.ResourceDescription,
            PlannedUnits = r.PlannedUnits,
            SetupMinutes = r.SetupMinutes,
            RunMinutes = r.RunMinutes,
            QueueMinutes = r.QueueMinutes,
            Rate = r.Rate,
            PlannedAmount = r.PlannedAmount
        }).ToList()
    };

    private static DateTime Min(DateTime a, DateTime b) => a <= b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a >= b ? a : b;
}
