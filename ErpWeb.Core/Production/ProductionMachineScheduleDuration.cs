using ErpWeb.Core.Inventory;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Core.Production;

/// <summary>
/// One canonical definition of the productive machine duration used by every schedule consumer.
/// <para>
/// The current repository contract is:
/// <code>
/// duration =
///     SetupSeconds
///   + ConversionSeconds
///   + QueueSeconds
///   + PlannedRunMinutes * 60
/// </code>
/// <see cref="PlannedRunMinutes"/> is already rounded to quantity precision by
/// <see cref="WorkOrderQuantityCalculator"/>, so this helper must not re-derive run time from
/// <see cref="ProductionWorkOrderMachine.CycleSeconds"/>. Changing that formula is a separate,
/// migration-bearing change; it is deliberately not part of the machine-capacity correction.
/// </para>
/// </summary>
public static class ProductionMachineScheduleDuration
{
    /// <summary>Productive seconds contributed by one machine timing row. May be zero.</summary>
    public static decimal ComputeSeconds(
        decimal setupSeconds,
        decimal conversionSeconds,
        decimal queueSeconds,
        decimal plannedRunMinutes) =>
        setupSeconds + conversionSeconds + queueSeconds + plannedRunMinutes * 60m;

    public static decimal ComputeSeconds(ProductionWorkOrderMachine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return ComputeSeconds(
            machine.SetupSeconds,
            machine.ConversionSeconds,
            machine.QueueSeconds,
            machine.PlannedRunMinutes);
    }

    /// <summary>
    /// True when any persisted timing component is negative. Negative values are corruption, not
    /// a legacy empty interval, and must never be treated as an ignorable zero-duration row.
    /// </summary>
    public static bool HasNegativeComponent(ProductionWorkOrderMachine machine) =>
        machine.SetupSeconds < 0m
        || machine.ConversionSeconds < 0m
        || machine.QueueSeconds < 0m
        || machine.PlannedRunMinutes < 0m;

    /// <summary>
    /// Converts productive seconds to the exact duration the calendar scheduler consumes. The
    /// two-part construction is intentional: it reproduces the historical
    /// <c>FromSeconds(fixed) + FromMinutes(runMinutes)</c> arithmetic tick-for-tick.
    /// </summary>
    public static TimeSpan ToTimeSpan(decimal setupSeconds, decimal conversionSeconds, decimal queueSeconds, decimal plannedRunMinutes) =>
        TimeSpan.FromSeconds((double)(setupSeconds + conversionSeconds + queueSeconds))
        + TimeSpan.FromMinutes((double)plannedRunMinutes);

    public static TimeSpan ToTimeSpan(ProductionWorkOrderMachine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return ToTimeSpan(
            machine.SetupSeconds,
            machine.ConversionSeconds,
            machine.QueueSeconds,
            machine.PlannedRunMinutes);
    }

    /// <summary>True only for a strictly positive, non-negative-component machine duration.</summary>
    public static bool IsPositive(ProductionWorkOrderMachine machine) =>
        !HasNegativeComponent(machine) && ComputeSeconds(machine) > 0m;

    /// <summary>
    /// Product Definition activation precision rule. A selectable machine must be able to produce
    /// positive scheduled duration for a one-slot run under the scheduler's current precision,
    /// where one slot of run minutes is rounded to <see cref="IvQty.Scale"/> decimals.
    /// <para>
    /// A positive but tiny <c>CycleSeconds</c> that always rounds to zero run minutes is not a
    /// valid activation basis unless fixed seconds (setup/conversion/queue) already make the
    /// duration positive.
    /// </para>
    /// </summary>
    public static bool CanProducePositiveOneSlotDuration(
        decimal setupSeconds,
        decimal conversionSeconds,
        decimal queueSeconds,
        decimal cycleSeconds)
    {
        if (setupSeconds < 0m || conversionSeconds < 0m || queueSeconds < 0m || cycleSeconds < 0m)
            return false;

        var fixedSeconds = setupSeconds + conversionSeconds + queueSeconds;
        if (fixedSeconds > 0m)
            return true;

        return RoundRunMinutes(cycleSeconds) > 0m;
    }

    /// <summary>Run minutes for one cycle slot under the repository's current precision.</summary>
    public static decimal RoundRunMinutes(decimal cycleSeconds) =>
        decimal.Round(cycleSeconds / 60m, IvQty.Scale, MidpointRounding.AwayFromZero);
}
