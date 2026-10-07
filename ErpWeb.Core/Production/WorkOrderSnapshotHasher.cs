using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Core.Production;

/// <summary>How a snapshot timestamp's <see cref="DateTime.Kind"/> is written into the hash.</summary>
public enum TimestampHashKind
{
    /// <summary>Clock time only. Local and Unspecified values with the same ticks hash alike.</summary>
    Unspecified = 0,

    /// <summary>Write the value's current kind. Reproduces hashes saved before kind was stripped.</summary>
    AsStored = 1,

    /// <summary>Treat the value as <see cref="DateTimeKind.Local"/> before writing it.</summary>
    AsLocal = 2
}

/// <summary>
/// Which timestamps keep a local offset. A Draft saved from <see cref="DateTime.Today"/> hashed
/// the planner anchor as local and calendar rows (loaded from SQL) as unspecified. SQL reloads
/// every datetime2 as unspecified, so release recognizes those older hashes before rewriting them.
/// </summary>
public readonly record struct TimestampHashRules(TimestampHashKind Anchor, TimestampHashKind Planned, TimestampHashKind Calendar)
{
    public static TimestampHashRules Canonical { get; } = new(TimestampHashKind.Unspecified, TimestampHashKind.Unspecified, TimestampHashKind.Unspecified);

    public static TimestampHashRules AsStored { get; } = new(TimestampHashKind.AsStored, TimestampHashKind.AsStored, TimestampHashKind.AsStored);

    /// <summary>Only the planner anchor was local. Typical when the anchor is midnight, before the shift.</summary>
    public static TimestampHashRules AnchorLocal { get; } = new(TimestampHashKind.AsLocal, TimestampHashKind.Unspecified, TimestampHashKind.Unspecified);

    /// <summary>Anchor and planned start/completion were local. Calendar rows stayed unspecified.</summary>
    public static TimestampHashRules PlannedLocal { get; } = new(TimestampHashKind.AsLocal, TimestampHashKind.AsLocal, TimestampHashKind.Unspecified);

    public static TimestampHashRules AllLocal { get; } = new(TimestampHashKind.AsLocal, TimestampHashKind.AsLocal, TimestampHashKind.AsLocal);

    public static IReadOnlyList<TimestampHashRules> StoredHashRecognition { get; } =
    [
        Canonical,
        AnchorLocal,
        PlannedLocal,
        AllLocal
    ];
}

/// <summary>
/// A single canonicalized value in a snapshot/source hasher payload. Values are normalized by
/// <see cref="CanonicalHashWriter"/>, so callers pass raw domain values and never pre-format.
/// </summary>
public sealed class CanonicalHashWriter
{
    private readonly StringBuilder _sb = new();

    public void Add(int value) => Add((long)value);

    public void Add(long value) => Append(value.ToString(CultureInfo.InvariantCulture));

    public void Add(long? value) => Append(value?.ToString(CultureInfo.InvariantCulture));

    public void Add(int? value) => Append(value?.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Numbers are written with a fixed round-trip format so 1m, 1.0m and 1.0000m hash alike and
    /// no culture can change the separator.
    /// </summary>
    public void Add(decimal value) => Append(value.ToString("0.############################", CultureInfo.InvariantCulture));

    public void Add(decimal? value) => Append(value?.ToString("0.############################", CultureInfo.InvariantCulture));

    public void Add(bool value) => Append(value ? "1" : "0");

    public void Add(Guid value) => Append(value.ToString("D"));

    public void Add(Guid? value) => Append(value?.ToString("D"));

    public void Add(string? value) => Append(value);

    /// <summary>Date-only values hash on the calendar day; time-of-day is dropped intentionally.</summary>
    public void AddDate(DateTime value) => Append(value.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    public void AddDate(DateTime? value) => Append(value?.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    /// <summary>
    /// How <see cref="DateTime.Kind"/> is written. The default drops it because SQL datetime2
    /// reloads every value as <see cref="DateTimeKind.Unspecified"/>.
    /// </summary>
    public TimestampHashRules TimestampRules { get; init; } = TimestampHashRules.Canonical;

    /// <summary>
    /// Timestamps hash on clock time. Kind is omitted because a Local value saved from the
    /// screen reloads as Unspecified and must not look like a different snapshot.
    /// </summary>
    public void AddTimestamp(DateTime value) => Append(FormatTimestamp(value, TimestampHashKind.Unspecified));

    public void AddTimestamp(DateTime? value) => Append(value is null ? null : FormatTimestamp(value.Value, TimestampHashKind.Unspecified));

    public void AddAnchorTimestamp(DateTime? value) => Append(value is null ? null : FormatTimestamp(value.Value, TimestampRules.Anchor));

    public void AddPlannedTimestamp(DateTime value) => Append(FormatTimestamp(value, TimestampRules.Planned));

    public void AddPlannedTimestamp(DateTime? value) => Append(value is null ? null : FormatTimestamp(value.Value, TimestampRules.Planned));

    public void AddCalendarTimestamp(DateTime? value) => Append(value is null ? null : FormatTimestamp(value.Value, TimestampRules.Calendar));

    private static string FormatTimestamp(DateTime value, TimestampHashKind kind)
    {
        var stamp = kind switch
        {
            TimestampHashKind.AsStored => value,
            TimestampHashKind.AsLocal => DateTime.SpecifyKind(value, DateTimeKind.Local),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Unspecified)
        };
        return stamp.ToString("O", CultureInfo.InvariantCulture);
    }

    /// <summary>Starts a new ordered section; the marker cannot be produced by a value.</summary>
    public void Section(string name)
    {
        _sb.Append('\u001F').Append(name).Append('\u001F');
    }

    /// <summary>Records how many items follow, so two different groupings cannot collide.</summary>
    public void Count(int count) => Append(count.ToString(CultureInfo.InvariantCulture));

    public string ComputeHash()
    {
        var bytes = Encoding.UTF8.GetBytes(_sb.ToString());
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    /// <summary>
    /// Length-prefixing keeps the serialization unambiguous even when a value contains the
    /// separator, a colon, or a newline.
    /// </summary>
    private void Append(string? text)
    {
        var value = text ?? string.Empty;
        _sb.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
    }
}

/// <summary>
/// Canonical hashers for the Work Order snapshot and for the Product Definition revision payload
/// it was built from (plan §4.2).
/// <para>
/// Both hashers are pure functions of their input graph. They never read a clock, never read the
/// database, and never depend on collection ordering: callers hand over an unordered graph and the
/// hasher orders it by stable authored keys. That is what makes a stored hash a usable staleness
/// token instead of a coincidence.
/// </para>
/// <para>
/// Hash algorithm versions are immutable executable contracts. Dispatch by the stored
/// <c>SnapshotHashVersion</c> / <c>DefinitionSourceHashVersion</c>; never recompute a stored V1
/// hash with the V2 field set.
/// </para>
/// </summary>
public static class WorkOrderSnapshotHasher
{
    /// <summary>
    /// Hash of the complete manufacturing snapshot plus its planned schedule, using the algorithm
    /// named by <see cref="ProductionWorkOrder.SnapshotHashVersion"/>.
    /// </summary>
    public static string ComputeSnapshotHash(ProductionWorkOrder workOrder) =>
        ComputeSnapshotHash(workOrder, TimestampHashRules.Canonical);

    /// <summary>
    /// Hash using an explicit kind rule. <see cref="TimestampHashRules.AsStored"/> reproduces a
    /// hash saved before kind was stripped.
    /// </summary>
    internal static string ComputeSnapshotHash(ProductionWorkOrder workOrder, TimestampHashRules rules)
    {
        ArgumentNullException.ThrowIfNull(workOrder);

        return workOrder.SnapshotHashVersion switch
        {
            ProductionSnapshotHashVersions.V1 => ComputeSnapshotHashV1(workOrder, rules),
            ProductionSnapshotHashVersions.DefinitionIdentityV2 => ComputeSnapshotHashV2(workOrder, rules),
            ProductionSnapshotHashVersions.RouteOutputContractV3 => ComputeSnapshotHashV3(workOrder, rules),
            _ => throw new InvalidOperationException(
                $"Unsupported SnapshotHashVersion {workOrder.SnapshotHashVersion}."),
        };
    }

    /// <summary>
    /// True when the stored hash matches the canonical payload, or the same clock times under a
    /// kind pattern produced by the screen before kind was stripped.
    /// </summary>
    public static bool MatchesStoredSnapshotHash(ProductionWorkOrder workOrder)
    {
        ArgumentNullException.ThrowIfNull(workOrder);
        var stored = workOrder.SnapshotHash;
        foreach (var rules in TimestampHashRules.StoredHashRecognition)
        {
            if (string.Equals(ComputeSnapshotHash(workOrder, rules), stored, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Replaces a kind-sensitive stored hash with the canonical hash when the payload is unchanged.
    /// A tampered snapshot is left untouched.
    /// </summary>
    public static void CanonicalizeStoredTimestampKindHash(ProductionWorkOrder workOrder)
    {
        ArgumentNullException.ThrowIfNull(workOrder);
        var canonical = ComputeSnapshotHash(workOrder);
        if (string.Equals(canonical, workOrder.SnapshotHash, StringComparison.Ordinal))
        {
            return;
        }

        if (MatchesStoredSnapshotHash(workOrder))
        {
            workOrder.SnapshotHash = canonical;
        }
    }

    /// <summary>
    /// Hash-version 1 (byte-for-byte legacy algorithm). Includes
    /// <see cref="ProductionWorkOrder.DefinitionEffectiveDate"/> and
    /// <see cref="ProductionWorkOrder.SourceEffectiveFrom"/>.
    /// </summary>
    public static string ComputeSnapshotHashV1(ProductionWorkOrder workOrder, TimestampHashRules timestampRules = default)
    {
        ArgumentNullException.ThrowIfNull(workOrder);

        var w = new CanonicalHashWriter { TimestampRules = timestampRules == default ? TimestampHashRules.Canonical : timestampRules };
        WriteSnapshotHeaderCommon(w, workOrder);
        w.AddDate(workOrder.DefinitionEffectiveDate);
        w.AddDate(workOrder.SourceEffectiveFrom);
        WriteSnapshotScheduleAndBody(w, workOrder, includeComponentDefinitionCode: false, includeRouteOutputContract: false);
        return w.ComputeHash();
    }

    /// <summary>
    /// Hash-version 2 (definition identity). Uses
    /// <see cref="ProductionWorkOrder.SourceDefinitionCode"/> and
    /// <see cref="ProductionWorkOrder.SourceProductDefinitionRevisionId"/>; omits date-selection
    /// fields; includes material <see cref="ProductionWorkOrderMaterial.ComponentDefinitionCode"/>.
    /// </summary>
    public static string ComputeSnapshotHashV2(ProductionWorkOrder workOrder, TimestampHashRules timestampRules = default)
    {
        ArgumentNullException.ThrowIfNull(workOrder);

        var w = new CanonicalHashWriter { TimestampRules = timestampRules == default ? TimestampHashRules.Canonical : timestampRules };
        WriteSnapshotHeaderCommon(w, workOrder);
        w.Add(workOrder.SourceDefinitionCode);
        w.Add(workOrder.SourceProductDefinitionRevisionId);
        WriteSnapshotScheduleAndBody(w, workOrder, includeComponentDefinitionCode: true, includeRouteOutputContract: false);
        return w.ComputeHash();
    }

    /// <summary>
    /// Hash-version 3: V2 body plus route OutputType, YieldPercent, OutputBaseUom,
    /// OutputConversionFactorToBase.
    /// </summary>
    public static string ComputeSnapshotHashV3(ProductionWorkOrder workOrder, TimestampHashRules timestampRules = default)
    {
        ArgumentNullException.ThrowIfNull(workOrder);

        var w = new CanonicalHashWriter { TimestampRules = timestampRules == default ? TimestampHashRules.Canonical : timestampRules };
        WriteSnapshotHeaderCommon(w, workOrder);
        w.Add(workOrder.SourceDefinitionCode);
        w.Add(workOrder.SourceProductDefinitionRevisionId);
        WriteSnapshotScheduleAndBody(w, workOrder, includeComponentDefinitionCode: true, includeRouteOutputContract: true);
        return w.ComputeHash();
    }

    private static void WriteSnapshotHeaderCommon(CanonicalHashWriter w, ProductionWorkOrder workOrder)
    {
        w.Section("HEADER");

        // Identity that changes the meaning of the snapshot. The work order number is deliberately
        // excluded: renumbering or re-importing the same plan must not invalidate the snapshot.
        w.Add(workOrder.CompanyCode);
        w.Add(workOrder.BranchCode);
        w.Add(workOrder.ProductCode);
        w.Add(workOrder.OutputUom);
        w.Add(workOrder.SourceBomHdrId);
        w.Add(workOrder.SourceBomVersion);
        w.Add(workOrder.BomBaseQty);
        w.Add(workOrder.BomBaseUom);
        w.Add(workOrder.PlannedQty);
    }

    private static void WriteSnapshotScheduleAndBody(
        CanonicalHashWriter w,
        ProductionWorkOrder workOrder,
        bool includeComponentDefinitionCode,
        bool includeRouteOutputContract)
    {
        w.AddAnchorTimestamp(workOrder.ScheduleAnchorDateTime);
        w.AddPlannedTimestamp(workOrder.PlannedStartDateTime);
        w.AddPlannedTimestamp(workOrder.PlannedCompletionDateTime);
        w.Add(workOrder.SchedulingDirection);
        w.Add(workOrder.SourceType);
        w.Add(workOrder.SourceReference);
        w.Add(workOrder.Remark);

        w.Section("ROUTESTEPS");
        var routeSteps = workOrder.RouteSteps
            .OrderBy(x => x.StageSequence)
            .ThenBy(x => x.SourceRouteStepKey)
            .ToList();
        w.Count(routeSteps.Count);

        foreach (var step in routeSteps)
        {
            w.Add(step.StageSequence);
            w.Add(step.SourceRouteStepId);
            w.Add(step.SourceRouteStepKey);
            w.Add(step.WorkCentreCode);
            w.Add(step.WorkCentreDescription);
            w.Add(step.OutputItemCode);
            w.Add(step.OutputItemDescription);
            w.Add(step.OutputBaseQty);
            w.Add(step.PlannedQty);
            w.Add(step.OutputUom);
            if (includeRouteOutputContract)
            {
                w.Add(step.OutputType);
                w.Add(step.YieldPercent);
                w.Add(step.OutputBaseUom);
                w.Add(step.OutputConversionFactorToBase);
            }
            w.AddPlannedTimestamp(step.PlannedStartDateTime);
            w.AddPlannedTimestamp(step.PlannedCompletionDateTime);

            var operations = step.Operations
                .OrderBy(x => x.ProcessSequence)
                .ThenBy(x => x.SourceOperationKey)
                .ToList();
            w.Count(operations.Count);

            foreach (var operation in operations)
            {
                AddOperation(w, operation);
            }
        }

        w.Section("MATERIALS");
        var materials = workOrder.Materials
            .OrderBy(x => x.MaterialSequence)
            .ThenBy(x => x.ComponentCode, StringComparer.Ordinal)
            .ToList();
        w.Count(materials.Count);

        foreach (var material in materials)
        {
            w.Add(material.MaterialSequence);
            w.Add(material.LineNo);
            w.Add(material.SourceBomHdrId);
            w.Add(material.SourceBomVersion);
            w.Add(material.SourceBomLineId);
            w.Add(material.SourceMaterialKey);
            w.Add(material.AlternateGroupCode);
            w.Add(material.ParentProductCode);
            w.Add(material.BomPath);
            w.Add(material.ComponentCode);
            w.Add(material.ComponentDescription);
            w.Add(material.MfgType);
            w.Add(material.ComponentQtyPerParent);
            w.Add(material.StandardUom);
            w.Add(material.BomOutputQty);
            w.Add(material.BomOutputUom);
            w.Add(material.ScrapPercent);
            w.Add(material.Tolerance);
            w.Add(material.IssueMethod);
            w.Add(material.SupplySource);
            if (includeComponentDefinitionCode)
            {
                w.Add(material.ComponentDefinitionCode);
            }

            w.Add(material.RequiredQty);
            w.Add(material.RequiredUom);
            w.Add(material.RequiredBaseQty);
            w.Add(material.BaseUom);
            w.Add(material.ConversionFactorToBase);
            w.Add(material.WarehouseCode);
            w.Add(material.LocationCode);

            // Only definition-derived references are hashed. ProWorkOrderMaterial.ProducingRouteStepID
            // and WorkOrderOperationID are database-generated surrogates: a freshly built snapshot
            // does not have them yet and a reloaded one does, so including them would make the hash
            // change on save and turn the stored token into a false staleness alarm. The link is
            // expressed through the referenced row's own stable identity instead, which is equally
            // discriminating.
            w.Add(material.ProducingRouteStep?.SourceRouteStepId);
            w.Add(material.ProducingRouteStep?.SourceRouteStepKey);
            w.Add(material.ProducingRouteStep?.StageSequence);
            w.Add(material.ProducingRouteStep?.OutputItemCode);
            w.Add(material.WorkOrderOperation?.SourceOperationId);
            w.Add(material.WorkOrderOperation?.SourceOperationKey);
            w.Add(material.WorkOrderOperation?.ProcessSequence);
        }
    }

    private static void AddOperation(CanonicalHashWriter w, ProductionWorkOrderOperation operation)
    {
        w.Add(operation.SourceOperationId);
        w.Add(operation.SourceOperationKey);
        w.Add(operation.ProcessSequence);
        w.Add(operation.ProcessType);
        w.Add(operation.OperationCode);
        w.Add(operation.WorkCentreCode);
        w.Add(operation.IsFinalOperation);
        w.Add(operation.StandardDurationMinutes);
        w.Add(operation.PlannedInputQty);
        w.Add(operation.PlannedInputUom);
        w.Add(operation.PlannedOutputQty);
        w.Add(operation.PlannedOutputUom);
        w.Add(operation.CalendarSourceType);
        w.Add(operation.CalendarSourceId);
        w.AddCalendarTimestamp(operation.CalendarSourceLastModified);
        w.Add(operation.ScheduleSourceHash);
        w.AddCalendarTimestamp(operation.CalendarHorizonStart);
        w.AddCalendarTimestamp(operation.CalendarHorizonEnd);
        w.AddPlannedTimestamp(operation.PlannedStartDateTime);
        w.AddPlannedTimestamp(operation.PlannedCompletionDateTime);

        var machines = operation.Machines
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.MachineCode, StringComparer.Ordinal)
            .ToList();
        w.Count(machines.Count);

        foreach (var machine in machines)
        {
            // IsSelected/IsDefault are authored selection state; the derived cycle numbers below
            // are computed outputs. Including both means a quantity change and a machine change
            // are both detected without either one masking the other.
            w.Add(machine.SourceMachineOptionId);
            w.Add(machine.SourceMachineKey);
            w.Add(machine.MachineCode);
            w.Add(machine.MachineDescription);
            w.Add(machine.Priority);
            w.Add(machine.IsDefault);
            w.Add(machine.IsSelected);
            w.Add(machine.ParallelMachineCount);
            w.Add(machine.CycleQuantityMode);
            w.Add(machine.CycleSeconds);
            w.Add(machine.OutputPerCycle);
            w.Add(machine.OutputPerCycleUom);
            w.Add(machine.ConversionSeconds);
            w.Add(machine.SetupSeconds);
            w.Add(machine.QueueSeconds);
            w.Add(machine.RequiredMachineOutputQty);
            w.Add(machine.RequiredMachineOutputUom);
            w.Add(machine.PlannedCycleCount);
            w.Add(machine.PlannedCycleSlots);
            w.Add(machine.PlannedRunMinutes);
            w.AddPlannedTimestamp(machine.PlannedStartDateTime);
            w.AddPlannedTimestamp(machine.PlannedCompletionDateTime);
            w.Add(machine.MachineRatePerHour);
            w.Add(machine.CalendarSourceId);
            w.AddCalendarTimestamp(machine.CalendarSourceLastModified);
            w.Add(machine.ScheduleSourceHash);
            w.AddCalendarTimestamp(machine.CalendarHorizonStart);
            w.AddCalendarTimestamp(machine.CalendarHorizonEnd);

            var machineLabours = machine.Labours
                .OrderBy(x => x.LabourCode, StringComparer.Ordinal)
                .ToList();
            w.Count(machineLabours.Count);
            foreach (var labour in machineLabours)
            {
                AddLabour(w, labour);
            }
        }

        // The owning collection is the discriminator, never MachineID/OperationID: those are
        // database-generated surrogates that are still null on a freshly built graph but populated
        // after a reload, so filtering on them would make the hash depend on whether the aggregate
        // had been saved yet. Machine-owned labour is hashed under its machine and operation-level
        // labour here, matching the two disjoint collections the model exposes.
        var operationLabours = operation.Labours
            .OrderBy(x => x.LabourCode, StringComparer.Ordinal)
            .ToList();
        w.Count(operationLabours.Count);
        foreach (var labour in operationLabours)
        {
            AddLabour(w, labour);
        }
    }

    private static void AddLabour(CanonicalHashWriter w, ProductionWorkOrderLabour labour)
    {
        w.Add(labour.SourceLabourId);
        w.Add(labour.SourceLabourKey);
        w.Add(labour.LabourCode);
        w.Add(labour.LabourDescription);
        w.Add(labour.PlannedUnits);
        w.Add(labour.PlannedMinutes);
        w.Add(labour.RateBasis);
        w.Add(labour.Rate);
        w.Add(labour.ContributesToPlan);
        w.Add(labour.PlannedAmount);
    }

    /// <summary>
    /// Hash of the exact Product Definition payload used to build a snapshot, using
    /// <see cref="ProductionDefinitionSourceHashVersions.Current"/>.
    /// </summary>
    public static string ComputeDefinitionSourceHash(PrBomHdr revision) =>
        ComputeDefinitionSourceHash(revision, ProductionDefinitionSourceHashVersions.Current);

    /// <summary>
    /// Hash of the exact Product Definition payload used to build a snapshot, dispatched by
    /// <paramref name="version"/>.
    /// <para>
    /// Deliberately excludes volatile audit metadata (created/modified stamps, <c>RowVersion</c>,
    /// descriptions that cannot reach the snapshot, validation bookkeeping, and
    /// <c>IsDefaultDefinition</c>). Including those would make Refresh report a "changed
    /// definition" every time someone re-saved an untouched revision or flipped the default flag.
    /// </para>
    /// </summary>
    public static string ComputeDefinitionSourceHash(PrBomHdr revision, int version)
    {
        ArgumentNullException.ThrowIfNull(revision);

        return version switch
        {
            ProductionDefinitionSourceHashVersions.V1 => ComputeDefinitionSourceHashV1(revision),
            ProductionDefinitionSourceHashVersions.DefinitionIdentityV2 => ComputeDefinitionSourceHashV2(revision),
            _ => throw new InvalidOperationException(
                $"Unsupported DefinitionSourceHashVersion {version}."),
        };
    }

    /// <summary>Source-hash V1: includes EffectiveFrom/To (legacy date-window selection).</summary>
    public static string ComputeDefinitionSourceHashV1(PrBomHdr revision)
    {
        ArgumentNullException.ThrowIfNull(revision);

        var w = new CanonicalHashWriter();
        w.Section("REVISION");

        // Revision identity and effective bounds: the same content under a different revision is a
        // different source, because the work order records which revision it came from.
        w.Add(revision.CompanyCode);
        w.Add(revision.ProdCode);
        w.Add(revision.Version);
        w.Add(revision.Status);
        w.AddDate(revision.EffectiveFrom);
        w.AddDate(revision.EffectiveTo);
        WriteDefinitionSourceBody(w, revision, includeComponentDefinitionCode: false);
        return w.ComputeHash();
    }

    /// <summary>
    /// Source-hash V2: uses <see cref="PrBomHdr.DefinitionCode"/>; omits EffectiveFrom/To and
    /// never includes <see cref="PrBomHdr.IsDefaultDefinition"/>.
    /// </summary>
    public static string ComputeDefinitionSourceHashV2(PrBomHdr revision)
    {
        ArgumentNullException.ThrowIfNull(revision);

        var w = new CanonicalHashWriter();
        w.Section("REVISION");

        w.Add(revision.CompanyCode);
        w.Add(revision.ProdCode);
        w.Add(revision.DefinitionCode);
        w.Add(revision.Version);
        w.Add(revision.Status);
        WriteDefinitionSourceBody(w, revision, includeComponentDefinitionCode: true);
        return w.ComputeHash();
    }

    private static void WriteDefinitionSourceBody(
        CanonicalHashWriter w,
        PrBomHdr revision,
        bool includeComponentDefinitionCode)
    {
        w.Add(revision.BaseQty);
        w.Add(revision.BaseUom);
        w.Add(revision.Prefix);

        w.Section("ROUTESTEPS");
        var routeSteps = revision.RouteSteps
            .OrderBy(x => x.StageSequence)
            .ThenBy(x => x.WorkCentreCode, StringComparer.Ordinal)
            .ThenBy(x => x.OutputItemCode, StringComparer.Ordinal)
            .ToList();
        w.Count(routeSteps.Count);

        foreach (var step in routeSteps)
        {
            w.Add(step.RouteStepKey);
            w.Add(step.StageSequence);
            w.Add(step.WorkCentreCode);
            w.Add(step.OutputItemCode);
            w.Add(step.OutputType);
            w.Add(step.StandardOutputQty);
            w.Add(step.OutputUom);
            w.Add(step.YieldPercent);

            // Operations are read from the route step's own collection: the revision loader
            // populates it, and reading one authoritative path keeps the hash stable no matter
            // whether the caller's graph was materialized with keys or from an unsaved draft.
            var operations = step.Operations
                .OrderBy(x => x.ProcessSequence)
                .ThenBy(x => x.OperationCode, StringComparer.Ordinal)
                .ToList();
            w.Count(operations.Count);

            foreach (var operation in operations)
            {
                AddDefinitionOperation(w, operation, includeComponentDefinitionCode);
            }
        }

        // Operations that belong to no route step still reach the snapshot, so they still
        // invalidate the definition hash.
        w.Section("UNOWNEDOPERATIONS");
        var unownedOperations = revision.Operations
            .Where(x => x.RouteStepId is null && x.RouteStep is null)
            .OrderBy(x => x.ProcessSequence)
            .ThenBy(x => x.OperationCode, StringComparer.Ordinal)
            .ToList();
        w.Count(unownedOperations.Count);
        foreach (var operation in unownedOperations)
        {
            AddDefinitionOperation(w, operation, includeComponentDefinitionCode);
        }

        // Lines that are not owned by an operation still reach the snapshot, so they must still
        // invalidate the definition hash.
        w.Section("UNOWNEDMATERIALS");
        var unowned = revision.Lines
            .Where(x => x.OperationId is null)
            .OrderBy(x => x.SeqNo)
            .ThenBy(x => x.ICode, StringComparer.Ordinal)
            .ToList();
        w.Count(unowned.Count);
        foreach (var material in unowned)
        {
            AddDefinitionMaterial(w, material, includeComponentDefinitionCode);
        }
    }

    private static void AddDefinitionOperation(
        CanonicalHashWriter w,
        PrBomOperation operation,
        bool includeComponentDefinitionCode)
    {
        w.Add(operation.OperationKey);
        w.Add(operation.OperationCode);
        w.Add(operation.ProcessSequence);
        w.Add(operation.ProcessType);
        w.Add(operation.StandardDurationMinutes);
        w.Add(operation.IsFinalOperation);
        w.Add(operation.WorkCentreCode);
        w.Add(operation.OutputItemCode);
        w.Add(operation.OutputBaseQty);
        w.Add(operation.OutputUom);
        w.Add(operation.SetupLossQty);
        w.Add(operation.OperationLossQty);

        var machines = operation.Machines
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.MachineCode, StringComparer.Ordinal)
            .ToList();
        w.Count(machines.Count);

        foreach (var machine in machines)
        {
            w.Add(machine.MachineCode);
            w.Add(machine.Priority);
            w.Add(machine.IsPrimary);
            w.Add(machine.ParallelMachineCount);
            w.Add(machine.CycleSeconds);
            w.Add(machine.OutputPerCycle);
            w.Add(machine.ConversionSeconds);
            w.Add(machine.SetupSeconds);
            w.Add(machine.QueueSeconds);
            w.Add(machine.MachineRatePerHour);

            var machineLabours = machine.Labours
                .OrderBy(x => x.LabourCode, StringComparer.Ordinal)
                .ToList();
            w.Count(machineLabours.Count);
            foreach (var labour in machineLabours)
            {
                w.Add(labour.LabourCode);
                w.Add(labour.CostPerOutputUnit);
            }
        }

        var labourRequirements = operation.LabourRequirements
            .OrderBy(x => x.LabourCode, StringComparer.Ordinal)
            .ThenBy(x => x.MachineOptionId)
            .ToList();
        w.Count(labourRequirements.Count);
        foreach (var requirement in labourRequirements)
        {
            w.Add(requirement.LabourCode);
            w.Add(requirement.RequiredHeadcount);
            w.Add(requirement.SetupMinutes);
            w.Add(requirement.RunMinutes);
            w.Add(requirement.CostRate);
            w.Add(requirement.CostBasis);
            w.Add(requirement.MachineOptionId);
        }

        var materials = operation.Materials
            .OrderBy(x => x.SeqNo)
            .ThenBy(x => x.ICode, StringComparer.Ordinal)
            .ToList();
        w.Count(materials.Count);
        foreach (var material in materials)
        {
            AddDefinitionMaterial(w, material, includeComponentDefinitionCode);
        }
    }

    private static void AddDefinitionMaterial(
        CanonicalHashWriter w,
        PrDefBOM material,
        bool includeComponentDefinitionCode)
    {
        w.Add(material.ICode);
        w.Add(material.SeqNo);
        w.Add(material.StdQty);
        w.Add(material.StdUom);
        w.Add(material.ScrapPercent);
        w.Add(material.Tolerance);
        w.Add(material.IssueMethod);
        w.Add(material.SupplySource);
        w.Add(material.Warehouse);
        w.Add(material.BomDefault);
        w.Add(material.AlternateGroupCode);
        w.Add(material.ProducingRouteStepId);
        if (includeComponentDefinitionCode)
        {
            w.Add(material.ComponentDefinitionCode);
        }
    }
}
