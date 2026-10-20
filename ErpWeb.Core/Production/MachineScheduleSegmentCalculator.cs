using ErpWeb.Core.Inventory;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Core.Production;

/// <summary>
/// Quantity/cycle contract for one business segment. Mirrors the canonical Work Order quantity
/// calculator exactly: ceiling on cycles, ceiling on parallel slots, and run minutes rounded to
/// quantity precision.
/// </summary>
public readonly record struct MachineScheduleQuantityPlan(
    decimal OutputQty,
    decimal CycleCount,
    decimal CycleSlots,
    decimal RunMinutes)
{
    public bool IsEmpty => OutputQty <= 0m || CycleCount <= 0m;
}

/// <summary>Resolved cycle-safe split boundary.</summary>
public readonly record struct MachineScheduleSplitBoundary(
    DateTime Requested,
    DateTime Resolved,
    int SlotIndex,
    bool WasAdjusted,
    string Explanation);

/// <summary>
/// Pure segment math. It never reads a database and never changes production quantities.
/// </summary>
public static class MachineScheduleSegmentCalculator
{
    /// <summary>
    /// Discrete cycle math. A visual split may only sit on a productive cycle-slot boundary, so
    /// this is also the boundary calculator's source of truth.
    /// </summary>
    public static MachineScheduleQuantityPlan ComputeQuantity(
        decimal outputQty,
        decimal outputPerCycle,
        int parallelMachineCount,
        decimal cycleSeconds)
    {
        if (outputQty <= 0m || outputPerCycle <= 0m || parallelMachineCount < 1 || cycleSeconds < 0m)
            return new MachineScheduleQuantityPlan(Math.Max(0m, outputQty), 0m, 0m, 0m);

        var cycleCount = Math.Ceiling(outputQty / outputPerCycle);
        var cycleSlots = Math.Ceiling(cycleCount / parallelMachineCount);
        var runMinutes = decimal.Round(cycleSlots * cycleSeconds / 60m, IvQty.Scale, MidpointRounding.AwayFromZero);
        return new MachineScheduleQuantityPlan(outputQty, cycleCount, cycleSlots, runMinutes);
    }

    public static decimal RunMinutes(decimal cycleSlots, decimal cycleSeconds) =>
        decimal.Round(cycleSlots * cycleSeconds / 60m, IvQty.Scale, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Snaps a requested split point to a whole cycle-slot boundary measured from the segment's
    /// productive start. Rounding is <b>up</b>: a partial cycle is a productive cycle.
    /// </summary>
    public static MachineScheduleSplitBoundary SnapToCycleBoundary(
        DateTime productiveStart,
        DateTime requested,
        decimal cycleSeconds)
    {
        if (cycleSeconds <= 0m)
        {
            return new MachineScheduleSplitBoundary(requested, productiveStart, 0, true,
                "Machine cycle time is invalid; no split boundary can be resolved.");
        }

        var slot = TimeSpan.FromSeconds((double)cycleSeconds);
        var elapsed = requested - productiveStart;
        if (elapsed <= TimeSpan.Zero)
        {
            return new MachineScheduleSplitBoundary(requested, productiveStart, 0, true,
                $"Requested split is before the productive start; resolved to {productiveStart:yyyy-MM-dd HH:mm}.");
        }

        var slots = (int)Math.Ceiling(elapsed.TotalSeconds / slot.TotalSeconds);
        var resolved = productiveStart.AddTicks(slot.Ticks * slots);
        var adjusted = resolved != requested;
        return new MachineScheduleSplitBoundary(
            requested,
            resolved,
            slots,
            adjusted,
            adjusted
                ? $"Split snapped to the next cycle boundary {resolved:yyyy-MM-dd HH:mm} ({cycleSeconds:n0}s cycles)."
                : $"Split sits exactly on cycle boundary {resolved:yyyy-MM-dd HH:mm}.");
    }

    /// <summary>Additional planning overhead when production genuinely restarts (not a calendar break).</summary>
    public static TimeSpan RestartOverheadSeconds(ProductionWorkOrderMachine machine, bool includeRestartSetup)
    {
        if (!includeRestartSetup)
            return TimeSpan.Zero;
        return TimeSpan.FromSeconds((double)(machine.SetupSeconds + machine.ConversionSeconds));
    }
}
