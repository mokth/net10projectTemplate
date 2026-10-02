using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionOutputService : IProductionOutputService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _access;
    private readonly ICurrentDateService _clock;
    private readonly IRunningNumberService _runningNumbers;

    public ProductionOutputService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService access,
        ICurrentDateService clock,
        IRunningNumberService runningNumbers)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
        _clock = clock;
        _runningNumbers = runningNumbers;
    }

    public async Task<IvMasterOperationResult<ProductionOutputDetail>> CreateAsync(
        ProductionOutputCreateRequest request, CancellationToken cancellationToken = default)
    {
        if (!await CanAccessAsync(PermissionCodes.Add, cancellationToken))
            return Fail("Access denied.", IvMasterErrorCode.AccessDenied);

        var scope = WriteScope();
        if (scope is null) return Fail("A company, branch and user scope is required.", IvMasterErrorCode.InvalidScope);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var operation = await LoadOperationGraphAsync(db, scope, request.WorkOrderOperationId, cancellationToken);
        if (operation is null)
            return Fail("Work Order operation was not found.");

        var validation = ValidateQuantities(request);
        if (validation is not null) return Fail(validation);

        var routeStep = operation.RouteStep
            ?? await db.ProductionWorkOrderRouteSteps.SingleAsync(x => x.Uid == operation.RouteStepId, cancellationToken);
        var order = operation.WorkOrder
            ?? await db.ProductionWorkOrders.SingleAsync(x => x.Uid == operation.WorkOrderId, cancellationToken);

        if (order.Status is not (ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress))
            return Fail("Work Order must be RELEASED or IN_PROGRESS.");
        if (string.IsNullOrWhiteSpace(routeStep.OutputType))
            return Fail("Route step OutputType is missing; refresh and release the Work Order snapshot (hash V3).");
        if (routeStep.YieldPercent is not null and not 100m)
            return Fail("Non-100% yield is not supported in this milestone.");

        var now = _clock.Now;
        var user = TruncateUser(scope.UserId);
        var documentNo = await AllocateDocumentNoAsync(db, scope.CompanyCode, cancellationToken);
        var postingRequestId = Guid.NewGuid().ToString("N");

        var output = new ProductionOutput
        {
            CompanyCode = scope.CompanyCode,
            BranchCode = scope.BranchCode!,
            DocumentNo = documentNo,
            Status = ProductionOutputStatuses.New,
            WorkOrderId = order.Uid,
            RouteStepId = routeStep.Uid,
            WorkOrderOperationId = operation.Uid,
            ProductionDate = request.ProductionDate,
            ShiftCode = Normalize(request.ShiftCode),
            ActualMachineCode = Normalize(request.ActualMachineCode),
            OperatorCode = Normalize(request.OperatorCode),
            GoodQty = IvQty.Round(request.GoodQty),
            ScrapQty = IvQty.Round(request.ScrapQty),
            RejectQty = IvQty.Round(request.RejectQty),
            HoldQty = IvQty.Round(request.HoldQty),
            OutputUom = routeStep.OutputUom ?? operation.PlannedOutputUom ?? order.OutputUom ?? string.Empty,
            OutputItemCode = routeStep.OutputItemCode,
            OutputType = routeStep.OutputType,
            OutputLotNo = (request.OutputLotNo ?? string.Empty).Trim(),
            SnapshotRevision = order.SnapshotRevision,
            SnapshotHash = order.SnapshotHash ?? string.Empty,
            PostingRequestId = postingRequestId,
            CreatedDate = now,
            CreatedBy = user,
        };
        db.ProductionOutputs.Add(output);
        await db.SaveChangesAsync(cancellationToken);

        db.ProductionPostingLinks.Add(new ProductionPostingLink
        {
            CompanyCode = scope.CompanyCode,
            BranchCode = scope.BranchCode!,
            CommandType = ProductionPostingCommandTypes.OutputPost,
            PostingRequestId = postingRequestId,
            WorkOrderId = order.Uid,
            ProductionDocumentType = ProductionDocumentTypes.ProductionOutput,
            ProductionDocumentNo = documentNo,
            ProductionDocumentLineId = output.Uid,
            SnapshotRevision = order.SnapshotRevision,
            SnapshotHash = order.SnapshotHash,
            Status = ProductionPostingLinkStatuses.Draft,
            CreatedDate = now,
            CreatedBy = user,
        });
        await db.SaveChangesAsync(cancellationToken);

        return Ok(await MapDetailAsync(db, output.Uid, cancellationToken));
    }

    public async Task<IvMasterOperationResult<ProductionOutputDetail>> UpdateAsync(
        ProductionOutputUpdateRequest request, CancellationToken cancellationToken = default)
    {
        if (!await CanAccessAsync(PermissionCodes.Edit, cancellationToken))
            return Fail("Access denied.", IvMasterErrorCode.AccessDenied);
        var scope = WriteScope();
        if (scope is null) return Fail("A company, branch and user scope is required.", IvMasterErrorCode.InvalidScope);

        var validation = ValidateQuantities(request);
        if (validation is not null) return Fail(validation);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var output = await db.ProductionOutputs.SingleOrDefaultAsync(x =>
            x.Uid == request.OutputId
            && x.CompanyCode == scope.CompanyCode
            && x.BranchCode == scope.BranchCode, cancellationToken);
        if (output is null) return Fail("Production output was not found.", IvMasterErrorCode.NotFound);
        if (output.Status != ProductionOutputStatuses.New)
            return Fail("Only NEW drafts can be edited.");
        if (!output.RowVersion.SequenceEqual(request.RowVersion ?? []))
            return Fail("The document was changed by another user.", IvMasterErrorCode.Concurrency);

        output.ProductionDate = request.ProductionDate;
        output.ShiftCode = Normalize(request.ShiftCode);
        output.ActualMachineCode = Normalize(request.ActualMachineCode);
        output.OperatorCode = Normalize(request.OperatorCode);
        output.GoodQty = IvQty.Round(request.GoodQty);
        output.ScrapQty = IvQty.Round(request.ScrapQty);
        output.RejectQty = IvQty.Round(request.RejectQty);
        output.HoldQty = IvQty.Round(request.HoldQty);
        output.OutputLotNo = (request.OutputLotNo ?? string.Empty).Trim();
        output.ModifiedDate = _clock.Now;
        output.ModifiedBy = TruncateUser(scope.UserId);
        await db.SaveChangesAsync(cancellationToken);
        return Ok(await MapDetailAsync(db, output.Uid, cancellationToken));
    }

    public async Task<IvMasterOperationResult<bool>> DeleteAsync(long outputId, CancellationToken cancellationToken = default)
    {
        if (!await CanAccessAsync(PermissionCodes.Delete, cancellationToken))
            return IvMasterOperationResult<bool>.Fail(IvMasterErrorCode.AccessDenied, "Access denied.");
        var scope = WriteScope();
        if (scope is null)
            return IvMasterOperationResult<bool>.Fail(IvMasterErrorCode.InvalidScope, "A company, branch and user scope is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var output = await db.ProductionOutputs.SingleOrDefaultAsync(x =>
            x.Uid == outputId && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode, cancellationToken);
        if (output is null)
            return IvMasterOperationResult<bool>.Fail(IvMasterErrorCode.NotFound, "Production output was not found.");
        if (output.Status != ProductionOutputStatuses.New)
            return IvMasterOperationResult<bool>.Fail(IvMasterErrorCode.Validation, "Only NEW drafts can be deleted.");

        var link = await db.ProductionPostingLinks.SingleOrDefaultAsync(x =>
            x.CommandType == ProductionPostingCommandTypes.OutputPost
            && x.PostingRequestId == output.PostingRequestId, cancellationToken);
        if (link is not null) db.ProductionPostingLinks.Remove(link);
        db.ProductionOutputs.Remove(output);
        await db.SaveChangesAsync(cancellationToken);
        return IvMasterOperationResult<bool>.Ok(true);
    }

    public async Task<IvMasterOperationResult<ProductionOutputDetail>> GetAsync(
        long outputId, CancellationToken cancellationToken = default)
    {
        if (!await CanAccessAsync(PermissionCodes.Access, cancellationToken))
            return Fail("Access denied.", IvMasterErrorCode.AccessDenied);
        var scope = _tenant.TryBranchScope();
        if (scope is null) return Fail("A company and branch scope is required.", IvMasterErrorCode.InvalidScope);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var detail = await MapDetailAsync(db, outputId, cancellationToken);
        if (detail is null
            || !string.Equals(detail.WorkOrderNo, detail.WorkOrderNo, StringComparison.Ordinal))
        {
            // MapDetail returns null when missing
        }
        if (detail is null) return Fail("Production output was not found.", IvMasterErrorCode.NotFound);
        return Ok(detail);
    }

    public async Task<IvMasterOperationResult<ProductionOutputSearchPage>> SearchAsync(
        ProductionOutputSearchQuery query, CancellationToken cancellationToken = default)
    {
        if (!await CanAccessAsync(PermissionCodes.Access, cancellationToken))
            return IvMasterOperationResult<ProductionOutputSearchPage>.Fail(IvMasterErrorCode.AccessDenied, "Access denied.");
        var scope = _tenant.TryBranchScope();
        if (scope is null)
            return IvMasterOperationResult<ProductionOutputSearchPage>.Fail(IvMasterErrorCode.InvalidScope, "A company and branch scope is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var q = db.ProductionOutputs.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode);
        if (!string.IsNullOrWhiteSpace(query.Status))
            q = q.Where(x => x.Status == query.Status.Trim());
        if (!string.IsNullOrWhiteSpace(query.WorkOrderNo))
        {
            var wo = query.WorkOrderNo.Trim();
            q = q.Where(x => db.ProductionWorkOrders.Any(w => w.Uid == x.WorkOrderId && w.WorkOrderNo.Contains(wo)));
        }
        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var term = query.SearchText.Trim();
            q = q.Where(x => x.DocumentNo.Contains(term) || x.OutputItemCode.Contains(term) || x.OutputLotNo.Contains(term));
        }

        var total = await q.CountAsync(cancellationToken);
        var take = Math.Clamp(query.Take <= 0 ? 50 : query.Take, 1, 200);
        var rows = await q.OrderByDescending(x => x.ProductionDate).ThenByDescending(x => x.Uid)
            .Skip(Math.Max(0, query.Skip)).Take(take)
            .Select(x => new ProductionOutputListRow
            {
                Uid = x.Uid,
                DocumentNo = x.DocumentNo,
                Status = x.Status,
                WorkOrderNo = db.ProductionWorkOrders.Where(w => w.Uid == x.WorkOrderId).Select(w => w.WorkOrderNo).FirstOrDefault() ?? "",
                OperationCode = db.ProductionWorkOrderOperations.Where(o => o.Uid == x.WorkOrderOperationId).Select(o => o.OperationCode).FirstOrDefault() ?? "",
                ProductionDate = x.ProductionDate,
                GoodQty = x.GoodQty,
                ScrapQty = x.ScrapQty,
                RejectQty = x.RejectQty,
                OutputItemCode = x.OutputItemCode,
                OutputLotNo = x.OutputLotNo,
            }).ToListAsync(cancellationToken);

        return IvMasterOperationResult<ProductionOutputSearchPage>.Ok(new ProductionOutputSearchPage
        {
            Rows = rows,
            TotalCount = total,
        });
    }

    public async Task<IvMasterOperationResult<IReadOnlyList<ProductionEligibleOperationRow>>> SearchEligibleOperationsAsync(
        ProductionEligibleOperationQuery query, CancellationToken cancellationToken = default)
    {
        if (!await CanAccessAsync(PermissionCodes.Access, cancellationToken))
            return IvMasterOperationResult<IReadOnlyList<ProductionEligibleOperationRow>>.Fail(IvMasterErrorCode.AccessDenied, "Access denied.");
        var scope = _tenant.TryBranchScope();
        if (scope is null)
            return IvMasterOperationResult<IReadOnlyList<ProductionEligibleOperationRow>>.Fail(IvMasterErrorCode.InvalidScope, "A company and branch scope is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var take = Math.Clamp(query.Take <= 0 ? 100 : query.Take, 1, 500);
        var ops = db.ProductionWorkOrderOperations.AsNoTracking()
            .Include(x => x.WorkOrder)
            .Include(x => x.RouteStep)
            .Where(x => x.WorkOrder != null
                && x.WorkOrder.CompanyCode == scope.CompanyCode
                && x.WorkOrder.BranchCode == scope.BranchCode
                && (x.WorkOrder.Status == ProductionWorkOrderStatuses.Released
                    || x.WorkOrder.Status == ProductionWorkOrderStatuses.InProgress)
                && x.PlannedOutputQty - x.GoodQty > 0m);

        if (!string.IsNullOrWhiteSpace(query.WorkOrderNo))
            ops = ops.Where(x => x.WorkOrder!.WorkOrderNo.Contains(query.WorkOrderNo.Trim()));
        if (!string.IsNullOrWhiteSpace(query.ProductCode))
            ops = ops.Where(x => x.WorkOrder!.ProductCode.Contains(query.ProductCode.Trim()));
        if (!string.IsNullOrWhiteSpace(query.WorkCentreCode))
            ops = ops.Where(x => (x.RouteStep != null && x.RouteStep.WorkCentreCode == query.WorkCentreCode.Trim())
                || x.WorkCentreCode == query.WorkCentreCode.Trim());
        if (!string.IsNullOrWhiteSpace(query.OperationCode))
            ops = ops.Where(x => x.OperationCode.Contains(query.OperationCode.Trim()));
        if (!string.IsNullOrWhiteSpace(query.OutputItemCode))
            ops = ops.Where(x => x.RouteStep != null && x.RouteStep.OutputItemCode.Contains(query.OutputItemCode.Trim()));

        var rows = await ops.OrderBy(x => x.WorkOrder!.WorkOrderNo).ThenBy(x => x.ProcessSequence)
            .Take(take)
            .Select(x => new ProductionEligibleOperationRow
            {
                WorkOrderOperationId = x.Uid,
                WorkOrderId = x.WorkOrderId ?? x.WorkOrder!.Uid,
                WorkOrderNo = x.WorkOrder!.WorkOrderNo,
                ProductCode = x.WorkOrder.ProductCode,
                WorkCentreCode = x.RouteStep != null ? x.RouteStep.WorkCentreCode : (x.WorkCentreCode ?? ""),
                OperationCode = x.OperationCode,
                OutputItemCode = x.RouteStep != null ? x.RouteStep.OutputItemCode : "",
                PlannedOutputQty = x.PlannedOutputQty,
                GoodQty = x.GoodQty,
                RemainingQty = IvQty.Round(x.PlannedOutputQty - x.GoodQty),
                OutputUom = x.PlannedOutputUom ?? x.RouteStep!.OutputUom,
                IsFinalOperation = x.IsFinalOperation,
                OutputType = x.RouteStep!.OutputType,
            }).ToListAsync(cancellationToken);

        return IvMasterOperationResult<IReadOnlyList<ProductionEligibleOperationRow>>.Ok(rows);
    }

    public async Task<IvMasterOperationResult<ProductionOutputWorkspace>> GetWorkspaceAsync(
        long workOrderOperationId, CancellationToken cancellationToken = default)
    {
        if (!await CanAccessAsync(PermissionCodes.Access, cancellationToken))
            return IvMasterOperationResult<ProductionOutputWorkspace>.Fail(IvMasterErrorCode.AccessDenied, "Access denied.");
        var scope = _tenant.TryBranchScope();
        if (scope is null)
            return IvMasterOperationResult<ProductionOutputWorkspace>.Fail(IvMasterErrorCode.InvalidScope, "A company and branch scope is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var operation = await LoadOperationGraphAsync(db, scope, workOrderOperationId, cancellationToken);
        if (operation?.WorkOrder is null || operation.RouteStep is null)
            return IvMasterOperationResult<ProductionOutputWorkspace>.Fail(IvMasterErrorCode.NotFound, "Operation was not found.");

        var materials = new List<ProductionOutputMaterialLine>();
        foreach (var material in operation.Materials.OrderBy(x => x.MaterialSequence))
        {
            var available = 0m;
            string? blocking = null;
            if (string.Equals(material.IssueMethod, PrMaterialIssueMethods.Manual, StringComparison.OrdinalIgnoreCase)
                && (material.SupplySource is PrMaterialSupplySources.Purchased or PrMaterialSupplySources.ExternalSupply))
            {
                available = await db.ProductionBalLots.AsNoTracking()
                    .Where(x => x.Kind == ProductionBalLotKinds.MaterialIn
                        && x.WorkOrderMaterialId == material.Uid
                        && x.BaseQty > 0m)
                    .SumAsync(x => x.Qty, cancellationToken);
            }
            else if (string.Equals(material.SupplySource, PrMaterialSupplySources.InternalRouteWip, StringComparison.OrdinalIgnoreCase))
            {
                if (material.ProducingRouteStepId is null)
                    blocking = "Producing route step is missing.";
                else
                {
                    available = await db.ProductionBalLots.AsNoTracking()
                        .Where(x => x.Kind == ProductionBalLotKinds.Wip
                            && x.WorkOrderId == operation.WorkOrderId
                            && x.ProducingRouteStepId == material.ProducingRouteStepId
                            && x.ItemCode == material.ComponentCode
                            && x.BaseQty > 0m)
                        .SumAsync(x => x.Qty, cancellationToken);
                }
            }
            else
            {
                blocking = $"{material.IssueMethod}/{material.SupplySource} is not supported for Daily Production in this milestone.";
            }

            materials.Add(new ProductionOutputMaterialLine
            {
                WorkOrderMaterialId = material.Uid,
                ComponentCode = material.ComponentCode,
                Description = material.ComponentDescription,
                IssueMethod = material.IssueMethod,
                SupplySource = material.SupplySource,
                RequiredQty = material.RequiredQty,
                RequiredUom = material.RequiredUom ?? "",
                AvailableQty = IvQty.Round(available),
                BlockingReason = blocking,
            });
        }

        var row = new ProductionEligibleOperationRow
        {
            WorkOrderOperationId = operation.Uid,
            WorkOrderId = operation.WorkOrder!.Uid,
            WorkOrderNo = operation.WorkOrder.WorkOrderNo,
            ProductCode = operation.WorkOrder.ProductCode,
            WorkCentreCode = operation.RouteStep.WorkCentreCode,
            OperationCode = operation.OperationCode,
            OutputItemCode = operation.RouteStep.OutputItemCode,
            PlannedOutputQty = operation.PlannedOutputQty,
            GoodQty = operation.GoodQty,
            RemainingQty = IvQty.Round(operation.PlannedOutputQty - operation.GoodQty),
            OutputUom = operation.PlannedOutputUom ?? operation.RouteStep.OutputUom,
            IsFinalOperation = operation.IsFinalOperation,
            OutputType = operation.RouteStep.OutputType,
        };

        return IvMasterOperationResult<ProductionOutputWorkspace>.Ok(new ProductionOutputWorkspace
        {
            Operation = row,
            Materials = materials,
        });
    }

    private async Task<string> AllocateDocumentNoAsync(AppDbContext db, string company, CancellationToken ct)
    {
        for (var i = 0; i < 10; i++)
        {
            var seq = await _runningNumbers.GetNextAsync(db, company, RunningNumberKeys.ProductionDailyOutput, ct);
            var number = $"DP{seq:D8}";
            if (!await db.ProductionOutputs.AsNoTracking()
                    .AnyAsync(x => x.CompanyCode == company && x.DocumentNo == number, ct))
                return number;
        }
        throw new InvalidOperationException("Unable to allocate a Production Output document number.");
    }

    private async Task<ProductionWorkOrderOperation?> LoadOperationGraphAsync(
        AppDbContext db, InventoryTenantScope scope, long operationId, CancellationToken ct) =>
        await db.ProductionWorkOrderOperations
            .Include(x => x.WorkOrder)
            .Include(x => x.RouteStep)
            .Include(x => x.Materials)
            .SingleOrDefaultAsync(x => x.Uid == operationId
                && x.WorkOrder != null
                && x.WorkOrder.CompanyCode == scope.CompanyCode
                && x.WorkOrder.BranchCode == scope.BranchCode, ct);

    private static string? ValidateQuantities(ProductionOutputCreateRequest request)
    {
        if (request.WorkOrderOperationId <= 0) return "Operation is required.";
        if (request.GoodQty < 0m || request.ScrapQty < 0m || request.RejectQty < 0m || request.HoldQty < 0m)
            return "Quantities cannot be negative.";
        if (request.GoodQty + request.ScrapQty + request.RejectQty + request.HoldQty <= 0m)
            return "At least one of Good/Scrap/Reject/Hold must be positive.";
        return null;
    }

    private async Task<bool> CanAccessAsync(string permission, CancellationToken ct) =>
        await _access.CanAsync(MenuCodes.PlanningDailyProduction, PermissionCodes.Access, ct)
        && (permission == PermissionCodes.Access
            || await _access.CanAsync(MenuCodes.PlanningDailyProduction, permission, ct));

    private InventoryTenantScope? WriteScope()
    {
        var scope = _tenant.TryWriteScope();
        return scope is null || string.IsNullOrWhiteSpace(scope.BranchCode) ? null : scope;
    }

    private static string TruncateUser(string userId) => userId.Length > 10 ? userId[..10] : userId;
    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IvMasterOperationResult<ProductionOutputDetail> Fail(
        string message, IvMasterErrorCode code = IvMasterErrorCode.Validation) =>
        IvMasterOperationResult<ProductionOutputDetail>.Fail(code, message);

    private static IvMasterOperationResult<ProductionOutputDetail> Ok(ProductionOutputDetail? detail) =>
        detail is null
            ? Fail("Production output was not found.", IvMasterErrorCode.NotFound)
            : IvMasterOperationResult<ProductionOutputDetail>.Ok(detail);

    private static async Task<ProductionOutputDetail?> MapDetailAsync(AppDbContext db, long uid, CancellationToken ct)
    {
        return await db.ProductionOutputs.AsNoTracking()
            .Where(x => x.Uid == uid)
            .Select(x => new ProductionOutputDetail
            {
                Uid = x.Uid,
                DocumentNo = x.DocumentNo,
                Status = x.Status,
                WorkOrderId = x.WorkOrderId,
                WorkOrderNo = db.ProductionWorkOrders.Where(w => w.Uid == x.WorkOrderId).Select(w => w.WorkOrderNo).FirstOrDefault() ?? "",
                RouteStepId = x.RouteStepId,
                WorkOrderOperationId = x.WorkOrderOperationId,
                OperationCode = db.ProductionWorkOrderOperations.Where(o => o.Uid == x.WorkOrderOperationId).Select(o => o.OperationCode).FirstOrDefault() ?? "",
                WorkCentreCode = db.ProductionWorkOrderRouteSteps.Where(r => r.Uid == x.RouteStepId).Select(r => r.WorkCentreCode).FirstOrDefault() ?? "",
                ProductionDate = x.ProductionDate,
                ShiftCode = x.ShiftCode,
                ActualMachineCode = x.ActualMachineCode,
                OperatorCode = x.OperatorCode,
                GoodQty = x.GoodQty,
                ScrapQty = x.ScrapQty,
                RejectQty = x.RejectQty,
                HoldQty = x.HoldQty,
                OutputUom = x.OutputUom,
                OutputItemCode = x.OutputItemCode,
                OutputType = x.OutputType,
                OutputLotNo = x.OutputLotNo,
                PostingRequestId = x.PostingRequestId,
                RowVersion = x.RowVersion,
            }).SingleOrDefaultAsync(ct);
    }
}
