using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionOutputService
{
    public async Task<IvMasterOperationResult<ProductionEligibleOperationPage>> SearchEligibleOperationsAsync(
        ProductionEligibleOperationQuery query, CancellationToken cancellationToken = default)
    {
        if (!await CanAccessAsync(PermissionCodes.Access, cancellationToken))
            return IvMasterOperationResult<ProductionEligibleOperationPage>.Fail(IvMasterErrorCode.AccessDenied, "Access denied.");

        var scope = _tenant.TryBranchScope();
        if (scope is null)
            return IvMasterOperationResult<ProductionEligibleOperationPage>.Fail(
                IvMasterErrorCode.InvalidScope, "A company and branch scope is required.");

        query ??= new ProductionEligibleOperationQuery();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var operations = ApplyOperationFilters(EligibleOperationsQuery(db, scope), query);
        var total = await operations.CountAsync(cancellationToken);
        var take = Math.Clamp(query.Take <= 0 ? 20 : query.Take, 1, 100);
        var skip = Math.Max(0, query.Skip);

        var rows = await operations
            .OrderBy(x => x.WorkOrder!.WorkOrderNo)
            .ThenBy(x => x.RouteStep!.StageSequence)
            .ThenBy(x => x.RouteStep!.WorkCentreCode)
            .ThenBy(x => x.ProcessSequence)
            .ThenBy(x => x.Uid)
            .Skip(skip)
            .Take(take)
            .Select(x => new ProductionEligibleOperationRow
            {
                WorkOrderOperationId = x.Uid,
                WorkOrderId = x.WorkOrderId ?? x.WorkOrder!.Uid,
                WorkOrderNo = x.WorkOrder!.WorkOrderNo,
                ProductCode = x.WorkOrder.ProductCode,
                ProductDescription = x.WorkOrder.ProductDescription,
                WorkCentreCode = x.RouteStep!.WorkCentreCode,
                StageSequence = x.RouteStep.StageSequence,
                OperationCode = x.OperationCode,
                OperationDescription = x.OperationDescription,
                ProcessSequence = x.ProcessSequence,
                WorkOrderStatus = x.WorkOrder.Status,
                OutputItemCode = x.RouteStep.OutputItemCode,
                SelectedMachineCode = x.Machines.Where(m => m.IsSelected).OrderBy(m => m.Priority)
                    .ThenBy(m => m.Uid).Select(m => m.MachineCode).FirstOrDefault(),
                SelectedMachineDescription = x.Machines.Where(m => m.IsSelected).OrderBy(m => m.Priority)
                    .ThenBy(m => m.Uid).Select(m => m.MachineDescription).FirstOrDefault(),
                PlannedOutputQty = x.PlannedOutputQty,
                GoodQty = x.GoodQty,
                RemainingQty = IvQty.Round(x.PlannedOutputQty - x.GoodQty),
                OutputUom = x.PlannedOutputUom ?? x.RouteStep.OutputUom,
                IsFinalOperation = x.IsFinalOperation,
                OutputType = x.RouteStep.OutputType,
            })
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<ProductionEligibleOperationPage>.Ok(new ProductionEligibleOperationPage
        {
            Rows = rows,
            TotalCount = total,
        });
    }

    public async Task<IvMasterOperationResult<ProductionEligibleOperationFilterOptions>>
        GetEligibleOperationFilterOptionsAsync(CancellationToken cancellationToken = default)
    {
        if (!await CanAccessAsync(PermissionCodes.Access, cancellationToken))
            return IvMasterOperationResult<ProductionEligibleOperationFilterOptions>.Fail(
                IvMasterErrorCode.AccessDenied, "Access denied.");

        var scope = _tenant.TryBranchScope();
        if (scope is null)
            return IvMasterOperationResult<ProductionEligibleOperationFilterOptions>.Fail(
                IvMasterErrorCode.InvalidScope, "A company and branch scope is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var operations = await EligibleOperationsQuery(db, scope)
            .Include(x => x.WorkOrder)
            .Include(x => x.RouteStep)
            .Include(x => x.Machines)
            .Include(x => x.Materials)
            .ToListAsync(cancellationToken);

        var workOrders = operations
            .Select(x => new ProductionOutputChoice(
                x.WorkOrder!.WorkOrderNo,
                $"{x.WorkOrder.WorkOrderNo} — {x.WorkOrder.ProductCode}{DescriptionSuffix(x.WorkOrder.ProductDescription)}"))
            .DistinctBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var products = operations
            .Select(x => new ProductionOutputChoice(x.WorkOrder!.ProductCode,
                $"{x.WorkOrder.ProductCode}{DescriptionSuffix(x.WorkOrder.ProductDescription)}"))
            .DistinctBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var workCentres = operations
            .Select(x => x.RouteStep!.WorkCentreCode)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(x => new ProductionOutputChoice(x, x))
            .ToList();
        var processes = operations
            .GroupBy(x => x.OperationCode, StringComparer.OrdinalIgnoreCase)
            .Select(x => new ProductionOutputChoice(
                x.Key,
                $"{x.Key}{DescriptionSuffix(x.First().OperationDescription)}"))
            .OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var outputItems = operations
            .Select(x => new ProductionOutputChoice(
                x.RouteStep!.OutputItemCode,
                $"{x.RouteStep.OutputItemCode}{DescriptionSuffix(x.RouteStep.OutputItemDescription)}"))
            .DistinctBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var rawMaterials = operations
            .SelectMany(x => x.Materials)
            .Select(x => new ProductionOutputChoice(
                x.ComponentCode,
                $"{x.ComponentCode}{DescriptionSuffix(x.ComponentDescription)}"))
            .DistinctBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var machines = operations
            .SelectMany(x => x.Machines.Where(m => m.IsSelected))
            .Select(x => new ProductionOutputChoice(
                x.MachineCode,
                $"{x.MachineCode}{DescriptionSuffix(x.MachineDescription)}"))
            .DistinctBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return IvMasterOperationResult<ProductionEligibleOperationFilterOptions>.Ok(new()
        {
            WorkOrders = workOrders,
            Products = products,
            WorkCentres = workCentres,
            Processes = processes,
            OutputItems = outputItems,
            RawMaterials = rawMaterials,
            Machines = machines,
        });
    }

    public async Task<IvMasterOperationResult<ProductionOutputEntryLookups>> GetEntryLookupsAsync(
        long workOrderOperationId, CancellationToken cancellationToken = default)
    {
        if (!await CanAccessAsync(PermissionCodes.Access, cancellationToken))
            return IvMasterOperationResult<ProductionOutputEntryLookups>.Fail(
                IvMasterErrorCode.AccessDenied, "Access denied.");

        var scope = _tenant.TryBranchScope();
        if (scope is null)
            return IvMasterOperationResult<ProductionOutputEntryLookups>.Fail(
                IvMasterErrorCode.InvalidScope, "A company and branch scope is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var operation = await LoadOperationGraphAsync(db, scope, workOrderOperationId, cancellationToken);
        if (operation?.RouteStep is null || operation.WorkOrder is null)
            return IvMasterOperationResult<ProductionOutputEntryLookups>.Fail(
                IvMasterErrorCode.NotFound, "Operation was not found.");

        var shifts = await db.PrShifts.AsNoTracking()
            .Where(x => x.CompCode == scope.CompanyCode)
            .OrderBy(x => x.ShiftCd)
            .Select(x => new ProductionOutputChoice(
                x.ShiftCd,
                x.ShiftCd + (x.ShiftDes == null || x.ShiftDes == "" ? "" : " — " + x.ShiftDes)))
            .ToListAsync(cancellationToken);
        var machines = await db.PrMachines.AsNoTracking()
            .Where(x => x.CompCode == scope.CompanyCode
                && x.Active
                && x.ProcessCd == operation.OperationCode)
            .OrderBy(x => x.MachineCd)
            .Select(x => new ProductionOutputChoice(
                x.MachineCd,
                x.MachineCd + (x.MachineDes == null || x.MachineDes == "" ? "" : " — " + x.MachineDes)))
            .ToListAsync(cancellationToken);
        var operators = await db.PrOperators.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && (x.Active == null || x.Active == true))
            .OrderBy(x => x.Code)
            .Select(x => new ProductionOutputChoice(
                x.Code,
                x.Code + (x.Name == null || x.Name == "" ? "" : " — " + x.Name)))
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<ProductionOutputEntryLookups>.Ok(new()
        {
            Shifts = shifts,
            Machines = machines,
            Operators = operators,
        });
    }

    public async Task<IvMasterOperationResult<ProductionOutputWorkspace>> GetWorkspaceAsync(
        long workOrderOperationId, DateTime? productionDate = null, CancellationToken cancellationToken = default)
    {
        if (!await CanAccessAsync(PermissionCodes.Access, cancellationToken))
            return IvMasterOperationResult<ProductionOutputWorkspace>.Fail(IvMasterErrorCode.AccessDenied, "Access denied.");

        var scope = _tenant.TryBranchScope();
        if (scope is null)
            return IvMasterOperationResult<ProductionOutputWorkspace>.Fail(
                IvMasterErrorCode.InvalidScope, "A company and branch scope is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var operation = await LoadOperationGraphAsync(db, scope, workOrderOperationId, cancellationToken);
        if (!IsEligibleDailyProductionOperation(operation))
            return IvMasterOperationResult<ProductionOutputWorkspace>.Fail(
                IvMasterErrorCode.NotFound, "Operation is not eligible for Daily Production.");

        var sequenceGraph = await LoadSequenceGraphAsync(db, operation!.WorkOrderId!.Value, cancellationToken);
        var sequence = _operationEligibility.Evaluate(
            operation, sequenceGraph.RouteSteps, sequenceGraph.Operations);
        if (!sequence.IsEligible)
            return IvMasterOperationResult<ProductionOutputWorkspace>.Fail(
                IvMasterErrorCode.Validation, sequence.BlockingReason!);

        return IvMasterOperationResult<ProductionOutputWorkspace>.Ok(
            await BuildWorkspaceAsync(
                db, scope, operation, productionDate ?? DateTime.Today, processedOverride: null,
                savedFacts: null, historical: false, cancellationToken));
    }

    public async Task<IvMasterOperationResult<ProductionOutputWorkspace>> GetDocumentWorkspaceAsync(
        long outputId, CancellationToken cancellationToken = default)
    {
        if (!await CanAccessAsync(PermissionCodes.Access, cancellationToken))
            return IvMasterOperationResult<ProductionOutputWorkspace>.Fail(IvMasterErrorCode.AccessDenied, "Access denied.");

        var scope = _tenant.TryBranchScope();
        if (scope is null)
            return IvMasterOperationResult<ProductionOutputWorkspace>.Fail(
                IvMasterErrorCode.InvalidScope, "A company and branch scope is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var operationId = await db.ProductionOutputs.AsNoTracking()
            .Where(x => x.Uid == outputId
                && x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode)
            .Select(x => (long?)x.WorkOrderOperationId)
            .SingleOrDefaultAsync(cancellationToken);
        if (operationId is null)
            return IvMasterOperationResult<ProductionOutputWorkspace>.Fail(
                IvMasterErrorCode.NotFound, "Production output was not found.");

        var operation = await LoadOperationGraphAsync(db, scope, operationId.Value, cancellationToken);
        if (operation?.RouteStep is null || operation.WorkOrder is null)
            return IvMasterOperationResult<ProductionOutputWorkspace>.Fail(
                IvMasterErrorCode.NotFound, "Saved Work Order operation was not found.");

        var output = await db.ProductionOutputs.AsNoTracking()
            .Include(x => x.Materials)
            .SingleOrDefaultAsync(x => x.Uid == outputId
                && x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode, cancellationToken);
        if (output is null)
            return IvMasterOperationResult<ProductionOutputWorkspace>.Fail(
                IvMasterErrorCode.NotFound, "Production output was not found.");

        var historical = output.Status is ProductionOutputStatuses.Posted or ProductionOutputStatuses.Reversed;
        return IvMasterOperationResult<ProductionOutputWorkspace>.Ok(
            await BuildWorkspaceAsync(
                db, scope, operation, output.ProductionDate,
                processedOverride: ProductionMaterialExecutionCalc.ProcessedThisPost(
                    output.GoodQty, output.ScrapQty, output.RejectQty, output.HoldQty),
                savedFacts: output.Materials.ToList(),
                historical: historical,
                cancellationToken));
    }

    private static IQueryable<ProductionWorkOrderOperation> EligibleOperationsQuery(
        AppDbContext db, InventoryTenantScope scope) =>
        db.ProductionWorkOrderOperations.AsNoTracking()
            .Where(x => x.WorkOrder != null
                && x.RouteStep != null
                && x.RouteStep.OutputType != null
                && x.RouteStep.OutputType.Trim() != ""
                && (x.RouteStep.YieldPercent == null || x.RouteStep.YieldPercent == 100m)
                && x.WorkOrder.SnapshotHashVersion >= ProductionSnapshotHashVersions.Current
                && x.WorkOrder.CompanyCode == scope.CompanyCode
                && x.WorkOrder.BranchCode == scope.BranchCode
                && (x.WorkOrder.Status == ProductionWorkOrderStatuses.Released
                    || x.WorkOrder.Status == ProductionWorkOrderStatuses.InProgress)
                && x.RouteStep.StageSequence > 0
                && x.ProcessSequence > 0
                && x.PlannedOutputQty - x.GoodQty > 0m
                // A malformed released snapshot must not expose an operation as executable.
                && !db.ProductionWorkOrderRouteSteps.Any(route =>
                    route.WorkOrderId == x.WorkOrderId && route.StageSequence <= 0)
                && !db.ProductionWorkOrderRouteSteps.Any(route =>
                    route.WorkOrderId == x.WorkOrderId
                    && !db.ProductionWorkOrderOperations.Any(operation =>
                        operation.WorkOrderId == x.WorkOrderId && operation.RouteStepId == route.Uid))
                && !db.ProductionWorkOrderOperations.Any(operation =>
                    operation.WorkOrderId == x.WorkOrderId
                    && (operation.RouteStepId == null
                        || operation.ProcessSequence <= 0
                        || !db.ProductionWorkOrderRouteSteps.Any(route =>
                            route.Uid == operation.RouteStepId && route.WorkOrderId == x.WorkOrderId)))
                // All lower stages must be complete. Equal stages are parallel and excluded.
                && !db.ProductionWorkOrderOperations.Any(lowerOperation =>
                    lowerOperation.WorkOrderId == x.WorkOrderId
                    && lowerOperation.RouteStep != null
                    && lowerOperation.RouteStep.StageSequence < x.RouteStep.StageSequence
                    && lowerOperation.PlannedOutputQty - lowerOperation.GoodQty > 0m)
                // All lower processes in the same route step must have posted Good. Equal processes
                // remain parallel and therefore are intentionally excluded.
                && !db.ProductionWorkOrderOperations.Any(lowerOperation =>
                    lowerOperation.RouteStepId == x.RouteStepId
                    && lowerOperation.ProcessSequence < x.ProcessSequence
                    && lowerOperation.GoodQty <= 0m));

    private static IQueryable<ProductionWorkOrderOperation> ApplyOperationFilters(
        IQueryable<ProductionWorkOrderOperation> operations,
        ProductionEligibleOperationQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.WorkOrderNo))
            operations = ApplyTextFilter(operations, query.WorkOrderNo, x => x.WorkOrder!.WorkOrderNo, query.ExactMatch);
        if (!string.IsNullOrWhiteSpace(query.ProductCode))
            operations = ApplyTextFilter(operations, query.ProductCode, x => x.WorkOrder!.ProductCode, query.ExactMatch);
        if (!string.IsNullOrWhiteSpace(query.WorkCentreCode))
            operations = ApplyTextFilter(operations, query.WorkCentreCode, x => x.RouteStep!.WorkCentreCode, query.ExactMatch);
        if (!string.IsNullOrWhiteSpace(query.OperationCode))
            operations = ApplyTextFilter(operations, query.OperationCode, x => x.OperationCode, query.ExactMatch);
        if (!string.IsNullOrWhiteSpace(query.OutputItemCode))
            operations = ApplyTextFilter(operations, query.OutputItemCode, x => x.RouteStep!.OutputItemCode, query.ExactMatch);
        if (!string.IsNullOrWhiteSpace(query.MachineCode))
        {
            var value = query.MachineCode.Trim();
            operations = query.ExactMatch
                ? operations.Where(x => x.Machines.Any(m => m.IsSelected && m.MachineCode == value))
                : operations.Where(x => x.Machines.Any(m => m.IsSelected && m.MachineCode.Contains(value)));
        }
        if (!string.IsNullOrWhiteSpace(query.RawMaterialCode))
        {
            var value = query.RawMaterialCode.Trim();
            operations = query.ExactMatch
                ? operations.Where(x => x.Materials.Any(m => m.ComponentCode == value))
                : operations.Where(x => x.Materials.Any(m => m.ComponentCode.Contains(value)
                    || (m.ComponentDescription != null && m.ComponentDescription.Contains(value))));
        }
        return operations;
    }

    private static IQueryable<ProductionWorkOrderOperation> ApplyTextFilter(
        IQueryable<ProductionWorkOrderOperation> query,
        string value,
        System.Linq.Expressions.Expression<Func<ProductionWorkOrderOperation, string?>> selector,
        bool exact)
    {
        var trimmed = value.Trim();
        var parameter = selector.Parameters[0];
        var body = selector.Body;
        var constant = System.Linq.Expressions.Expression.Constant(trimmed, typeof(string));
        System.Linq.Expressions.Expression expression;
        if (exact)
            expression = System.Linq.Expressions.Expression.Equal(body, constant);
        else
            expression = System.Linq.Expressions.Expression.Call(body, nameof(string.Contains), Type.EmptyTypes, constant);
        return query.Where(System.Linq.Expressions.Expression.Lambda<Func<ProductionWorkOrderOperation, bool>>(
            expression, parameter));
    }

    private static bool IsEligibleDailyProductionOperation(ProductionWorkOrderOperation? operation) =>
        operation?.WorkOrder is not null
        && operation.RouteStep is not null
        && (operation.WorkOrder.Status is ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress)
        && operation.WorkOrder.SnapshotHashVersion >= ProductionSnapshotHashVersions.Current
        && !string.IsNullOrWhiteSpace(operation.RouteStep.OutputType)
        && (operation.RouteStep.YieldPercent is null || operation.RouteStep.YieldPercent == 100m)
        && operation.RouteStep.StageSequence > 0
        && operation.ProcessSequence > 0
        && IvQty.Round(operation.PlannedOutputQty - operation.GoodQty) > 0m;

    private static async Task<(
        List<ProductionWorkOrderRouteStep> RouteSteps,
        List<ProductionWorkOrderOperation> Operations)> LoadSequenceGraphAsync(
        AppDbContext db,
        long workOrderId,
        CancellationToken cancellationToken)
    {
        var routeSteps = await db.ProductionWorkOrderRouteSteps.AsNoTracking()
            .Where(x => x.WorkOrderId == workOrderId)
            .OrderBy(x => x.StageSequence)
            .ThenBy(x => x.Uid)
            .ToListAsync(cancellationToken);
        var operations = await db.ProductionWorkOrderOperations.AsNoTracking()
            .Where(x => x.WorkOrderId == workOrderId)
            .OrderBy(x => x.RouteStepId)
            .ThenBy(x => x.ProcessSequence)
            .ThenBy(x => x.Uid)
            .ToListAsync(cancellationToken);
        return (routeSteps, operations);
    }

    private async Task<ProductionOutputWorkspace> BuildWorkspaceAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        ProductionWorkOrderOperation operation,
        DateTime productionDate,
        decimal? processedOverride,
        IReadOnlyList<ProductionOutputMaterial>? savedFacts,
        bool historical,
        CancellationToken cancellationToken)
    {
        var processed = processedOverride
            ?? 0m;
        var materials = new List<ProductionOutputMaterialLine>();
        foreach (var material in operation.Materials.OrderBy(x => x.MaterialSequence))
        {
            var blocking = ProductionOutputMaterialSupport.BlockingReason(material);
            var available = blocking is null
                ? await AvailableQtyAsync(db, scope, material, productionDate, cancellationToken)
                : 0m;
            var saved = savedFacts?.FirstOrDefault(x => x.WorkOrderMaterialId == material.Uid);
            var standard = historical && saved is not null
                ? saved.StandardQty
                : ProductionMaterialExecutionCalc.DailyProductionStandardQty(
                    material.RequiredQty, operation.PlannedOutputQty, processed);
            var consume = saved is not null ? saved.ConsumeQty : standard;
            var reasonCode = historical || saved is not null ? saved?.VarianceReasonCode : null;
            var reasonText = historical || saved is not null ? saved?.VarianceReasonText : null;
            if (historical && saved is not null)
            {
                consume = saved.ConsumeQty;
                standard = saved.StandardQty;
            }

            materials.Add(ToLine(material, standard, consume, reasonCode, reasonText, available, blocking));
        }

        if (operation.RouteStepId is long routeStepId)
        {
            var siblings = await db.ProductionWorkOrderOperations.AsNoTracking()
                .Where(x => x.RouteStepId == routeStepId)
                .OrderBy(x => x.ProcessSequence)
                .ThenBy(x => x.Uid)
                .ToListAsync(cancellationToken);
            if (ProductionProcessHandoff.TryGetImmediatePrior(siblings, operation, out var prior) is null
                && prior is not null)
            {
                var savedHandoff = savedFacts?.FirstOrDefault(x => x.IsHandoff);
                var available = await AvailableHandoffQtyAsync(
                    db, scope, operation, prior.Uid, productionDate, cancellationToken);
                var consume = historical && savedHandoff is not null ? savedHandoff.ConsumeQty : processed;
                var standard = historical && savedHandoff is not null ? savedHandoff.StandardQty : processed;
                var uom = savedHandoff?.RequiredUom
                    ?? operation.PlannedOutputUom
                    ?? operation.RouteStep!.OutputUom
                    ?? string.Empty;
                materials.Add(new ProductionOutputMaterialLine
                {
                    WorkOrderMaterialId = ProductionProcessHandoff.SyntheticMaterialId(prior.Uid),
                    ComponentCode = savedHandoff?.ComponentCode ?? operation.RouteStep!.OutputItemCode,
                    Description = $"Previous process {prior.OperationCode}",
                    IssueMethod = ProductionProcessHandoff.IssueMethod,
                    SupplySource = ProductionProcessHandoff.SupplySource,
                    RequiredQty = 0m,
                    WoBomRequiredQty = 0m,
                    RequiredUom = uom,
                    StandardQty = standard,
                    ConsumeQty = consume,
                    MaxConsumeQty = consume,
                    VarianceQty = 0m,
                    AvailableQty = available,
                    RemainingAfterConsume = IvQty.Round(available - consume),
                    IsHandoff = true,
                    HandoffFromOperationId = prior.Uid,
                    IsConsumeEditable = false,
                });
            }
        }

        var selectedMachine = SelectedMachine(operation);
        return new ProductionOutputWorkspace
        {
            Operation = new ProductionEligibleOperationRow
            {
                WorkOrderOperationId = operation.Uid,
                WorkOrderId = operation.WorkOrder!.Uid,
                WorkOrderNo = operation.WorkOrder.WorkOrderNo,
                ProductCode = operation.WorkOrder.ProductCode,
                ProductDescription = operation.WorkOrder.ProductDescription,
                WorkCentreCode = operation.RouteStep!.WorkCentreCode,
                StageSequence = operation.RouteStep.StageSequence,
                OperationCode = operation.OperationCode,
                OperationDescription = operation.OperationDescription,
                ProcessSequence = operation.ProcessSequence,
                WorkOrderStatus = operation.WorkOrder.Status,
                OutputItemCode = operation.RouteStep.OutputItemCode,
                SelectedMachineCode = selectedMachine?.MachineCode,
                SelectedMachineDescription = selectedMachine?.MachineDescription,
                PlannedOutputQty = operation.PlannedOutputQty,
                GoodQty = operation.GoodQty,
                RemainingQty = IvQty.Round(operation.PlannedOutputQty - operation.GoodQty),
                OutputUom = operation.PlannedOutputUom ?? operation.RouteStep.OutputUom,
                IsFinalOperation = operation.IsFinalOperation,
                OutputType = operation.RouteStep.OutputType,
            },
            Materials = materials,
        };
    }

    private static string DescriptionSuffix(string? description) =>
        string.IsNullOrWhiteSpace(description) ? string.Empty : $" — {description}";
}
