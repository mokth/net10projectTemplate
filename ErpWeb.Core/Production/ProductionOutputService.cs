using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.Transactions;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionOutputService : IProductionOutputService
{
    internal static Action? TestHookAfterOutputValuation;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _access;
    private readonly ICurrentDateService _clock;
    private readonly IRunningNumberService _runningNumbers;
    private readonly IStockPostingCoordinator _stockCoordinator;
    private readonly IProductionOperationEligibilityService _operationEligibility;

    public ProductionOutputService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService access,
        ICurrentDateService clock,
        IRunningNumberService runningNumbers,
        IStockPostingCoordinator stockCoordinator,
        IProductionOperationEligibilityService? operationEligibility = null)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
        _clock = clock;
        _runningNumbers = runningNumbers;
        _stockCoordinator = stockCoordinator ?? throw new ArgumentNullException(nameof(stockCoordinator));
        _operationEligibility = operationEligibility ?? new ProductionOperationEligibilityService();
    }

    public async Task<IvMasterOperationResult<ProductionOutputDetail>> CreateAsync(
        ProductionOutputCreateRequest request, CancellationToken cancellationToken = default)
    {
        if (!await CanAccessAsync(PermissionCodes.Add, cancellationToken))
            return Fail("Access denied.", IvMasterErrorCode.AccessDenied);

        if (!Guid.TryParseExact(request.PostingRequestId, "N", out _))
            return Fail("PostingRequestId must be a GUID in N format.");

        var scope = WriteScope();
        if (scope is null) return Fail("A company, branch and user scope is required.", IvMasterErrorCode.InvalidScope);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var replay = await db.ProductionOutputs
                .Include(x => x.Materials)
                .SingleOrDefaultAsync(x =>
                x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.PostingRequestId == request.PostingRequestId, cancellationToken);
            if (replay is not null)
            {
                if (!ReplayPayloadMatches(replay, request))
                    return Fail("PostingRequestId was already used with a different Daily Production payload.", IvMasterErrorCode.Concurrency);

                await tx.CommitAsync(cancellationToken);
                return Ok(await MapDetailAsync(db, scope, replay.Uid, cancellationToken));
            }

        var operation = await LoadOperationGraphAsync(db, scope, request.WorkOrderOperationId, cancellationToken);
        if (operation is null)
            return Fail("Work Order operation was not found.");

        var validation = ValidateQuantities(request);
        if (validation is not null) return Fail(validation);

        var routeStep = operation.RouteStep;
        if (routeStep is null)
            return Fail("The Work Order operation has no route step.");
        var order = operation.WorkOrder;
        if (order is null)
            return Fail("The Work Order operation has no Work Order.");

        if (order.Status is not (ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress))
            return Fail("Work Order must be RELEASED or IN_PROGRESS.");
        if (string.IsNullOrWhiteSpace(routeStep.OutputType))
            return Fail("Route step OutputType is missing; refresh and release the Work Order snapshot (hash V3).");
        if (routeStep.YieldPercent is not null and not 100m)
            return Fail("Non-100% yield is not supported in this milestone.");
        if (order.SnapshotHashVersion < ProductionSnapshotHashVersions.Current)
            return Fail("Work Order snapshot hash version must be refreshed to V3 before Daily Production.");
        if (IvQty.Round(operation.PlannedOutputQty - operation.GoodQty) <= 0m)
            return Fail("The Work Order operation has no remaining Good quantity.");

        var sequenceGraph = await LoadSequenceGraphAsync(db, order.Uid, cancellationToken);
        var sequence = _operationEligibility.Evaluate(
            operation, sequenceGraph.RouteSteps, sequenceGraph.Operations);
        if (!sequence.IsEligible)
            return Fail(sequence.BlockingReason!);

        var referenceError = await ValidateReferencesAsync(
            db, scope, operation, request.ShiftCode, request.ActualMachineCode, request.OperatorCode,
            cancellationToken);
        if (referenceError is not null)
            return Fail(referenceError);

        var now = _clock.Now;
        var user = TruncateUser(scope.UserId);
        var documentNo = await AllocateDocumentNoAsync(db, scope.CompanyCode, cancellationToken);

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
            PlannedMachineCode = SelectedMachine(operation)?.MachineCode,
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
            PostingRequestId = request.PostingRequestId,
            CreatedDate = now,
            CreatedBy = user,
        };
        db.ProductionOutputs.Add(output);
        await db.SaveChangesAsync(cancellationToken);

        var materialError = await PersistMaterialFactsAsync(
            db, scope, output, operation, request.Materials ?? [], interactive: true, cancellationToken);
        if (materialError is not null)
            return Fail(materialError);

        db.ProductionPostingLinks.Add(new ProductionPostingLink
        {
            CompanyCode = scope.CompanyCode,
            BranchCode = scope.BranchCode!,
            CommandType = ProductionPostingCommandTypes.OutputPost,
            PostingRequestId = request.PostingRequestId,
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

        await tx.CommitAsync(cancellationToken);
        return Ok(await MapDetailAsync(db, scope, output.Uid, cancellationToken));
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var replay = await db.ProductionOutputs.AsNoTracking()
                .Include(x => x.Materials)
                .SingleOrDefaultAsync(x =>
                x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.PostingRequestId == request.PostingRequestId, cancellationToken);
            if (replay is not null && ReplayPayloadMatches(replay, request))
                return Ok(await MapDetailAsync(db, scope, replay.Uid, cancellationToken));

            return Fail("Daily Production creation conflicted with another request; retry with the same PostingRequestId.",
                IvMasterErrorCode.Concurrency);
        }
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
        if (output.DeletedAtUtc is not null)
            return Fail(TransactionLifecycleGuard.ArchivedError(output.DeletedAtUtc, "This production output")!);
        if (output.Status != ProductionOutputStatuses.New)
            return Fail("Only NEW drafts can be edited.");
        if (!output.RowVersion.SequenceEqual(request.RowVersion ?? []))
            return Fail("The document was changed by another user.", IvMasterErrorCode.Concurrency);
        if (request.WorkOrderOperationId != output.WorkOrderOperationId)
            return Fail("The Work Order operation cannot be changed after the draft is created.");

        var operation = await LoadOperationGraphAsync(db, scope, output.WorkOrderOperationId, cancellationToken);
        if (operation is null)
            return Fail("The saved Work Order operation was not found.", IvMasterErrorCode.NotFound);

        var referenceError = await ValidateReferenceChangesAsync(
            db, scope, operation, output, request, cancellationToken);
        if (referenceError is not null)
            return Fail(referenceError);

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
        var materialError = await PersistMaterialFactsAsync(
            db, scope, output, operation, request.Materials ?? [], interactive: true, cancellationToken);
        if (materialError is not null)
            return Fail(materialError);
        await db.SaveChangesAsync(cancellationToken);
        return Ok(await MapDetailAsync(db, scope, output.Uid, cancellationToken));
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
        if (output.DeletedAtUtc is not null)
            return IvMasterOperationResult<bool>.Ok(true);

        var decision = await TransactionDeleteApplicator.DecideAsync(
            db,
            new TransactionDeleteSubject(
                scope.CompanyCode, scope.BranchCode!,
                TransactionDeleteOwnerTypes.ProductionOutput,
                output.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                output.DocumentNo),
            cancellationToken);
        if (decision.Mode == TransactionDeleteMode.Block)
            return IvMasterOperationResult<bool>.Fail(
                IvMasterErrorCode.Validation,
                decision.BlockingReason ?? TransactionDeleteMessages.NotDeletableStatus);
        if (decision.Mode == TransactionDeleteMode.ArchiveHistorical)
        {
            InventoryBatchRetention.Archive(output, scope.UserId, null);
            await db.SaveChangesAsync(cancellationToken);
            return IvMasterOperationResult<bool>.Ok(true);
        }

        var link = await db.ProductionPostingLinks.SingleOrDefaultAsync(x =>
            x.CompanyCode == scope.CompanyCode
            && x.BranchCode == scope.BranchCode
            && x.CommandType == ProductionPostingCommandTypes.OutputPost
            && x.PostingRequestId == output.PostingRequestId, cancellationToken);
        if (link is not null
            && !string.Equals(link.Status, ProductionPostingLinkStatuses.Draft, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(link.Status, ProductionPostingLinkStatuses.Cancelled, StringComparison.OrdinalIgnoreCase))
        {
            InventoryBatchRetention.Archive(output, scope.UserId, null);
            await db.SaveChangesAsync(cancellationToken);
            return IvMasterOperationResult<bool>.Ok(true);
        }

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
        var detail = await MapDetailAsync(db, scope, outputId, cancellationToken);
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
            .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode && x.DeletedAtUtc == null);
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
            .Include(x => x.Machines)
            .SingleOrDefaultAsync(x => x.Uid == operationId
                && x.WorkOrder != null
                && x.WorkOrder.CompanyCode == scope.CompanyCode
                && x.WorkOrder.BranchCode == scope.BranchCode, ct);

    private static ProductionWorkOrderMachine? SelectedMachine(ProductionWorkOrderOperation operation) =>
        operation.Machines
            .Where(x => x.IsSelected)
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.Uid)
            .FirstOrDefault();

    private static async Task<string?> ValidateReferencesAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        ProductionWorkOrderOperation operation,
        string? shiftCode,
        string? actualMachineCode,
        string? operatorCode,
        CancellationToken ct)
    {
        var shift = Normalize(shiftCode);
        if (shift is not null && !await db.PrShifts.AsNoTracking().AnyAsync(x =>
                x.CompCode == scope.CompanyCode && x.ShiftCd == shift, ct))
            return $"Shift {shift} was not found in the company master.";

        var machine = Normalize(actualMachineCode);
        if (machine is not null)
        {
            var machineRow = await db.PrMachines.AsNoTracking()
                .Where(x => x.CompCode == scope.CompanyCode && x.MachineCd == machine)
                .Select(x => new { x.Active, x.ProcessCd })
                .SingleOrDefaultAsync(ct);
            if (machineRow is null)
                return $"Machine {machine} was not found in the company master.";
            if (!machineRow.Active)
                return $"Machine {machine} is inactive.";
            if (!string.Equals(machineRow.ProcessCd, operation.OperationCode, StringComparison.OrdinalIgnoreCase))
                return $"Machine {machine} does not belong to process {operation.OperationCode}.";
        }

        var operatorValue = Normalize(operatorCode);
        if (operatorValue is not null)
        {
            var active = await db.PrOperators.AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode && x.Code == operatorValue)
                .Select(x => x.Active)
                .SingleOrDefaultAsync(ct);
            if (active is null)
            {
                var exists = await db.PrOperators.AsNoTracking().AnyAsync(x =>
                    x.CompanyCode == scope.CompanyCode && x.Code == operatorValue, ct);
                if (!exists)
                    return $"Operator {operatorValue} was not found in the company master.";
            }
            else if (active == false)
            {
                return $"Operator {operatorValue} is inactive.";
            }
        }

        return null;
    }

    private async Task<string?> ValidateReferenceChangesAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        ProductionWorkOrderOperation operation,
        ProductionOutput output,
        ProductionOutputUpdateRequest request,
        CancellationToken ct)
    {
        var shiftChanged = !CodesEqual(output.ShiftCode, request.ShiftCode);
        var machineChanged = !CodesEqual(output.ActualMachineCode, request.ActualMachineCode);
        var operatorChanged = !CodesEqual(output.OperatorCode, request.OperatorCode);

        if (!shiftChanged && !machineChanged && !operatorChanged)
            return null;

        return await ValidateReferencesAsync(
            db,
            scope,
            operation,
            shiftChanged ? request.ShiftCode : null,
            machineChanged ? request.ActualMachineCode : null,
            operatorChanged ? request.OperatorCode : null,
            ct);
    }

    private static bool CodesEqual(string? left, string? right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

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

    private static async Task<ProductionOutputDetail?> MapDetailAsync(
        AppDbContext db, InventoryTenantScope scope, long uid, CancellationToken ct)
    {
        var detail = await db.ProductionOutputs.AsNoTracking()
            .Where(x => x.Uid == uid
                && x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode)
            .Select(x => new ProductionOutputDetail
            {
                Uid = x.Uid,
                DocumentNo = x.DocumentNo,
                Status = x.Status,
                WorkOrderId = x.WorkOrderId,
                ProductCode = db.ProductionWorkOrders
                    .Where(w => w.Uid == x.WorkOrderId && w.CompanyCode == scope.CompanyCode && w.BranchCode == scope.BranchCode)
                    .Select(w => w.ProductCode).FirstOrDefault() ?? "",
                ProductDescription = db.ProductionWorkOrders
                    .Where(w => w.Uid == x.WorkOrderId && w.CompanyCode == scope.CompanyCode && w.BranchCode == scope.BranchCode)
                    .Select(w => w.ProductDescription).FirstOrDefault(),
                WorkOrderNo = db.ProductionWorkOrders
                    .Where(w => w.Uid == x.WorkOrderId && w.CompanyCode == scope.CompanyCode && w.BranchCode == scope.BranchCode)
                    .Select(w => w.WorkOrderNo).FirstOrDefault() ?? "",
                RouteStepId = x.RouteStepId,
                WorkOrderOperationId = x.WorkOrderOperationId,
                OperationCode = db.ProductionWorkOrderOperations
                    .Where(o => o.Uid == x.WorkOrderOperationId && o.WorkOrderId == x.WorkOrderId)
                    .Select(o => o.OperationCode).FirstOrDefault() ?? "",
                OperationDescription = db.ProductionWorkOrderOperations
                    .Where(o => o.Uid == x.WorkOrderOperationId && o.WorkOrderId == x.WorkOrderId)
                    .Select(o => o.OperationDescription).FirstOrDefault(),
                WorkOrderStatus = db.ProductionWorkOrders
                    .Where(w => w.Uid == x.WorkOrderId && w.CompanyCode == scope.CompanyCode && w.BranchCode == scope.BranchCode)
                    .Select(w => w.Status).FirstOrDefault(),
                WorkCentreCode = db.ProductionWorkOrderRouteSteps
                    .Where(r => r.Uid == x.RouteStepId && r.WorkOrderId == x.WorkOrderId)
                    .Select(r => r.WorkCentreCode).FirstOrDefault() ?? "",
                PlannedMachineCode = x.PlannedMachineCode,
                PlannedMachineDescription = db.ProductionWorkOrderMachines
                    .Where(m => m.OperationId == x.WorkOrderOperationId
                        && m.MachineCode == x.PlannedMachineCode
                        && db.ProductionWorkOrderOperations.Any(o => o.Uid == m.OperationId && o.WorkOrderId == x.WorkOrderId))
                    .Select(m => m.MachineDescription).FirstOrDefault(),
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
                PostedDate = x.PostedDate,
                PostedBy = x.PostedBy,
                ReversedDate = x.ReversedDate,
                ReversedBy = x.ReversedBy,
                RowVersion = x.RowVersion,
            }).SingleOrDefaultAsync(ct);
        if (detail is null)
            return null;

        var lines = await db.ProductionOutputMaterials.AsNoTracking()
            .Where(x => x.ProductionOutputId == uid
                && x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode)
            .OrderBy(x => x.IsHandoff)
            .ThenBy(x => x.WorkOrderMaterialId)
            .ToListAsync(ct);
        return detail with
        {
            Materials = lines.Select(x => new ProductionOutputMaterialLine
            {
                WorkOrderMaterialId = x.IsHandoff && x.HandoffFromOperationId is long prior
                    ? ProductionProcessHandoff.SyntheticMaterialId(prior)
                    : x.WorkOrderMaterialId ?? 0,
                ComponentCode = x.ComponentCode,
                IssueMethod = x.IssueMethod,
                SupplySource = x.SupplySource,
                TolerancePercent = x.TolerancePercent,
                RequiredQty = x.WoBomRequiredQty,
                WoBomRequiredQty = x.WoBomRequiredQty,
                RequiredUom = x.RequiredUom,
                StandardQty = x.StandardQty,
                ConsumeQty = x.ConsumeQty,
                MaxConsumeQty = ProductionMaterialExecutionCalc.DailyProductionMaxQty(x.StandardQty, x.TolerancePercent),
                VarianceQty = x.VarianceQty,
                VarianceReasonCode = x.VarianceReasonCode,
                VarianceReasonText = x.VarianceReasonText,
                IsHandoff = x.IsHandoff,
                HandoffFromOperationId = x.HandoffFromOperationId,
                IsConsumeEditable = !x.IsHandoff,
            }).ToList(),
        };
    }
}
