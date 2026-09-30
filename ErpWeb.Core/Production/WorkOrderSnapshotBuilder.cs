using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

/// <summary>Inputs for building a version-2 Work Order snapshot from a Product Definition revision.</summary>
public sealed class WorkOrderSnapshotRequest
{
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string? LocationCode { get; set; }
    public string? WorkOrderNo { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public decimal PlannedQty { get; set; } = 1m;

    /// <summary>
    /// As-of date used to resolve the source revision. Persisted as the snapshot's definition
    /// effective date so a later Refresh re-resolves against the same date.
    /// </summary>
    public DateTime DefinitionEffectiveDate { get; set; } = DateTime.UtcNow.Date;

    public DateTime PlannedStartDateTime { get; set; }
    public DateTime PlannedCompletionDateTime { get; set; }
    public string SchedulingDirection { get; set; } = ProductionSchedulingDirections.Forward;
    public string SourceType { get; set; } = ProductionSourceTypes.Manual;
    public string? SourceReference { get; set; }
    public string? Remark { get; set; }

    /// <summary>Snapshot revision number to stamp. Refresh increments it.</summary>
    public int SnapshotRevision { get; set; } = 1;

    /// <summary>Product description override; falls back to the item master.</summary>
    public string? ProductDescription { get; set; }
}

public sealed class WorkOrderSnapshotBuildResult
{
    public ProductionWorkOrder? WorkOrder { get; init; }
    public long? SourceProductDefinitionRevisionId { get; init; }
    public int? SourceBomVersion { get; init; }
    public DateTime? SourceEffectiveFrom { get; init; }
    public string? DefinitionSourceHash { get; init; }
    public int DefinitionSourceHashVersion { get; init; } = ProductionDefinitionSourceHashVersions.Current;
    public IReadOnlyList<WorkOrderCalculationError> Errors { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public bool Succeeded => WorkOrder is not null;

    public string FailureCode => Errors.Count > 0 ? Errors[0].Code : string.Empty;
    public string FailureMessage => Errors.Count > 0 ? Errors[0].Message : string.Empty;

    internal static WorkOrderSnapshotBuildResult Fail(string code, string message) => new()
    {
        Errors = [new WorkOrderCalculationError(code, message)],
    };

    internal static WorkOrderSnapshotBuildResult Fail(IReadOnlyList<WorkOrderCalculationError> errors) => new()
    {
        Errors = errors,
    };
}

/// <summary>
/// Builds the complete version-2 Work Order snapshot graph from a resolved Product Definition
/// revision (plan §7.1–§7.4).
/// <para>
/// The builder is deliberately faithful rather than clever: it copies the authored definition
/// shape, resolves the cross-references that cannot survive a copy (route-step identity, machine
/// option identity, labour owner), and then delegates all arithmetic to
/// <see cref="IWorkOrderQuantityCalculator"/>. It never resolves a revision itself and never
/// guesses at a missing value — anything the definition does not say is left missing so the
/// Release readiness gate can refuse it (plan §9).
/// </para>
/// </summary>
public interface IWorkOrderSnapshotBuilder
{
    /// <summary>Resolves the source revision and builds the snapshot graph for it.</summary>
    Task<WorkOrderSnapshotBuildResult> BuildAsync(
        WorkOrderSnapshotRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds the snapshot graph from an already-resolved revision. Used by Refresh, which must
    /// compare the graph against the persisted snapshot without re-resolving silently.
    /// </summary>
    Task<WorkOrderSnapshotBuildResult> BuildFromRevisionAsync(
        WorkOrderSnapshotRequest request,
        PrBomHdr revision,
        IReadOnlyDictionary<string, IvStockMaster>? items = null,
        CancellationToken cancellationToken = default);
}

public sealed class WorkOrderSnapshotBuilder : IWorkOrderSnapshotBuilder
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IProductDefinitionSnapshotLoader _loader;
    private readonly IWorkOrderQuantityCalculator _calculator;

    public WorkOrderSnapshotBuilder(
        IDbContextFactory<AppDbContext> dbFactory,
        IProductDefinitionSnapshotLoader loader,
        IWorkOrderQuantityCalculator calculator)
    {
        _dbFactory = dbFactory;
        _loader = loader;
        _calculator = calculator;
    }

    public async Task<WorkOrderSnapshotBuildResult> BuildAsync(
        WorkOrderSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.PlannedQty <= 0m)
        {
            return WorkOrderSnapshotBuildResult.Fail(
                ProductionReadinessErrorCodes.WorkOrderQtyInvalid,
                "Work Order planned quantity must be positive.");
        }

        var resolved = await _loader.ResolveRevisionAsync(
            request.CompanyCode,
            request.ProductCode,
            request.DefinitionEffectiveDate,
            cancellationToken);

        if (!resolved.Succeeded)
        {
            return WorkOrderSnapshotBuildResult.Fail(resolved.FailureCode!, resolved.FailureMessage!);
        }

        var revision = resolved.Revision!;

        var itemCodes = revision.Lines.Select(l => l.ICode)
            .Append(revision.ProdCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var items = await LoadItemsAsync(request.CompanyCode, itemCodes, cancellationToken);

        return await BuildFromRevisionAsync(request, revision, items, cancellationToken);
    }

    public async Task<WorkOrderSnapshotBuildResult> BuildFromRevisionAsync(
        WorkOrderSnapshotRequest request,
        PrBomHdr revision,
        IReadOnlyDictionary<string, IvStockMaster>? items = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(revision);

        var masters = items ?? new Dictionary<string, IvStockMaster>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<WorkOrderCalculationError>();
        var warnings = new List<string>();

        var workOrder = BuildHeader(request, revision, masters);

        // Route steps are built first so material producers can be resolved to the occurrence they
        // point at. The source definition references a route step by its own ID; the snapshot has a
        // brand-new ID, so the reference must be re-pointed deliberately (plan §6.3).
        var stepsBySourceId = new Dictionary<long, ProductionWorkOrderRouteStep>();
        var stepsByKey = new Dictionary<Guid, ProductionWorkOrderRouteStep>();

        foreach (var sourceStep in revision.RouteSteps
                     .OrderBy(s => s.StageSequence)
                     .ThenBy(s => s.WorkCentreCode, StringComparer.Ordinal)
                     .ThenBy(s => s.OutputItemCode, StringComparer.Ordinal))
        {
            var step = BuildRouteStep(sourceStep);
            workOrder.RouteSteps.Add(step);
            stepsByKey[sourceStep.RouteStepKey] = step;
            if (sourceStep.Uid != 0)
            {
                stepsBySourceId[sourceStep.Uid] = step;
            }
        }

        var builtOperations = new Dictionary<PrBomOperation, ProductionWorkOrderOperation>();
        var builtOperationsBySourceId = new Dictionary<long, ProductionWorkOrderOperation>();
        var operationSequence = 0;

        foreach (var sourceStep in revision.RouteSteps
                     .OrderBy(s => s.StageSequence)
                     .ThenBy(s => s.WorkCentreCode, StringComparer.Ordinal)
                     .ThenBy(s => s.OutputItemCode, StringComparer.Ordinal))
        {
            if (!stepsByKey.TryGetValue(sourceStep.RouteStepKey, out var step))
            {
                continue;
            }

            foreach (var sourceOperation in sourceStep.Operations
                         .OrderBy(o => o.ProcessSequence)
                         .ThenBy(o => o.OperationCode, StringComparer.Ordinal))
            {
                var operation = BuildOperation(sourceOperation, ++operationSequence);
                step.Operations.Add(operation);

                // WorkOrderID on a descendant row is a query pointer, not the owning relationship.
                // Populating both collections is what lets EF assign the pointer when the graph is
                // saved in a single transaction.
                workOrder.Operations.Add(operation);
                builtOperations[sourceOperation] = operation;
                if (sourceOperation.Uid != 0)
                {
                    builtOperationsBySourceId[sourceOperation.Uid] = operation;
                }

                BuildMachines(operation, sourceOperation, warnings);
                BuildLabour(operation, sourceOperation, warnings);
            }
        }

        // Every definition line becomes exactly one material row. The consuming operation is
        // resolved through a single rule, so a line can never be snapshotted twice or silently
        // dropped, and it does not matter whether the caller's revision graph was materialized
        // through navigations or through persisted foreign keys.
        foreach (var sourceMaterial in revision.Lines
                     .OrderBy(m => m.SeqNo)
                     .ThenBy(m => m.ICode, StringComparer.Ordinal))
        {
            var operation = ResolveBuiltOperation(
                revision, sourceMaterial, builtOperations, builtOperationsBySourceId);

            var material = BuildMaterial(revision, operation, sourceMaterial, masters, stepsBySourceId, errors);
            if (operation is not null)
            {
                // Wire the inverse navigation before hashing. Collection.Add alone does not fix up
                // POCOs that are not yet tracked, and WorkOrderSnapshotHasher reads
                // material.WorkOrderOperation — leaving it null here makes the stored hash diverge
                // from a SQL Server reload where Include populates the navigation.
                material.WorkOrderOperation = operation;
                operation.Materials.Add(material);
            }

            // A line the definition did not assign to an operation is still a real requirement. It is
            // snapshotted at header level with no consuming operation so the readiness gate can refuse
            // the release with WO_MATERIAL_OPERATION_MISSING instead of the requirement being dropped.
            workOrder.Materials.Add(material);
        }

        if (revision.RouteSteps.Count == 0)
        {
            errors.Add(new WorkOrderCalculationError(
                ProductionReadinessErrorCodes.NoRoute,
                "The Product Definition revision has no route steps, so a Work Order snapshot cannot be built."));
        }

        if (errors.Count > 0)
        {
            return new WorkOrderSnapshotBuildResult
            {
                Errors = errors,
                Warnings = warnings,
                SourceProductDefinitionRevisionId = revision.Uid,
                SourceBomVersion = revision.Version,
                SourceEffectiveFrom = revision.EffectiveFrom,
            };
        }

        return await FinalizeAsync(workOrder, request, revision, warnings, cancellationToken);
    }

    private async Task<WorkOrderSnapshotBuildResult> FinalizeAsync(
        ProductionWorkOrder workOrder,
        WorkOrderSnapshotRequest request,
        PrBomHdr revision,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        // Derived quantities are computed once, against the fully assembled graph, and written
        // onto the rows. Nothing downstream recalculates them opportunistically (plan §7.3).
        var calculation = await _calculator.CalculateAsync(workOrder, cancellationToken);
        if (!calculation.Succeeded)
        {
            return new WorkOrderSnapshotBuildResult
            {
                Errors = [.. calculation.Errors],
                Warnings = warnings,
                SourceProductDefinitionRevisionId = revision.Uid,
                SourceBomVersion = revision.Version,
                SourceEffectiveFrom = revision.EffectiveFrom,
            };
        }

        var definitionSourceHash = WorkOrderSnapshotHasher.ComputeDefinitionSourceHash(revision);

        workOrder.DefinitionSourceHash = definitionSourceHash;
        workOrder.DefinitionSourceHashVersion = ProductionDefinitionSourceHashVersions.Current;

        // The snapshot hash is written last because it covers the planned schedule and every
        // derived quantity produced above.
        workOrder.SnapshotHash = WorkOrderSnapshotHasher.ComputeSnapshotHash(workOrder);
        workOrder.SnapshotHashVersion = ProductionSnapshotHashVersions.Current;
        workOrder.SnapshotFormatVersion = ProductionSnapshotFormatVersions.Current;
        workOrder.IsLegacySnapshot = false;
        workOrder.LegacySnapshotReason = null;
        workOrder.SnapshotRevision = request.SnapshotRevision;

        return new WorkOrderSnapshotBuildResult
        {
            WorkOrder = workOrder,
            SourceProductDefinitionRevisionId = revision.Uid,
            SourceBomVersion = revision.Version,
            SourceEffectiveFrom = revision.EffectiveFrom,
            DefinitionSourceHash = definitionSourceHash,
            Warnings = warnings,
        };
    }

    private static ProductionWorkOrder BuildHeader(
        WorkOrderSnapshotRequest request,
        PrBomHdr revision,
        IReadOnlyDictionary<string, IvStockMaster> items)
    {
        var productCode = NormalizeCode(revision.ProdCode);
        items.TryGetValue(productCode, out var productItem);

        return new ProductionWorkOrder
        {
            CompanyCode = NormalizeCode(request.CompanyCode),
            BranchCode = NormalizeCode(request.BranchCode),
            LocationCode = request.LocationCode,
            WorkOrderNo = request.WorkOrderNo ?? string.Empty,
            SnapshotRevision = request.SnapshotRevision,
            DefinitionEffectiveDate = request.DefinitionEffectiveDate.Date,

            ProductCode = productCode,
            ProductDescription = request.ProductDescription ?? productItem?.IDesc,
            OutputUom = Normalize(revision.BaseUom) ?? Normalize(productItem?.StdUom),

            SourceBomHdrId = revision.Uid,
            SourceBomVersion = revision.Version,
            BomBaseQty = revision.BaseQty,
            BomBaseUom = Normalize(revision.BaseUom),

            PlannedQty = request.PlannedQty,
            RemainingQty = request.PlannedQty,

            SourceEffectiveFrom = revision.EffectiveFrom,
            SourceProductDefinitionRevisionId = revision.Uid,

            PlannedStartDateTime = request.PlannedStartDateTime,
            PlannedCompletionDateTime = request.PlannedCompletionDateTime,
            SchedulingDirection = request.SchedulingDirection,
            ScheduleAnchorDateTime = request.SchedulingDirection == ProductionSchedulingDirections.Backward
                ? request.PlannedCompletionDateTime
                : request.PlannedStartDateTime,

            Status = ProductionWorkOrderStatuses.Draft,
            SourceType = request.SourceType,
            SourceReference = request.SourceReference,
            Remark = request.Remark,

            // A brand-new snapshot is incomplete until it is scheduled and released.
            SnapshotFormatVersion = ProductionSnapshotFormatVersions.Current,
            IsLegacySnapshot = false,
        };
    }

    private static ProductionWorkOrderRouteStep BuildRouteStep(PrBomRouteStep source) => new()
    {
        SourceRouteStepId = source.Uid == 0 ? null : source.Uid,
        SourceRouteStepKey = source.RouteStepKey,
        StageSequence = source.StageSequence,
        WorkCentreCode = NormalizeCode(source.WorkCentreCode),
        OutputItemCode = NormalizeCode(source.OutputItemCode),
        OutputBaseQty = source.StandardOutputQty,
        OutputUom = Normalize(source.OutputUom),
    };

    private static ProductionWorkOrderOperation BuildOperation(PrBomOperation source, int sequenceNo)
    {
        var processType = Normalize(source.ProcessType) ?? PrProcessTypes.Manual;
        var outputUom = Normalize(source.OutputUom);

        return new ProductionWorkOrderOperation
        {
            SourceOperationId = source.Uid == 0 ? null : source.Uid,
            SourceOperationKey = source.OperationKey,
            // Flat list order for API / legacy readers. Distinct from ProcessSequence, which is the
            // authored order inside a route step (and may collide across steps).
            SequenceNo = sequenceNo,
            ProcessSequence = source.ProcessSequence,
            ProcessType = processType,
            OperationCode = NormalizeCode(source.OperationCode),
            WorkCentreCode = Normalize(source.WorkCentreCode),
            IsFinalOperation = source.IsFinalOperation,
            StandardDurationMinutes = source.StandardDurationMinutes,
            SetupLossQty = source.SetupLossQty,
            OperationLossQty = source.OperationLossQty,

            // The operation consumes and produces the full owning route-step quantity in the
            // definition's own UOMs; the calculator converts it into the route-step quantity.
            PlannedInputUom = outputUom,
            PlannedOutputUom = outputUom,

            // A duration-based process never depends on a machine calendar; a machine-capable
            // process does as soon as a machine option is selected (plan §6.9).
            CalendarSourceType = ProductionCalendarSourceTypes.PlantDefault,
        };
    }

    private static void BuildMachines(
        ProductionWorkOrderOperation operation,
        PrBomOperation source,
        List<string> warnings)
    {
        if (!PrProcessTypes.SupportsMachine(operation.ProcessType))
        {
            if (source.Machines.Count > 0)
            {
                warnings.Add(
                    $"Operation {operation.OperationCode} ({operation.ProcessType}) carries machine options "
                    + "that were not snapshotted because a fixed-duration process must not depend on a machine.");
            }

            return;
        }

        // All authored alternatives are snapshotted for traceability; exactly one is marked as the
        // option this snapshot schedules. Selection is deterministic: the authored default wins,
        // then the lowest priority, then the machine code.
        var selected = source.Machines
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => m.Priority)
            .ThenBy(m => m.MachineCode, StringComparer.Ordinal)
            .FirstOrDefault();

        foreach (var option in source.Machines
                     .OrderBy(m => m.Priority)
                     .ThenBy(m => m.MachineCode, StringComparer.Ordinal))
        {
            var isSelected = ReferenceEquals(option, selected);
            var machine = new ProductionWorkOrderMachine
            {
                SourceMachineOptionId = option.Uid == 0 ? null : option.Uid,
                MachineCode = NormalizeCode(option.MachineCode),
                MachineDescription = option.MachineDescription,
                Priority = option.Priority,
                IsDefault = option.IsPrimary,
                IsSelected = isSelected,
                ParallelMachineCount = option.ParallelMachineCount < 1 ? 1 : option.ParallelMachineCount,
                CycleQuantityMode = ProductionMachineCycleQuantityModes.Discrete,
                CycleSeconds = option.CycleSeconds,
                OutputPerCycle = option.OutputPerCycle,
                OutputPerCycleUom = operation.PlannedOutputUom,
                ConversionSeconds = option.ConversionSeconds,
                SetupSeconds = option.SetupSeconds,
                QueueSeconds = option.QueueSeconds,
                MachineRatePerHour = option.MachineRatePerHour,
            };

            if (isSelected)
            {
                operation.CalendarSourceType = ProductionCalendarSourceTypes.Machine;
            }

            operation.Machines.Add(machine);
        }

        if (selected is null)
        {
            // Nothing was authored: leave the operation without a machine so Release refuses it.
            operation.CalendarSourceType = ProductionCalendarSourceTypes.PlantDefault;
        }
    }

    private static void BuildLabour(
        ProductionWorkOrderOperation operation,
        PrBomOperation source,
        List<string> warnings)
    {
        // Legacy machine-owned standards: cost per output unit, contributing only when the machine
        // option they hang from is the selected one (plan §6.4).
        foreach (var machine in operation.Machines)
        {
            var machineOption = source.Machines
                .FirstOrDefault(m => (m.Uid != 0 && m.Uid == machine.SourceMachineOptionId)
                                     || string.Equals(NormalizeCode(m.MachineCode), machine.MachineCode, StringComparison.Ordinal));
            if (machineOption is null)
            {
                continue;
            }

            foreach (var standard in machineOption.Labours.OrderBy(l => l.LabourCode, StringComparer.Ordinal))
            {
                machine.Labours.Add(new ProductionWorkOrderLabour
                {
                    SourceLabourId = standard.Uid == 0 ? null : standard.Uid,
                    LabourCode = NormalizeCode(standard.LabourCode),
                    LabourDescription = standard.LabourDescription,
                    RateBasis = ProductionLabourRateBases.PerOutputUnit,
                    Rate = standard.CostPerOutputUnit,
                    ContributesToPlan = machine.IsSelected,
                });
            }
        }

        foreach (var requirement in source.LabourRequirements.OrderBy(l => l.LabourCode, StringComparer.Ordinal))
        {
            var costBasis = Normalize(requirement.CostBasis);
            var rateBasis = costBasis is null ? PrLabourCostBases.PerHour : costBasis;
            var supported = string.Equals(rateBasis, PrLabourCostBases.PerOutputUnit, StringComparison.Ordinal);

            if (!supported)
            {
                warnings.Add(
                    $"Labour {requirement.LabourCode} on operation {operation.OperationCode} is costed "
                    + $"{rateBasis}, which the snapshot cannot express; the row is kept for traceability "
                    + "but does not contribute to the plan and will block release.");
            }

            var owner = requirement.MachineOptionId is null
                ? null
                : operation.Machines.FirstOrDefault(m => m.SourceMachineOptionId == requirement.MachineOptionId);

            // Free-standing labour requirements are operation-level; a requirement bound to a
            // machine option belongs to that machine (Option A1: exactly one owner).
            if (requirement.MachineOptionId is not null && owner is null)
            {
                warnings.Add(
                    $"Labour {requirement.LabourCode} on operation {operation.OperationCode} references "
                    + "a machine option that is not part of this snapshot; it was snapshotted as "
                    + "operation-level labour.");
            }

            var labour = new ProductionWorkOrderLabour
            {
                SourceLabourId = requirement.Uid == 0 ? null : requirement.Uid,
                LabourCode = NormalizeCode(requirement.LabourCode),
                PlannedUnits = requirement.RequiredHeadcount,
                PlannedMinutes = requirement.SetupMinutes + requirement.RunMinutes,
                RateBasis = rateBasis,
                Rate = requirement.CostRate,
                ContributesToPlan = supported && (owner is null || owner.IsSelected),
            };

            if (owner is not null)
            {
                owner.Labours.Add(labour);
            }
            else
            {
                operation.Labours.Add(labour);
            }
        }
    }

    private static ProductionWorkOrderMaterial BuildMaterial(
        PrBomHdr revision,
        ProductionWorkOrderOperation? operation,
        PrDefBOM source,
        IReadOnlyDictionary<string, IvStockMaster> items,
        IReadOnlyDictionary<long, ProductionWorkOrderRouteStep> stepsBySourceId,
        List<WorkOrderCalculationError> errors)
    {
        var componentCode = NormalizeCode(source.ICode);
        items.TryGetValue(componentCode, out var item);

        var standardUom = Normalize(source.StdUom);
        var baseUom = Normalize(item?.StdUom) ?? standardUom;
        var supplySource = Normalize(source.SupplySource) ?? PrMaterialSupplySources.Purchased;

        ProductionWorkOrderRouteStep? producer = null;
        if (string.Equals(supplySource, PrMaterialSupplySources.InternalRouteWip, StringComparison.Ordinal)
            && source.ProducingRouteStepId is not null)
        {
            stepsBySourceId.TryGetValue(source.ProducingRouteStepId.Value, out producer);
        }

        var material = new ProductionWorkOrderMaterial
        {
            SourceOperationId = operation?.SourceOperationId,
            MaterialSequence = source.SeqNo,
            LineNo = source.SeqNo,
            SourceBomHdrId = revision.Uid,
            SourceBomVersion = revision.Version,
            SourceBomLineId = source.Uid == 0 ? null : source.Uid,
            ParentProductCode = NormalizeCode(revision.ProdCode),
            BomPath = string.Empty,
            ComponentCode = componentCode,
            ComponentDescription = source.IName ?? item?.IDesc,
            MfgType = Normalize(item?.MfgType) ?? PrMfgTypes.Buy,

            // ComponentQtyPerParent is expressed against the revision's own base quantity, which is
            // why BomOutputQty/BomOutputUom must be the revision's base and not the route step's.
            ComponentQtyPerParent = source.StdQty,
            StandardUom = standardUom,
            BomOutputQty = revision.BaseQty,
            BomOutputUom = Normalize(revision.BaseUom),
            ScrapPercent = source.ScrapPercent,
            Tolerance = source.Tolerance,

            IssueMethod = Normalize(source.IssueMethod) ?? PrMaterialIssueMethods.Manual,
            SupplySource = supplySource,
            RequiredUom = standardUom,
            BaseUom = baseUom,
            WarehouseCode = Normalize(source.Warehouse) ?? item?.DefWarehouse,
            LocationCode = source.LocationCode ?? item?.DefLocation,
        };

        if (producer is not null)
        {
            material.ProducingRouteStep = producer;
        }
        else if (string.Equals(supplySource, PrMaterialSupplySources.InternalRouteWip, StringComparison.Ordinal))
        {
            errors.Add(new WorkOrderCalculationError(
                ProductionReadinessErrorCodes.WipProducerMissing,
                $"Internal route WIP material {componentCode} does not resolve to a route step in this "
                + "Product Definition revision.",
                $"PrWorkOrderMaterial/{componentCode}"));
        }

        return material;
    }

    /// <summary>
    /// Resolves the built snapshot operation that consumes a definition line. Prefers the loaded
    /// navigation, falls back to the operation key, and finally to the persisted source operation
    /// id, so the answer never depends on how the caller materialized the revision graph.
    /// </summary>
    private static ProductionWorkOrderOperation? ResolveBuiltOperation(
        PrBomHdr revision,
        PrDefBOM line,
        IReadOnlyDictionary<PrBomOperation, ProductionWorkOrderOperation> builtOperations,
        IReadOnlyDictionary<long, ProductionWorkOrderOperation> builtOperationsBySourceId)
    {
        var authored = line.Operation
            ?? (line.OperationId is null
                ? null
                : revision.Operations.FirstOrDefault(o => o.Uid == line.OperationId.Value));

        if (authored is not null && builtOperations.TryGetValue(authored, out var built))
        {
            return built;
        }

        if (line.OperationId is not null && builtOperationsBySourceId.TryGetValue(line.OperationId.Value, out var byId))
        {
            return byId;
        }

        return null;
    }

    private async Task<IReadOnlyDictionary<string, IvStockMaster>> LoadItemsAsync(
        string companyCode,
        IReadOnlyList<string> itemCodes,
        CancellationToken cancellationToken)
    {
        if (itemCodes.Count == 0)
        {
            return new Dictionary<string, IvStockMaster>(StringComparer.OrdinalIgnoreCase);
        }

        var company = NormalizeCode(companyCode);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var items = await db.IvStockMasters.AsNoTracking()
            .Where(i => i.CompanyCode == company && itemCodes.Contains(i.ICode))
            .ToListAsync(cancellationToken);

        return items.ToDictionary(i => NormalizeCode(i.ICode), StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeCode(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();

    private static string? Normalize(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim().ToUpperInvariant();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
