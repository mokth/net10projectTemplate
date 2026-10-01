    private async Task<List<NormalizedOperation>> ValidateOperationsAsync(
        AppDbContext db,
        string company,
        string productCode,
        IReadOnlyList<PrProductDefOperationVm> operations,
        bool publishing,
        IReadOnlyDictionary<string, string?> itemStdUomByCode,
        Dictionary<string, string> errors,
        CancellationToken cancellationToken)
    {
        var result = new List<NormalizedOperation>();
        if (operations.Count == 0)
        {
            // Existing BOM-only definitions remain loadable during migration. Once routing is
            // introduced, the complete legacy publication rules below are enforced.
            return result;
        }

        var activeItemCodes = itemStdUomByCode.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var centres = await db.PrWorkCentres.AsNoTracking()
            .Where(x => x.CompCode == company)
            .Select(x => new { x.WrkCtrCd, x.WrkCtrDes })
            .ToListAsync(cancellationToken);
        var centreByCode = centres.ToDictionary(x => x.WrkCtrCd, StringComparer.OrdinalIgnoreCase);
        var processRows = await db.PrProcesses.AsNoTracking()
            .Where(x => x.CompCode == company)
            .Select(x => new { x.ProcessCd, x.WorkCentre, x.ProcessDes })
            .ToListAsync(cancellationToken);
        var processByKey = processRows.ToDictionary(
            x => $"{x.WorkCentre}|{x.ProcessCd}", StringComparer.OrdinalIgnoreCase);
        var machineRows = await db.PrMachines.AsNoTracking()
            .Where(x => x.CompCode == company)
            .Select(x => new { x.MachineCd, x.ProcessCd, x.MachineDes })
            .ToListAsync(cancellationToken);
        var machineByKey = machineRows
            .GroupBy(x => $"{x.ProcessCd}|{x.MachineCd}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var labourRows = await db.PrOperators.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.Active != false)
            .Select(x => new { x.Code, x.Name })
            .ToListAsync(cancellationToken);
        var labourByCode = labourRows.ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);

        var seenOperations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < operations.Count; i++)
        {
            var source = operations[i];
            var prefix = $"Operations[{i}]";
            var wc = NormalizeCode(source.WorkCentreCode);
            var output = NormalizeCode(source.OutputItemCode);
            var operation = NormalizeCode(source.OperationCode);
            var processType = (source.ProcessType ?? PrProcessTypes.Machine).Trim().ToUpperInvariant();
            if (!PrProcessTypes.IsValid(processType))
                errors[$"{prefix}.ProcessType"] = $"Route {i + 1}: select a valid process type.";

            if (wc.Length == 0 || wc.Length > 20 || !centreByCode.ContainsKey(wc))
                errors[$"{prefix}.WorkCentreCode"] = $"Route {i + 1}: select a valid work centre.";
            if (output.Length == 0 || output.Length > CodeMax || !activeItemCodes.Contains(output))
                errors[$"{prefix}.OutputItemCode"] = $"Route {i + 1}: select an active output item.";
            else if (itemStdUomByCode.TryGetValue(output, out var inventoryUomRaw))
            {
                var outputUom = Truncate(source.OutputUom ?? inventoryUomRaw, UomMax);
                var inventoryUom = Truncate(inventoryUomRaw, UomMax);
                if (!string.IsNullOrWhiteSpace(outputUom)
                    && !string.IsNullOrWhiteSpace(inventoryUom)
                    && !string.Equals(outputUom, inventoryUom, StringComparison.OrdinalIgnoreCase)
                    && !await _uom.HasConversionAsync(company, output, outputUom, inventoryUom, cancellationToken))
                {
                    errors[$"{prefix}.OutputUom"] =
                        $"Route {i + 1}: no approved conversion from {outputUom} to inventory UOM {inventoryUom} ({UomConversionFailureCodes.MissingConversion}).";
                }
            }
            if (operation.Length == 0 || operation.Length > 20
                || !processByKey.ContainsKey($"{wc}|{operation}"))
                errors[$"{prefix}.OperationCode"] = $"Route {i + 1}: select a process belonging to work centre {wc}.";
            if (source.CentralSequence <= 0)
                errors[$"{prefix}.CentralSequence"] = $"Route {i + 1}: central sequence must be greater than zero.";
            if (source.ProcessSequence <= 0)
                errors[$"{prefix}.ProcessSequence"] = $"Route {i + 1}: process sequence must be greater than zero.";

            var outputQty = IvQty.Round(source.OutputBaseQty);
            if (outputQty <= 0m)
                errors[$"{prefix}.OutputBaseQty"] = $"Route {i + 1}: output base quantity must be greater than zero.";

            var operationKey = $"{wc}|{output}|{operation}";
            if (!seenOperations.Add(operationKey))
                errors[$"{prefix}.OperationCode"] = $"Route {i + 1}: duplicate work centre/output/process combination.";

            var standardDuration = IvQty.Round(source.StandardDurationMinutes);
            if (standardDuration < 0m)
                errors[$"{prefix}.StandardDurationMinutes"] = $"Route {i + 1}: standard duration cannot be negative.";

            var durationBased = PrProcessTypes.IsDurationBased(processType);
            var machineCapable = PrProcessTypes.SupportsMachine(processType);
            // MACHINE/AUTOMATED always need a resource. Duration-only steps must not carry one.
            // PACKING is either machine-driven or duration-driven depending on what was authored.
            var requiresMachine = processType is PrProcessTypes.Machine or PrProcessTypes.Automated
                                  || (processType == PrProcessTypes.Packing && source.Machines.Count > 0);
            var requiresDuration = durationBased
                                   || (processType == PrProcessTypes.Packing && source.Machines.Count == 0);
            if (durationBased && source.Machines.Count > 0)
                errors[$"{prefix}.Machines"] =
                    $"Route {i + 1}: {processType} steps are duration-based and cannot select a machine.";
            else if (!machineCapable && !durationBased && source.Machines.Count > 0)
                errors[$"{prefix}.Machines"] =
                    $"Route {i + 1}: process type {processType} does not support machine options.";

            var machines = new List<NormalizedMachine>();
            var seenMachines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenMachinePriorities = new HashSet<int>();
            var primaryCount = 0;
            for (var j = 0; j < source.Machines.Count; j++)
            {
                var machine = source.Machines[j];
                var machinePrefix = $"{prefix}.Machines[{j}]";
                var machineCode = NormalizeCode(machine.MachineCode);
                var machineMaster = machineByKey.GetValueOrDefault($"{operation}|{machineCode}");
                if (machineCode.Length == 0 || machineCode.Length > 20
                    || machineMaster is null)
                {
                    errors[$"{machinePrefix}.MachineCode"] =
                        $"Route {i + 1}, machine {j + 1}: select a machine belonging to process {operation}.";
                }

                if (!seenMachines.Add(machineCode))
                    errors[$"{machinePrefix}.MachineCode"] = $"Route {i + 1}: duplicate machine {machineCode}.";
                if (machine.IsPrimary)
                    primaryCount++;
                if (machine.ResourceSequence <= 0)
                    errors[$"{machinePrefix}.ResourceSequence"] = "Machine sequence must be greater than zero.";
                if (machine.Priority <= 0)
                    errors[$"{machinePrefix}.Priority"] = "Machine priority must be greater than zero.";
                else if (!seenMachinePriorities.Add(machine.Priority))
                    errors[$"{machinePrefix}.Priority"] = $"Route {i + 1}: duplicate machine priority {machine.Priority}.";
                if (machine.ParallelMachineCount <= 0)
                    errors[$"{machinePrefix}.ParallelMachineCount"] = "Parallel machine count must be greater than zero.";
                if (machine.CycleSeconds < 0m || machine.ConversionSeconds < 0m
                    || machine.SetupSeconds < 0m || machine.QueueSeconds < 0m)
                    errors[$"{machinePrefix}.CycleSeconds"] = "Machine times cannot be negative.";
                if (machine.MachineRatePerHour < 0m)
                    errors[$"{machinePrefix}.MachineRatePerHour"] = "Machine rate per hour cannot be negative.";

                var outputPerCycle = IvQty.Round(machine.OutputPerCycle);
                if (outputPerCycle <= 0m)
                    errors[$"{machinePrefix}.OutputPerCycle"] = "Output per cycle must be greater than zero.";

                var labours = new List<NormalizedLabour>();
                if (machine.Labours.Count > 1)
                    errors[$"{machinePrefix}.Labours"] =
                        $"Route {i + 1}: the process-machine can have only one labour/operator.";
                var seenLabour = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var k = 0; k < machine.Labours.Count; k++)
                {
                    var labour = machine.Labours[k];
                    var labourPrefix = $"{machinePrefix}.Labours[{k}]";
                    var labourCode = NormalizeCode(labour.LabourCode);
                    var labourMaster = labourByCode.GetValueOrDefault(labourCode);
                    if (labourCode.Length == 0 || labourCode.Length > 20
                        || labourMaster is null)
                        errors[$"{labourPrefix}.LabourCode"] = "Select an active labour/operator code.";
                    if (!seenLabour.Add(labourCode))
                        errors[$"{labourPrefix}.LabourCode"] = $"Duplicate labour {labourCode}.";
                    if (labour.CostPerOutputUnit < 0m)
                        errors[$"{labourPrefix}.CostPerOutputUnit"] = "Labour cost per output unit cannot be negative.";

                    labours.Add(new NormalizedLabour(
                        labourCode,
                        labourByCode.GetValueOrDefault(labourCode)?.Name,
                        decimal.Round(labour.CostPerOutputUnit, 6, MidpointRounding.AwayFromZero)));
                }

                machines.Add(new NormalizedMachine(
                    machineCode,
                    machineMaster?.MachineDes ?? machine.MachineDescription,
                    machine.ResourceSequence,
                    machine.IsPrimary,
                    machine.Priority,
                    outputPerCycle,
                    IvQty.Round(machine.CycleSeconds),
                    IvQty.Round(machine.ConversionSeconds),
                    IvQty.Round(machine.SetupSeconds),
                    IvQty.Round(machine.QueueSeconds),
                    IvQty.Round(machine.MachineRatePerHour),
                    machine.ParallelMachineCount,
                    labours));
            }

            if (source.Machines.Count > 0 && primaryCount != 1)
                errors[$"{prefix}.Machines"] =
                    $"Route {i + 1}: select exactly one primary machine option.";

            if (publishing)
            {
                if (requiresMachine)
                {
                    if (machines.Count == 0)
                        errors[$"{prefix}.Machines"] =
                            $"Route {i + 1}: process {operation} requires at least one machine before activation.";
                    else if (machines.All(m => m.Labours.Count == 0))
                        errors[$"{prefix}.Machines"] =
                            $"Route {i + 1}: process {operation} requires a labour/operator on a machine option before activation.";
                }

                if (requiresDuration && standardDuration <= 0m)
                    errors[$"{prefix}.StandardDurationMinutes"] =
                        $"Route {i + 1}: process {operation} requires a standard duration greater than zero before activation.";
            }

            result.Add(new NormalizedOperation(
                source.OperationKey,
                wc,
                output,
                source.CentralSequence,
                outputQty,
                Truncate(source.OutputUom, UomMax),
                operation,
                source.ProcessSequence,
                processType,
                standardDuration,
                IvQty.Round(source.SetupLossQty),
                IvQty.Round(source.OperationLossQty),
                source.IsFinalOperation,
                Truncate(source.Remark, 500),
                machines));
        }

        if (publishing)
        {
            foreach (var centre in result.GroupBy(x => $"{x.WorkCentreCode}|{x.OutputItemCode}", StringComparer.OrdinalIgnoreCase))
            {
                var finals = centre.Count(x => x.IsFinalOperation);
                if (finals != 1)
                    errors[nameof(PrProductDefEditVm.Operations)] =
                        $"Work centre/output {centre.Key} must have exactly one final process before activation.";
            }

            // UQ_PrBomOperation_RouteStep_Sequence is (RouteStepID, ProcessSequence). Surface a
            // duplicate as a field error rather than a database constraint violation.
            foreach (var step in result.GroupBy(x => (x.WorkCentreCode, x.OutputItemCode, x.CentralSequence)))
            {
                var duplicate = step.GroupBy(x => x.ProcessSequence).FirstOrDefault(g => g.Count() > 1);
                if (duplicate is not null)
                    errors[nameof(PrProductDefEditVm.Operations)] =
                        $"Work centre/output {step.Key.OutputItemCode} stage {step.Key.CentralSequence} has duplicate process sequence {duplicate.Key}.";
            }
        }

        return result;
    }

    /// <summary>
    /// Persists the version's route: one <see cref="PrBomRouteStep"/> per
    /// (work centre, output item, central sequence) occurrence, each owning its process steps.
    /// Existing step keys are reused so a material's <c>ProducingRouteStepKey</c> survives a
    /// save/reload round-trip.
    /// </summary>
    private static (Dictionary<Guid, PrBomOperation> Operations, Dictionary<Guid, PrBomRouteStep> RouteSteps) AddRoute(
        AppDbContext db,
        PrBomHdr header,
        string company,
        IReadOnlyList<NormalizedOperation> operations,
        IReadOnlyDictionary<(string WorkCentreCode, string OutputItemCode, int StageSequence), Guid> existingStepKeys,
        DateTime now,
        string user)
    {
        var operationsByKey = new Dictionary<Guid, PrBomOperation>();
        var routeStepsByKey = new Dictionary<Guid, PrBomRouteStep>();
        var stepsByGroup = new Dictionary<(string WorkCentreCode, string OutputItemCode, int StageSequence), PrBomRouteStep>();

        var ordered = operations
            .OrderBy(x => x.CentralSequence)
            .ThenBy(x => x.WorkCentreCode)
            .ThenBy(x => x.OutputItemCode)
            .ThenBy(x => x.ProcessSequence)
            .ToList();

        foreach (var source in ordered)
        {
            var groupKey = (source.WorkCentreCode, source.OutputItemCode, source.CentralSequence);
            if (!stepsByGroup.TryGetValue(groupKey, out var routeStep))
            {
                var routeStepKey = existingStepKeys.TryGetValue(groupKey, out var priorKey) && priorKey != Guid.Empty
                    ? priorKey
                    : Guid.NewGuid();
                routeStep = new PrBomRouteStep
                {
                    Header = header,
                    BomHdrId = header.Uid,
                    RouteStepKey = routeStepKey,
                    CompanyCode = company,
                    WorkCentreCode = source.WorkCentreCode,
                    StageSequence = source.CentralSequence,
                    OutputItemCode = source.OutputItemCode,
                    OutputType = string.Equals(source.OutputItemCode, header.ProdCode, StringComparison.OrdinalIgnoreCase)
                        ? PrRouteOutputTypes.FinishedGoods
                        : PrRouteOutputTypes.WipStocked,
                    StandardOutputQty = source.OutputBaseQty,
                    OutputUom = source.OutputUom ?? header.BaseUom ?? string.Empty,
                    YieldPercent = 100m,
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user,
                    RowVersion = []
                };
                db.PrBomRouteSteps.Add(routeStep);
                stepsByGroup[groupKey] = routeStep;
                routeStepsByKey[routeStepKey] = routeStep;
            }

            var operation = new PrBomOperation
            {
                Header = header,
                RouteStep = routeStep,
                OperationKey = source.OperationKey,
                BomHdrId = header.Uid,
                CompanyCode = company,
                WorkCentreCode = source.WorkCentreCode,
                OutputItemCode = source.OutputItemCode,
                CentralSequence = source.CentralSequence,
                OutputBaseQty = source.OutputBaseQty,
                OutputUom = source.OutputUom,
                OperationCode = source.OperationCode,
                ProcessSequence = source.ProcessSequence,
                ProcessType = source.ProcessType,
                StandardDurationMinutes = source.StandardDurationMinutes,
                SetupLossQty = source.SetupLossQty,
                OperationLossQty = source.OperationLossQty,
                IsFinalOperation = source.IsFinalOperation,
                Remark = source.Remark,
                CreatedDate = now,
                CreatedBy = user,
                ModifiedDate = now,
                ModifiedBy = user,
                RowVersion = []
            };
            foreach (var machineSource in source.Machines
                         .OrderBy(x => x.ResourceSequence)
                         .ThenBy(x => x.Priority)
                         .ThenBy(x => x.MachineCode))
            {
                var machine = new PrBomMachineOption
                {
                    Operation = operation,
                    MachineCode = machineSource.MachineCode,
                    MachineDescription = Truncate(machineSource.MachineDescription, NameMax),
                    ResourceSequence = machineSource.ResourceSequence,
                    IsPrimary = machineSource.IsPrimary,
                    Priority = machineSource.Priority,
                    OutputPerCycle = machineSource.OutputPerCycle,
                    CycleSeconds = machineSource.CycleSeconds,
                    ConversionSeconds = machineSource.ConversionSeconds,
                    SetupSeconds = machineSource.SetupSeconds,
                    QueueSeconds = machineSource.QueueSeconds,
                    MachineRatePerHour = machineSource.MachineRatePerHour,
                    ParallelMachineCount = machineSource.ParallelMachineCount,
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user,
                    RowVersion = []
                };
                foreach (var labourSource in machineSource.Labours)
                {
                    machine.Labours.Add(new PrBomLabourStandard
                    {
                        MachineOption = machine,
                        LabourCode = labourSource.LabourCode,
                        LabourDescription = Truncate(labourSource.LabourDescription, NameMax),
                        CostPerOutputUnit = labourSource.CostPerOutputUnit,
                        CreatedDate = now,
                        CreatedBy = user,
                        ModifiedDate = now,
                        ModifiedBy = user,
                        RowVersion = []
                    });
                }
                operation.Machines.Add(machine);
            }
            db.PrBomOperations.Add(operation);
            operationsByKey[source.OperationKey] = operation;
        }

        return (operationsByKey, routeStepsByKey);
    }

    /// <summary>
    /// Route step keys a client may reference as a material producer during this save. Only
    /// persisted steps are known: a brand-new definition has none until it is saved once.
    /// </summary>
    private static Dictionary<Guid, string> KnownRouteStepOutputItems(IReadOnlyList<PrBomRouteStep> persistedSteps) =>
        persistedSteps
            .GroupBy(x => x.RouteStepKey)
            .ToDictionary(x => x.Key, x => x.First().OutputItemCode, EqualityComparer<Guid>.Default);

    private sealed record NormalizedOperation(
        Guid OperationKey,
        string WorkCentreCode,
        string OutputItemCode,
        int CentralSequence,
        decimal OutputBaseQty,
        string? OutputUom,
        string OperationCode,
        int ProcessSequence,
        string ProcessType,
        decimal StandardDurationMinutes,
        decimal SetupLossQty,
        decimal OperationLossQty,
        bool IsFinalOperation,
        string? Remark,
        IReadOnlyList<NormalizedMachine> Machines);

    private sealed record NormalizedMachine(
        string MachineCode,
        string? MachineDescription,
        int ResourceSequence,
        bool IsPrimary,
        int Priority,
        decimal OutputPerCycle,
        decimal CycleSeconds,
        decimal ConversionSeconds,
        decimal SetupSeconds,
        decimal QueueSeconds,
        decimal MachineRatePerHour,
        int ParallelMachineCount,
        IReadOnlyList<NormalizedLabour> Labours);

    private sealed record NormalizedLabour(
        string LabourCode,
        string? LabourDescription,
        decimal CostPerOutputUnit);

    private static PrProductDefEditVm MapEdit(
        PrBomHdr header,
        IvStockMaster? product,
        List<PrDefBOM> lines,
        Dictionary<string, string> mfgByCode,
        List<PrBomOperation> operations,
        Dictionary<long, Guid> routeStepKeyById,
        Dictionary<string, string?> workCentreDescriptions,
        Dictionary<string, string?> operationDescriptions) =>
        new()
        {
            ProdCode = header.ProdCode,
            ProdDesc = product?.IDesc,
            StdUom = product?.StdUom,
            MfgType = PrMfgTypes.Normalize(product?.MfgType),
            IsActive = product?.IsActive ?? false,
            BomHdrId = header.Uid,
            Version = header.Version,
            Status = header.Status,
            EffectiveFrom = header.EffectiveFrom,
            EffectiveTo = header.EffectiveTo,
            BaseQty = header.BaseQty,
            BaseUom = header.BaseUom,
            Prefix = header.Prefix,
            Remark = header.Remark,
            HeaderRowVersion = header.RowVersion,
            Lines = lines.Select(x => new PrProductDefLineVm
            {
                OperationKey = x.OperationKey,
                Uid = x.Uid,
                ICode = x.ICode,
                IName = x.IName,
                StdQty = x.StdQty,
                StdUom = x.StdUom,
                SeqNo = x.SeqNo,
                ScrapPercent = x.ScrapPercent,
                Warehouse = x.Warehouse,
                BomDefault = x.BomDefault,
                AlternateGroupCode = x.AlternateGroupCode,
                Tolerance = x.Tolerance,
                IssueMethod = x.IssueMethod,
                SupplySource = x.SupplySource,
                ProducingRouteStepKey = x.ProducingRouteStepId is { } producerId
                                        && routeStepKeyById.TryGetValue(producerId, out var producerKey)
                    ? producerKey
                    : null,
                MfgType = mfgByCode.TryGetValue(x.ICode, out var m) ? m : PrMfgTypes.Buy
            }).ToList(),
            Operations = operations.Select(x => new PrProductDefOperationVm
            {
                OperationKey = x.OperationKey,
                Uid = x.Uid,
                WorkCentreCode = x.WorkCentreCode,
                WorkCentreDescription = workCentreDescriptions.GetValueOrDefault(x.WorkCentreCode),
                OutputItemCode = x.OutputItemCode,
                CentralSequence = x.CentralSequence,
                OutputBaseQty = x.OutputBaseQty,
                OutputUom = x.OutputUom,
                OperationCode = x.OperationCode,
                OperationDescription = operationDescriptions.GetValueOrDefault(x.OperationCode),
                ProcessSequence = x.ProcessSequence,
                ProcessType = x.ProcessType,
                StandardDurationMinutes = x.StandardDurationMinutes,
                SetupLossQty = x.SetupLossQty,
                OperationLossQty = x.OperationLossQty,
                IsFinalOperation = x.IsFinalOperation,
                Remark = x.Remark,
                RouteStepKey = x.RouteStepId is { } routeStepId
                    && routeStepKeyById.TryGetValue(routeStepId, out var routeStepKey)
                    ? routeStepKey
                    : null,
                Machines = x.Machines
                    .OrderBy(m => m.ResourceSequence)
                    .ThenBy(m => m.MachineCode)
                    .Select(m => new PrProductDefMachineVm
                    {
                        Uid = m.Uid,
                        MachineCode = m.MachineCode,
                        MachineDescription = m.MachineDescription,
                        ResourceSequence = m.ResourceSequence,
                        IsPrimary = m.IsPrimary,
                        Priority = m.Priority,
                        OutputPerCycle = m.OutputPerCycle,
                        CycleSeconds = m.CycleSeconds,
                        ConversionSeconds = m.ConversionSeconds,
                        SetupSeconds = m.SetupSeconds,
                        QueueSeconds = m.QueueSeconds,
                        MachineRatePerHour = m.MachineRatePerHour,
                        ParallelMachineCount = m.ParallelMachineCount,
                        Labours = m.Labours
                            .OrderBy(l => l.LabourCode)
                            .Select(l => new PrProductDefLabourVm
                            {
                                Uid = l.Uid,
                                LabourCode = l.LabourCode,
                                LabourDescription = l.LabourDescription,
                                CostPerOutputUnit = l.CostPerOutputUnit
                            }).ToList()
                    }).ToList()
            }).ToList()
        };

    private async Task<(string? CompanyCode, string? UserId, ScopeError? Error)> RequireCompanyScopeAsync(
        string permission,
        CancellationToken cancellationToken)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return (null, null, new ScopeError(IvMasterErrorCode.InvalidScope, "Invalid company context."));
        }

        if (!await _accessRights.CanAsync(MenuCodes.PlanningProductDef, permission, cancellationToken))
        {
            return (null, null, new ScopeError(IvMasterErrorCode.AccessDenied, "Not authorized."));
        }

        return (scope.CompanyCode, scope.UserId, null);
    }

    private sealed record ScopeError(IvMasterErrorCode Code, string Message);

    private static IvMasterOperationResult<PrProductDefListPage> FailPage(ScopeError error) =>
        IvMasterOperationResult<PrProductDefListPage>.Fail(error.Code, error.Message);

    private static IvMasterOperationResult<PrProductDefEditVm> FailVm(ScopeError error) =>
        IvMasterOperationResult<PrProductDefEditVm>.Fail(error.Code, error.Message);

    private static IvMasterOperationResult<PrBomStructureResult> FailStructure(ScopeError error) =>
        IvMasterOperationResult<PrBomStructureResult>.Fail(error.Code, error.Message);

    private static IvMasterOperationResult<PrBomStructureResult> FailStructure(
        IvMasterErrorCode code,
        string message,
        string? field = null)
    {
        Dictionary<string, string>? errors = null;
        if (!string.IsNullOrWhiteSpace(field))
        {
            errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [field] = message };
        }

        return IvMasterOperationResult<PrBomStructureResult>.Fail(code, message, errors);
    }

    private static IvMasterOperationResult<PrProductDefEditVm> FailVm(
        IvMasterErrorCode code,
        string message,
        string? field = null)
    {
        Dictionary<string, string>? errors = null;
        if (!string.IsNullOrWhiteSpace(field))
        {
            errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [field] = message };
        }

        return IvMasterOperationResult<PrProductDefEditVm>.Fail(code, message, errors);
    }

    private static string NormalizeCode(string? value) =>
        (value ?? string.Empty).Trim().ToUpperInvariant();

    private static List<string> NormalizeCodes(IReadOnlyList<string>? codes) =>
        (codes ?? [])
            .Select(NormalizeCode)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string ValidateCode(
        IDictionary<string, string> errors,
        string field,
        string label,
        string? value,
        int maxLen)
    {
        var code = NormalizeCode(value);
        if (code.Length == 0)
        {
            errors[field] = $"{label} is required.";
            return string.Empty;
        }

        if (code.Length > maxLen)
        {
            errors[field] = $"{label} is too long.";
            return string.Empty;
        }

        return code;
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
