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

public readonly record struct MachineScheduleProductiveBoundary(
    DateTime Resolved,
    int CompletedSlots);

/// <summary>
/// Pure segment math. It never reads a database and never changes production quantities.
/// </summary>
public static class MachineScheduleSegmentCalculator
{
    public static async Task<MachineScheduleQuantityPlan> ComputeQuantityAsync(
        string companyCode,
        ProductionWorkOrderOperation operation,
        ProductionWorkOrderMachine machine,
        decimal operationOutputQty,
        IUomConversionService uom,
        CancellationToken cancellationToken = default)
    {
        var operationUom = (operation.PlannedOutputUom ?? string.Empty).Trim();
        var cycleUom = string.IsNullOrWhiteSpace(machine.OutputPerCycleUom)
            ? operationUom : machine.OutputPerCycleUom.Trim();
        var conversion = await uom.ConvertAsync(
            companyCode, operation.OperationCode, operationOutputQty,
            operationUom, cycleUom, cancellationToken);
        if (!conversion.Succeeded)
            throw new MachineScheduleQuantityConversionException(
                conversion.FailureMessage ?? $"No approved conversion from {operationUom} to {cycleUom}.");

        return ComputeQuantity(conversion.Quantity, machine.OutputPerCycle,
            machine.ParallelMachineCount, machine.CycleSeconds);
    }

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

    public static MachineScheduleProductiveBoundary ResolveCompletedProductiveBoundary(
        IReadOnlyList<TimeInterval> fragments,
        DateTime requested,
        decimal overheadSeconds,
        decimal cycleSeconds)
    {
        var ordered = fragments.Where(x => x.End > x.Start).OrderBy(x => x.Start).ToList();
        if (ordered.Count == 0)
            return new MachineScheduleProductiveBoundary(requested, 0);
        if (cycleSeconds <= 0m)
            return new MachineScheduleProductiveBoundary(ordered[0].Start, 0);

        var elapsed = ordered.Sum(x => x.Start >= requested
            ? 0m
            : (decimal)((x.End < requested ? x.End : requested) - x.Start).TotalSeconds);
        var productive = Math.Max(0m, elapsed - Math.Max(0m, overheadSeconds));
        var slots = (int)Math.Floor(productive / cycleSeconds);
        if (slots == 0)
            return new MachineScheduleProductiveBoundary(ordered[0].Start, 0);

        var rawSecondsToBoundary = Math.Max(0m, overheadSeconds) + slots * cycleSeconds;
        foreach (var fragment in ordered)
        {
            var seconds = (decimal)(fragment.End - fragment.Start).TotalSeconds;
            if (rawSecondsToBoundary <= seconds)
                return new MachineScheduleProductiveBoundary(
                    fragment.Start.AddSeconds((double)rawSecondsToBoundary), slots);
            rawSecondsToBoundary -= seconds;
        }
        return new MachineScheduleProductiveBoundary(ordered[^1].End, slots);
    }

    /// <summary>Additional planning overhead when production genuinely restarts (not a calendar break).</summary>
    public static TimeSpan RestartOverheadSeconds(ProductionWorkOrderMachine machine, bool includeRestartSetup)
    {
        if (!includeRestartSetup)
            return TimeSpan.Zero;
        return TimeSpan.FromSeconds((double)(machine.SetupSeconds + machine.ConversionSeconds));
    }
}

internal sealed class MachineScheduleQuantityConversionException(string message) : Exception(message);
