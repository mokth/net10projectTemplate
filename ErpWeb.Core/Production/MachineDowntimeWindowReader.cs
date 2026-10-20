using ErpWeb.Core.Planning;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

/// <summary>One machine-unavailable interval derived from an existing maintenance fact.</summary>
public readonly record struct MachineDowntimeWindow(
    string MachineCode,
    DateTime Start,
    DateTime End,
    string MaintenanceType,
    string MaintenanceStatus,
    bool IsOpenEnded,
    bool IsHistorical);

/// <summary>
/// Reuses <c>PrMacMaintenance</c> for breakdown/repair/service downtime. There is deliberately no
/// second breakdown entity.
/// <para>
/// OPEN / IN_PROGRESS block from Start to End, or from Start to the loaded horizon end when End is
/// unknown. COMPLETED rows keep their actual [Start, End) interval as historical downtime so a
/// completed repair does not suddenly turn back into working capacity. CANCELLED rows never block.
/// </para>
/// </summary>
public static class MachineDowntimeWindowReader
{
    public static async Task<IReadOnlyList<MachineDowntimeWindow>> LoadAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyCollection<string> machineCodes,
        DateTime horizonStart,
        DateTime horizonEnd,
        CancellationToken cancellationToken = default)
    {
        var codes = machineCodes
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (codes.Count == 0)
            return [];

        var rows = new List<PrMacMaintenance>();
        foreach (var batch in codes.Chunk(800))
        {
            rows.AddRange(await db.PrMacMaintenances.AsNoTracking()
                .Where(x => x.CompCode == companyCode
                    && x.MacCode != null && batch.Contains(x.MacCode)
                    && x.StartDateTime != null
                    && (x.MType == null || x.MType != PrMaintenanceTypes.Preventive)
                    && x.PreventiveUid == null
                    && (x.Status == PrMaintenanceStatuses.Open
                        || x.Status == PrMaintenanceStatuses.InProgress
                        || x.Status == PrMaintenanceStatuses.Completed))
                .ToListAsync(cancellationToken));
        }

        var windows = new List<MachineDowntimeWindow>();
        foreach (var row in rows)
        {
            if (row.StartDateTime is not DateTime start || row.MacCode is null)
                continue;

            var isCompleted = string.Equals(row.Status, PrMaintenanceStatuses.Completed, StringComparison.OrdinalIgnoreCase);
            DateTime? end = row.EndDateTime;
            if (isCompleted && end is null)
                continue;
            if (!isCompleted && end is null)
                end = horizonEnd;

            var resolvedEnd = end!.Value;
            if (resolvedEnd <= start)
                continue;
            if (resolvedEnd <= horizonStart || start >= horizonEnd)
                continue;

            windows.Add(new MachineDowntimeWindow(
                row.MacCode,
                DateTime.SpecifyKind(start, DateTimeKind.Unspecified),
                DateTime.SpecifyKind(resolvedEnd, DateTimeKind.Unspecified),
                row.MType ?? PrMaintenanceTypes.Other,
                row.Status!,
                IsOpenEnded: !isCompleted && row.EndDateTime is null,
                IsHistorical: isCompleted));
        }

        return windows;
    }

    public static IReadOnlyList<PreventiveWindow> ToBlockingWindows(IEnumerable<MachineDowntimeWindow> windows) =>
        windows.Select(x => new PreventiveWindow { Start = x.Start, End = x.End }).ToList();
}
