using System.Text.Json;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Planning;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionWorkOrderService : IProductionWorkOrderService
{
    private const int ProductCodeMax = 30;
    private const int SourceTypeMax = 30;
    private const int SourceReferenceMax = 80;
    private const int RemarkMax = 1000;
    private const int CancellationReasonMax = 500;
    private const decimal MaxStoredQuantity = 99_999_999_999_999.9999m;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly IRunningNumberService _runningNumbers;
    private readonly BomExplosionService _bomExplosion;
    private readonly IProductionCalendarScheduleDataLoader _calendarLoader;
    private readonly IWorkOrderSnapshotBuilder _snapshotBuilder;
    private readonly IWorkOrderQuantityCalculator _quantities;
    private readonly IWorkOrderScheduleCalculator _scheduler;
    private readonly IWorkOrderReadinessValidator _readiness;
    private readonly ProductionWorkOrderOptions _options;
    private readonly IProductionMaterialReconciliationService _materialReconciliation;

    public ProductionWorkOrderService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        IRunningNumberService runningNumbers,
        BomExplosionService bomExplosion,
        IProductionCalendarScheduleDataLoader calendarLoader,
        IWorkOrderSnapshotBuilder snapshotBuilder,
        IWorkOrderQuantityCalculator quantities,
        IWorkOrderScheduleCalculator scheduler,
        IWorkOrderReadinessValidator readiness,
        Microsoft.Extensions.Options.IOptions<ProductionWorkOrderOptions> options,
        IProductionMaterialReconciliationService? materialReconciliation = null)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _runningNumbers = runningNumbers;
        _bomExplosion = bomExplosion;
        _calendarLoader = calendarLoader;
        _snapshotBuilder = snapshotBuilder;
        _quantities = quantities;
        _scheduler = scheduler;
        _readiness = readiness;
        _options = options.Value;
        _materialReconciliation = materialReconciliation ?? new ProductionMaterialReconciliationService();
    }

    public async Task<IvMasterOperationResult<ProductionWorkOrderListPage>> SearchAsync(
        ProductionWorkOrderListQuery query,
        CancellationToken cancellationToken = default)
    {
        var auth = await AuthorizeAsync(PermissionCodes.Access, requireWriteScope: false, cancellationToken);
        if (auth.Error is not null)
        {
            return IvMasterOperationResult<ProductionWorkOrderListPage>.Fail(auth.Error.Value.Code, auth.Error.Value.Message);
        }

        query ??= new ProductionWorkOrderListQuery();
        var skip = Math.Max(0, query.Skip);
        var take = query.Take <= 0 ? 20 : Math.Min(query.Take, 500);
        var scope = auth.Scope!;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var workOrders = db.ProductionWorkOrders.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode);

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var term = query.SearchText.Trim();
            workOrders = workOrders.Where(x =>
                x.WorkOrderNo.Contains(term)
                || x.ProductCode.Contains(term)
                || (x.ProductDescription != null && x.ProductDescription.Contains(term))
                || (x.SourceReference != null && x.SourceReference.Contains(term)));
        }

        if (!string.IsNullOrWhiteSpace(query.WorkOrderNo))
        {
            var number = query.WorkOrderNo.Trim();
            workOrders = workOrders.Where(x => x.WorkOrderNo.Contains(number));
        }

        if (!string.IsNullOrWhiteSpace(query.ProductCode))
        {
            var product = query.ProductCode.Trim();
            workOrders = workOrders.Where(x => x.ProductCode.Contains(product));
        }

        if (!string.IsNullOrWhiteSpace(query.DefinitionCode))
        {
            var definition = query.DefinitionCode.Trim();
            workOrders = workOrders.Where(x => x.SourceDefinitionCode.Contains(definition));
        }

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var status = Normalize(query.Status);
            workOrders = workOrders.Where(x => x.Status == status);
        }

        if (query.StartDateFrom is DateTime from)
        {
            var date = from.Date;
            workOrders = workOrders.Where(x => x.PlannedStartDateTime >= date);
        }

        if (query.StartDateTo is DateTime to)
        {
            var exclusive = to.Date.AddDays(1);
            workOrders = workOrders.Where(x => x.PlannedStartDateTime < exclusive);
        }

        workOrders = ApplySort(workOrders, query.SortField, query.SortDescending);
        var total = await workOrders.CountAsync(cancellationToken);
        var page = await workOrders.Skip(skip).Take(take).ToListAsync(cancellationToken);

        var ids = page.Select(x => x.Uid).ToList();
        var materialRows = ids.Count == 0
            ? []
            : await db.ProductionWorkOrderMaterials.AsNoTracking()
                .Where(x => ids.Contains(x.WorkOrderId))
                .Select(x => new { x.WorkOrderId, x.RequiredQty, x.IssuedQty, x.ReturnedQty })
                .ToListAsync(cancellationToken);

        var materialProgress = materialRows
            .GroupBy(x => x.WorkOrderId)
            .ToDictionary(
                g => g.Key,
                g => ProductionWorkOrderCalc.ProgressPercent(
                    g.Sum(x => Math.Min(Math.Max(x.IssuedQty - x.ReturnedQty, 0m), x.RequiredQty)),
                    g.Sum(x => x.RequiredQty)));

        var rows = page.Select(x => new ProductionWorkOrderListRow
        {
            WorkOrderNo = x.WorkOrderNo,
            ProductCode = x.ProductCode,
            ProductDescription = x.ProductDescription,
            SourceDefinitionCode = x.SourceDefinitionCode,
            SourceDefinitionName = x.SourceDefinitionName,
            OutputUom = x.OutputUom,
            Status = x.Status,
            BomVersion = x.SourceBomVersion,
            SnapshotRevision = x.SnapshotRevision,
            PlannedQty = x.PlannedQty,
            GoodQty = x.GoodQty,
            RemainingQty = x.RemainingQty,
            PlannedStartDate = x.PlannedStartDateTime,
            PlannedCompletionDate = x.PlannedCompletionDateTime,
            SourceType = x.SourceType,
            SourceReference = x.SourceReference,
            MaterialProgressPercent = materialProgress.GetValueOrDefault(x.Uid),
            ProductionProgressPercent = ProductionWorkOrderCalc.ProgressPercent(x.GoodQty, x.PlannedQty),
            CreatedDate = x.CreatedDate,
            CreatedBy = x.CreatedBy,
            ModifiedDate = x.ModifiedDate,
            ModifiedBy = x.ModifiedBy
        }).ToList();

        return IvMasterOperationResult<ProductionWorkOrderListPage>.Ok(new ProductionWorkOrderListPage
        {
            Rows = rows,
            TotalCount = total
        });
    }

    public async Task<IvMasterOperationResult<ProductionWorkOrderDetail>> GetAsync(
        string workOrderNo,
        CancellationToken cancellationToken = default)
    {
        var auth = await AuthorizeAsync(PermissionCodes.Access, requireWriteScope: false, cancellationToken);
        if (auth.Error is not null)
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(auth.Error.Value.Code, auth.Error.Value.Message);
        }

        var number = Normalize(workOrderNo);
        if (number.Length == 0)
        {
            return ValidationFailure<ProductionWorkOrderDetail>("Work Order number is required.", "WorkOrderNo");
        }

        var scope = auth.Scope!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await LoadAggregateAsync(db, scope, number, tracking: false, cancellationToken);
        if (entity is null)
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.NotFound, "Work Order not found.");
        }

        return IvMasterOperationResult<ProductionWorkOrderDetail>.Ok(MapDetail(entity));
    }

    public async Task<IvMasterOperationResult<ProductionWorkOrderPreview>> ProcessPreviewAsync(
        ProductionWorkOrderDraftRequest request,
        CancellationToken cancellationToken = default)
    {
        request ??= new ProductionWorkOrderDraftRequest();
        var isNew = string.IsNullOrWhiteSpace(request.WorkOrderNo);
        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var auth = await AuthorizeAsync(permission, requireWriteScope: true, cancellationToken);
        if (auth.Error is not null)
        {
            return IvMasterOperationResult<ProductionWorkOrderPreview>.Fail(auth.Error.Value.Code, auth.Error.Value.Message);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        ProductionWorkOrder? existing = null;
        if (!isNew)
        {
            var number = Normalize(request.WorkOrderNo);
            existing = await db.ProductionWorkOrders.AsNoTracking()
                .FirstOrDefaultAsync(x => x.CompanyCode == auth.Scope!.CompanyCode
                    && x.BranchCode == auth.Scope.BranchCode
                    && x.WorkOrderNo == number, cancellationToken);
            if (existing is null)
            {
                return IvMasterOperationResult<ProductionWorkOrderPreview>.Fail(
                    IvMasterErrorCode.NotFound, "Work Order not found.");
            }

            if (!ProductionWorkOrderRules.CanEdit(existing.Status))
            {
                return IvMasterOperationResult<ProductionWorkOrderPreview>.Fail(
                    IvMasterErrorCode.Validation,
                    "Only Draft Work Orders can be reprocessed. Use a Change Order after release.");
            }

            if (existing.SnapshotFormatVersion < ProductionSnapshotFormatVersions.Current)
            {
                return IvMasterOperationResult<ProductionWorkOrderPreview>.Fail(
                    IvMasterErrorCode.Validation,
                    ProductionReadinessErrorCodes.LegacySnapshotRefreshRequired
                    + ": This Work Order uses an older snapshot format. Refresh before releasing or structurally editing.");
            }
        }

        var built = await BuildCurrentSnapshotAsync(
            auth.Scope!, request, existing?.SnapshotRevision ?? 1, explicitScheduleAnchor: null, cancellationToken);
        if (built.WorkOrder is null)
        {
            return IvMasterOperationResult<ProductionWorkOrderPreview>.Fail(
                IvMasterErrorCode.Validation, built.Error ?? "The snapshot could not be built.");
        }

        var detail = MapDetail(built.WorkOrder);
        return IvMasterOperationResult<ProductionWorkOrderPreview>.Ok(new ProductionWorkOrderPreview
        {
            ProductCode = detail.ProductCode,
            ProductDescription = detail.ProductDescription,
            OutputUom = detail.OutputUom,
            SourceDefinitionCode = detail.SourceDefinitionCode,
            SourceDefinitionName = detail.SourceDefinitionName,
            SourceBomHdrId = detail.SourceBomHdrId,
            SourceBomVersion = detail.SourceBomVersion,
            BomBaseQty = detail.BomBaseQty,
            BomBaseUom = detail.BomBaseUom,
            PlannedQty = detail.PlannedQty,
            PlannedStartDate = detail.PlannedStartDate,
            PlannedCompletionDate = detail.PlannedCompletionDate,
            SchedulingDirection = detail.SchedulingDirection,
            ScheduleAnchorDateTime = detail.ScheduleAnchorDateTime
                ?? throw new InvalidOperationException("A current snapshot must have a schedule anchor."),
            SnapshotHash = detail.SnapshotHash,
            SourceProductDefinitionRevisionId = detail.SourceProductDefinitionRevisionId,
            RouteSteps = detail.RouteSteps,
            Materials = detail.Materials,
            Operations = detail.Operations,
            Warnings = built.Warnings
        });
    }

    public async Task<IvMasterOperationResult<ProductionWorkOrderDetail>> SaveDraftAsync(
        ProductionWorkOrderDraftRequest request,
        CancellationToken cancellationToken = default)
    {
        request ??= new ProductionWorkOrderDraftRequest();
        var isNew = string.IsNullOrWhiteSpace(request.WorkOrderNo);
        if (isNew)
        {
            // New drafts always go through the format-3 snapshot builder.
            return await CreateDraftAsync(request, cancellationToken);
        }

        var auth = await AuthorizeAsync(PermissionCodes.Edit, requireWriteScope: true, cancellationToken);
        if (auth.Error is not null)
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(auth.Error.Value.Code, auth.Error.Value.Message);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var number = Normalize(request.WorkOrderNo);
        var entity = await LoadAggregateAsync(db, auth.Scope!, number, tracking: false, cancellationToken);
        if (entity is null)
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(IvMasterErrorCode.NotFound, "Work Order not found.");
        }

        if (!ProductionWorkOrderRules.CanEdit(entity.Status))
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.Validation,
                "Only Draft Work Orders can be edited. Use a Change Order after release.");
        }

        if (entity.SnapshotFormatVersion < ProductionSnapshotFormatVersions.Current)
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.Validation,
                ProductionReadinessErrorCodes.LegacySnapshotRefreshRequired
                + ": This Work Order uses an older snapshot format. Refresh before releasing or structurally editing.");
        }

        return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
            IvMasterErrorCode.Validation,
            "Structural draft rebuild via SaveDraft is no longer supported. "
            + "Use UpdateDraftHeader, Refresh, Change Definition, Select Machine, or Substitute Material.");
    }

    public async Task<IvMasterOperationResult<ProductionWorkOrderDetail>> ReleaseAsync(
        string workOrderNo,
        byte[] rowVersion,
        CancellationToken cancellationToken = default)
    {
        var auth = await AuthorizeAsync(ProductionPermissionCodes.Release, requireWriteScope: true, cancellationToken);
        if (auth.Error is not null)
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(auth.Error.Value.Code, auth.Error.Value.Message);
        }

        var number = Normalize(workOrderNo);
        if (number.Length == 0)
        {
            return ValidationFailure<ProductionWorkOrderDetail>("Work Order number is required.", "WorkOrderNo");
        }

        if (rowVersion is not { Length: > 0 })
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.Concurrency, "The Work Order version is missing. Reload before release.");
        }

        var scope = auth.Scope!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var entity = await LoadAggregateAsync(db, scope, number, tracking: true, cancellationToken);
            if (entity is null)
            {
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                    IvMasterErrorCode.NotFound, "Work Order not found.");
            }

            if (!ProductionWorkOrderRules.CanRelease(entity.Status))
            {
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                    IvMasterErrorCode.Validation, "Only a Draft Work Order can be released.");
            }

            if (entity.SnapshotFormatVersion < ProductionSnapshotFormatVersions.Current)
            {
                throw new WorkOrderCommandException(
                    IvMasterErrorCode.Validation,
                    ProductionReadinessErrorCodes.LegacySnapshotRefreshRequired
                    + ": This Work Order uses an older snapshot format. Refresh before releasing or structurally editing.");
            }

            db.Entry(entity).Property(x => x.RowVersion).OriginalValue = rowVersion;
            await ValidateAndApplyCurrentReleaseAsync(db, entity, scope, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Ok(MapDetail(entity));
        }
        catch (WorkOrderSchedulingLockException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.Validation,
                ProductionReadinessErrorCodes.SchedulingSourceBusy + ": " + ex.Message);
        }
        catch (WorkOrderCommandException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(ex.Code, ex.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.Concurrency,
                "This Work Order was changed or released by another user. Reload the latest version.");
        }
    }

    public async Task<IvMasterOperationResult<ProductionWorkOrderDetail>> CancelDraftAsync(
        string workOrderNo,
        byte[] rowVersion,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var auth = await AuthorizeAsync(PermissionCodes.Cancel, requireWriteScope: true, cancellationToken);
        if (auth.Error is not null)
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(auth.Error.Value.Code, auth.Error.Value.Message);
        }

        var number = Normalize(workOrderNo);
        var cleanReason = (reason ?? string.Empty).Trim();
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (number.Length == 0) errors["WorkOrderNo"] = "Work Order number is required.";
        if (cleanReason.Length == 0) errors["CancellationReason"] = "Cancellation reason is required.";
        if (cleanReason.Length > CancellationReasonMax)
            errors["CancellationReason"] = $"Cancellation reason must be {CancellationReasonMax} characters or fewer.";
        if (errors.Count > 0)
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.Validation, "Correct the highlighted fields.", errors);
        }

        if (rowVersion is not { Length: > 0 })
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.Concurrency, "The Work Order version is missing. Reload before cancellation.");
        }

        var scope = auth.Scope!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var entity = await LoadAggregateAsync(db, scope, number, tracking: true, cancellationToken);
            if (entity is null)
            {
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                    IvMasterErrorCode.NotFound, "Work Order not found.");
            }

            if (!ProductionWorkOrderRules.CanCancelDraft(entity.Status))
            {
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                    IvMasterErrorCode.Validation,
                    "Only a Draft Work Order can be cancelled. Released work requires controlled execution reversal/change processing.");
            }

            db.Entry(entity).Property(x => x.RowVersion).OriginalValue = rowVersion;
            var now = DateTime.UtcNow;
            entity.Status = ProductionWorkOrderStatuses.Cancelled;
            entity.CancelledDate = now;
            entity.CancelledBy = scope.UserId;
            entity.CancellationReason = cleanReason;
            entity.ModifiedDate = now;
            entity.ModifiedBy = scope.UserId;
            entity.AuditEvents.Add(new ProductionAuditEvent
            {
                WorkOrder = entity,
                EventType = ProductionAuditEventTypes.Cancelled,
                FromStatus = ProductionWorkOrderStatuses.Draft,
                ToStatus = ProductionWorkOrderStatuses.Cancelled,
                SnapshotRevision = entity.SnapshotRevision,
                Reason = cleanReason,
                OccurredDate = now,
                ActorUserId = scope.UserId
            });

            TouchSqliteRowVersions(db, entity);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Ok(MapDetail(entity));
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.Concurrency,
                "This Work Order was changed by another user. Reload the latest version.");
        }
    }

    private async Task<IvMasterOperationResult<PreparedSnapshot>> BuildSnapshotAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        ProductionWorkOrderDraftRequest request,
        CancellationToken cancellationToken)
    {
        var errors = ValidateAndNormalize(request, out var normalized);
        if (errors.Count > 0)
        {
            return IvMasterOperationResult<PreparedSnapshot>.Fail(
                IvMasterErrorCode.Validation, "Correct the highlighted fields.", errors);
        }

        var product = await db.IvStockMasters.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.ICode == normalized.ProductCode)
            .Select(x => new { x.ICode, x.IDesc, x.StdUom, x.IsActive })
            .FirstOrDefaultAsync(cancellationToken);
        if (product is null)
        {
            return ValidationFailure<PreparedSnapshot>(
                $"Product {normalized.ProductCode} does not exist in Stock Master.", "ProductCode");
        }

        if (!product.IsActive)
        {
            return ValidationFailure<PreparedSnapshot>(
                $"Product {normalized.ProductCode} is inactive.", "ProductCode");
        }

        IvMasterOperationResult<BomExplosionResult> explosion;
        try
        {
            explosion = await _bomExplosion.ExplodeInContextAsync(db, scope.CompanyCode,
                new BomExplosionRequest
                {
                    ProdCode = normalized.ProductCode,
                    DefinitionCode = normalized.DefinitionCode,
                    Quantity = normalized.PlannedQty,
                    Mode = BomExplosionMode.ProductionIssueRequirement
                }, cancellationToken);
        }
        catch (OverflowException)
        {
            return ValidationFailure<PreparedSnapshot>(
                "The planned quantity produces a component requirement outside the supported range.",
                "PlannedQty");
        }
        if (!explosion.Succeeded || explosion.Data is null)
        {
            return CopyFailure<BomExplosionResult, PreparedSnapshot>(explosion);
        }

        var exploded = explosion.Data;
        if (exploded.RootBomHdrId is not long rootBomId
            || exploded.RootBomVersion is not int rootBomVersion
            || exploded.RootBaseQty is not decimal rootBaseQty)
        {
            return IvMasterOperationResult<PreparedSnapshot>.Fail(
                IvMasterErrorCode.Validation, "The active BOM did not return a complete revision identity.");
        }

        var sourceLineIds = exploded.Nodes
            .Where(x => x.SourceLineUid is not null)
            .Select(x => x.SourceLineUid!.Value)
            .Distinct()
            .ToList();
        var sourceLines = sourceLineIds.Count == 0
            ? new Dictionary<long, SourceBomLineSnapshot>()
            : await db.PrDefBOMs.AsNoTracking()
                .Where(x => sourceLineIds.Contains(x.Uid))
                .Select(x => new SourceBomLineSnapshot(
                    x.Uid,
                    x.BomHdrId,
                    x.Header!.Version,
                    x.Header.BaseQty,
                    x.Header.BaseUom,
                    x.ProdCode,
                    x.IName,
                    x.StdQty,
                    x.StdUom,
                    x.ScrapPercent,
                    x.Tolerance,
                    x.Warehouse,
                    x.LocationCode))
                .ToDictionaryAsync(x => x.LineId, cancellationToken);

        if (sourceLines.Count != sourceLineIds.Count)
        {
            return IvMasterOperationResult<PreparedSnapshot>.Fail(
                IvMasterErrorCode.Concurrency,
                "The BOM changed while the Work Order snapshot was being prepared. Process it again.");
        }

        var oversizedRequirement = exploded.Nodes.FirstOrDefault(x => x.ExtendedQty > MaxStoredQuantity);
        if (oversizedRequirement is not null)
        {
            return ValidationFailure<PreparedSnapshot>(
                $"Component {oversizedRequirement.ItemCode} requirement exceeds the supported quantity range.",
                "PlannedQty");
        }

        var materials = exploded.Nodes.Select((node, index) =>
        {
            var source = node.SourceLineUid is long sourceId
                ? sourceLines.GetValueOrDefault(sourceId)
                : null;
            return new ProductionWorkOrderMaterialVm
            {
                LineNo = index + 1,
                SourceBomHdrId = source?.HeaderId,
                SourceBomVersion = source?.HeaderVersion,
                SourceBomLineId = node.SourceLineUid,
                ParentProductCode = source?.ParentProductCode ?? node.ParentItemCode,
                BomPath = node.Path,
                ComponentCode = Normalize(node.ItemCode),
                ComponentDescription = TrimTo(source?.ComponentDescription ?? node.ItemDesc, 200),
                MfgType = Normalize(node.MfgType),
                ComponentQtyPerParent = source?.ComponentQty ?? node.QtyPerParent,
                BomOutputQty = source?.BomOutputQty ?? 0m,
                BomOutputUom = TrimTo(source?.BomOutputUom, 10),
                ScrapPercent = source?.ScrapPercent ?? node.ScrapPercent,
                Tolerance = source?.Tolerance ?? 0m,
                SupplySource = TrimTo(node.SupplySource, 30),
                ComponentDefinitionCode = string.IsNullOrWhiteSpace(node.ComponentDefinitionCode)
                    ? null
                    : PrProductDefinitionCodes.Normalize(node.ComponentDefinitionCode),
                RequiredQty = node.ExtendedQty,
                RequiredUom = TrimTo(source?.ComponentUom ?? node.Uom, 10),
                WarehouseCode = TrimTo(source?.WarehouseCode ?? node.Warehouse, 20),
                LocationCode = TrimTo(source?.LocationCode, 10),
                OpenRequirementQty = node.ExtendedQty
            };
        }).ToList();

        var routingBuilt = await BuildRoutingOperationsAsync(
            db, scope.CompanyCode, normalized.ProductCode, rootBomId, rootBaseQty, normalized.PlannedQty,
            normalized.PlannedStartDate, normalized.PlannedCompletionDate, cancellationToken);
        if (!routingBuilt.Succeeded)
            return IvMasterOperationResult<PreparedSnapshot>.Fail(routingBuilt.Code, routingBuilt.Message);

        IReadOnlyList<ProductionWorkOrderOperationVm> operations;
        var plannedStart = normalized.PlannedStartDate.Date;
        var plannedCompletion = normalized.PlannedCompletionDate.Date;
        var warnings = new List<string>(routingBuilt.Warnings);
        try
        {
            var scheduled = await ProductionWorkOrderCalendarPlanner.ApplyAsync(
                _calendarLoader,
                scope.CompanyCode,
                routingBuilt.Operations,
                DateOnly.FromDateTime(normalized.PlannedStartDate),
                DateOnly.FromDateTime(normalized.PlannedCompletionDate),
                normalized.SchedulingDirection,
                cancellationToken);
            operations = scheduled.Operations;
            plannedStart = scheduled.HeaderStart.Date;
            plannedCompletion = scheduled.HeaderCompletion.Date;
        }
        catch (ScheduleFailureException ex)
        {
            return IvMasterOperationResult<PreparedSnapshot>.Fail(IvMasterErrorCode.Validation, ex.Message);
        }

        if (materials.Count == 0)
        {
            warnings.Add("The selected BOM has no direct production issue requirements.");
        }

        var outputUom = TrimTo(product.StdUom ?? exploded.RootBaseUom, 10);
        // Legacy ProductionSnapshotHasher still fingerprints DefinitionEffectiveDate (date column).
        // Stamp the same UTC calendar day that ApplyPreparedSnapshot writes onto the entity.
        var definitionEffectiveDate = DateTime.UtcNow.Date;
        var hashLines = materials.Select(ToHashLine).ToList();
        var hashOps = operations.Select(ToHashOperation).ToList();
        var hash = ProductionSnapshotHasher.Compute(
            normalized.ProductCode,
            product.IDesc,
            outputUom,
            rootBomId,
            rootBomVersion,
            rootBaseQty,
            exploded.RootBaseUom,
            normalized.PlannedQty,
            definitionEffectiveDate,
            plannedStart,
            plannedCompletion,
            normalized.SchedulingDirection,
            normalized.SourceType,
            normalized.SourceReference,
            normalized.Remark,
            hashLines,
            hashOps);

        var preview = new ProductionWorkOrderPreview
        {
            ProductCode = normalized.ProductCode,
            ProductDescription = product.IDesc,
            OutputUom = outputUom,
            SourceBomHdrId = rootBomId,
            SourceBomVersion = rootBomVersion,
            BomBaseQty = rootBaseQty,
            BomBaseUom = exploded.RootBaseUom,
            PlannedQty = normalized.PlannedQty,
            SourceDefinitionCode = normalized.DefinitionCode,
            SnapshotHash = hash,
            Materials = materials,
            Operations = operations,
            Warnings = warnings
        };

        // Apply scheduled header dates onto normalized request for persist
        normalized = normalized with
        {
            PlannedStartDate = plannedStart,
            PlannedCompletionDate = plannedCompletion
        };

        return IvMasterOperationResult<PreparedSnapshot>.Ok(new PreparedSnapshot(preview, normalized));
    }

    private static Dictionary<string, string> ValidateAndNormalize(
        ProductionWorkOrderDraftRequest request,
        out NormalizedDraft normalized)
    {
        var productCode = Normalize(request.ProductCode);
        var definitionCode = string.IsNullOrWhiteSpace(request.DefinitionCode)
            ? PrProductDefinitionCodes.Standard
            : PrProductDefinitionCodes.Normalize(request.DefinitionCode);
        var plannedQty = IvQty.Round(request.PlannedQty);
        var direction = Normalize(request.SchedulingDirection);
        var sourceType = Normalize(request.SourceType);
        var sourceReference = NullIfEmpty(request.SourceReference);
        var remark = NullIfEmpty(request.Remark);
        var today = DateTime.UtcNow.Date;
        var startDate = request.PlannedStartDate == default ? today : request.PlannedStartDate.Date;
        var completionDate = request.PlannedCompletionDate == default ? startDate : request.PlannedCompletionDate.Date;

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (productCode.Length == 0) errors["ProductCode"] = "Product code is required.";
        else if (productCode.Length > ProductCodeMax)
            errors["ProductCode"] = $"Product code must be {ProductCodeMax} characters or fewer.";
        if (definitionCode.Length == 0 || !PrProductDefinitionCodes.IsValidFormat(definitionCode))
            errors["DefinitionCode"] = "A valid Product Definition code is required.";
        if (plannedQty <= 0m) errors["PlannedQty"] = "Planned quantity must be at least 0.0001.";
        else if (plannedQty > MaxStoredQuantity)
            errors["PlannedQty"] = "Planned quantity exceeds the supported quantity range.";
        if (completionDate < startDate)
            errors["PlannedCompletionDate"] = "Planned completion date cannot be before the start date.";
        if (!ProductionSchedulingDirections.IsKnown(direction))
            errors["SchedulingDirection"] = "Scheduling direction must be Forward or Backward.";
        if (sourceType.Length == 0) errors["SourceType"] = "Source type is required.";
        else if (sourceType.Length > SourceTypeMax)
            errors["SourceType"] = $"Source type must be {SourceTypeMax} characters or fewer.";
        else if (!ProductionSourceTypes.IsKnown(sourceType))
            errors["SourceType"] = "Source type is not recognized.";
        else if (sourceType != ProductionSourceTypes.Manual)
            errors["SourceType"] = "Only Manual Work Order creation is available in Phase 1.";
        if (sourceReference?.Length > SourceReferenceMax)
            errors["SourceReference"] = $"Source reference must be {SourceReferenceMax} characters or fewer.";
        if (remark?.Length > RemarkMax)
            errors["Remark"] = $"Remark must be {RemarkMax} characters or fewer.";

        normalized = new NormalizedDraft(
            productCode,
            definitionCode,
            plannedQty,
            startDate,
            completionDate,
            direction,
            sourceType,
            sourceReference,
            remark);
        return errors;
    }

    private static void ApplyPreparedSnapshot(
        ProductionWorkOrder entity,
        PreparedSnapshot prepared,
        InventoryTenantScope scope,
        DateTime now,
        bool isNew)
    {
        var preview = prepared.Preview;
        var request = prepared.Request;
        entity.LocationCode = scope.LocationCode;
        entity.ProductCode = preview.ProductCode;
        entity.ProductDescription = preview.ProductDescription;
        entity.OutputUom = preview.OutputUom;
        entity.SourceBomHdrId = preview.SourceBomHdrId;
        entity.SourceBomVersion = preview.SourceBomVersion;
        entity.BomBaseQty = preview.BomBaseQty;
        entity.BomBaseUom = preview.BomBaseUom;
        entity.DefinitionEffectiveDate = DateTime.UtcNow.Date;
        entity.SourceDefinitionCode = preview.SourceDefinitionCode;
        entity.SourceDefinitionName = preview.SourceDefinitionName;
        entity.SnapshotHash = preview.SnapshotHash;
        entity.PlannedQty = preview.PlannedQty;
        entity.GoodQty = 0m;
        entity.ScrapQty = 0m;
        entity.RejectQty = 0m;
        entity.HoldQty = 0m;
        entity.ApprovedVarianceQty = 0m;
        entity.RemainingQty = preview.PlannedQty;
        entity.PlannedStartDateTime = request.PlannedStartDate;
        entity.PlannedCompletionDateTime = request.PlannedCompletionDate;
        entity.SchedulingDirection = request.SchedulingDirection;
        entity.SourceType = request.SourceType;
        entity.SourceReference = request.SourceReference;
        entity.Remark = request.Remark;
        entity.Status = ProductionWorkOrderStatuses.Draft;
        entity.ModifiedDate = now;
        entity.ModifiedBy = scope.UserId;
        if (isNew)
        {
            entity.CreatedDate = now;
            entity.CreatedBy = scope.UserId;
        }
    }

    private static void AddSnapshotChildren(
        ProductionWorkOrder entity,
        ProductionWorkOrderPreview preview,
        InventoryTenantScope scope,
        DateTime now,
        AppDbContext db)
    {
        foreach (var row in preview.Materials)
        {
            var material = new ProductionWorkOrderMaterial
            {
                WorkOrder = entity,
                LineNo = row.LineNo,
                SourceBomHdrId = row.SourceBomHdrId,
                SourceBomVersion = row.SourceBomVersion,
                SourceBomLineId = row.SourceBomLineId,
                ParentProductCode = row.ParentProductCode,
                BomPath = row.BomPath,
                ComponentCode = row.ComponentCode,
                ComponentDescription = row.ComponentDescription,
                MfgType = row.MfgType,
                ComponentQtyPerParent = row.ComponentQtyPerParent,
                BomOutputQty = row.BomOutputQty,
                BomOutputUom = row.BomOutputUom,
                ScrapPercent = row.ScrapPercent,
                Tolerance = row.Tolerance,
                SupplySource = row.SupplySource ?? string.Empty,
                ComponentDefinitionCode = row.ComponentDefinitionCode,
                RequiredQty = row.RequiredQty,
                RequiredUom = row.RequiredUom,
                WarehouseCode = row.WarehouseCode,
                LocationCode = row.LocationCode,
                CreatedDate = now,
                CreatedBy = scope.UserId,
                ModifiedDate = now,
                ModifiedBy = scope.UserId
            };
            if (IsSqlite(db)) material.RowVersion = Guid.NewGuid().ToByteArray();
            entity.Materials.Add(material);
        }

        foreach (var row in preview.Operations)
        {
            var operation = new ProductionWorkOrderOperation
            {
                WorkOrder = entity,
                SequenceNo = row.SequenceNo,
                WorkCentreCode = row.WorkCentreCode,
                WorkCentreDescription = row.WorkCentreDescription,
                OperationCode = row.OperationCode,
                OperationDescription = row.OperationDescription,
                IsFinalOperation = row.IsFinalOperation,
                PlannedStartDateTime = row.PlannedStartDate,
                PlannedCompletionDateTime = row.PlannedCompletionDate,
                PlannedQty = row.PlannedQty,
                SetupLossQty = row.SetupLossQty,
                OperationLossQty = row.OperationLossQty,
                RemainingQty = row.PlannedQty,
                CreatedDate = now,
                CreatedBy = scope.UserId,
                ModifiedDate = now,
                ModifiedBy = scope.UserId
            };
            if (IsSqlite(db)) operation.RowVersion = Guid.NewGuid().ToByteArray();

            var resSeq = 1;
            foreach (var res in row.Resources)
            {
                operation.Resources.Add(new ProductionWorkOrderResource
                {
                    SequenceNo = res.SequenceNo == 0 ? resSeq++ : res.SequenceNo,
                    ResourceType = res.ResourceType,
                    ResourceCode = res.ResourceCode,
                    ResourceDescription = res.ResourceDescription,
                    PlannedUnits = res.PlannedUnits,
                    SetupMinutes = res.SetupMinutes,
                    RunMinutes = res.RunMinutes,
                    QueueMinutes = res.QueueMinutes,
                    Rate = res.Rate,
                    PlannedAmount = res.PlannedAmount,
                    CreatedDate = now,
                    CreatedBy = scope.UserId
                });
            }

            entity.Operations.Add(operation);
        }
    }

    private static ProductionWorkOrderDetail MapDetail(ProductionWorkOrder entity) => new()
    {
        Uid = entity.Uid,
        WorkOrderNo = entity.WorkOrderNo,
        CompanyCode = entity.CompanyCode,
        BranchCode = entity.BranchCode,
        LocationCode = entity.LocationCode,
        SnapshotRevision = entity.SnapshotRevision,
        SnapshotHash = entity.SnapshotHash,
        SourceDefinitionCode = entity.SourceDefinitionCode,
        SourceDefinitionName = entity.SourceDefinitionName,
        ProductCode = entity.ProductCode,
        ProductDescription = entity.ProductDescription,
        OutputUom = entity.OutputUom,
        SourceBomHdrId = entity.SourceBomHdrId,
        SourceBomVersion = entity.SourceBomVersion,
        BomBaseQty = entity.BomBaseQty,
        BomBaseUom = entity.BomBaseUom,
        PlannedQty = entity.PlannedQty,
        GoodQty = entity.GoodQty,
        ScrapQty = entity.ScrapQty,
        RejectQty = entity.RejectQty,
        HoldQty = entity.HoldQty,
        ApprovedVarianceQty = entity.ApprovedVarianceQty,
        RemainingQty = entity.RemainingQty,
        PlannedStartDate = entity.PlannedStartDateTime,
        PlannedCompletionDate = entity.PlannedCompletionDateTime,
        SchedulingDirection = entity.SchedulingDirection,
        Status = entity.Status,
        SourceType = entity.SourceType,
        SourceReference = entity.SourceReference,
        Remark = entity.Remark,
        ReleasedDate = entity.ReleasedDate,
        ReleasedBy = entity.ReleasedBy,
        CancelledDate = entity.CancelledDate,
        CancelledBy = entity.CancelledBy,
        CancellationReason = entity.CancellationReason,
        CreatedDate = entity.CreatedDate,
        CreatedBy = entity.CreatedBy,
        ModifiedDate = entity.ModifiedDate,
        ModifiedBy = entity.ModifiedBy,
        RowVersion = entity.RowVersion.ToArray(),
        SnapshotFormatVersion = entity.SnapshotFormatVersion,
        IsLegacySnapshot = entity.IsLegacySnapshot,
        SourceProductDefinitionRevisionId = entity.SourceProductDefinitionRevisionId,
        DefinitionSourceHash = entity.DefinitionSourceHash,
        ScheduleAnchorDateTime = entity.ScheduleAnchorDateTime,
        RouteSteps = entity.RouteSteps
            .OrderBy(x => x.StageSequence)
            .ThenBy(x => x.WorkCentreCode)
            .Select(MapRouteStep)
            .ToList(),
        Materials = entity.Materials
            .OrderBy(x => x.LineNo)
            .Select(MapMaterial)
            .ToList(),
        Operations = entity.Operations
            .OrderBy(x => x.SequenceNo == 0 ? int.MaxValue : x.SequenceNo)
            .ThenBy(x => x.ProcessSequence)
            .ThenBy(x => x.OperationCode)
            .Select(MapOperation)
            .ToList(),
        AuditEvents = entity.AuditEvents
            .OrderByDescending(x => x.OccurredDate)
            .ThenByDescending(x => x.Uid)
            .Select(x => new ProductionAuditEventVm
            {
                Uid = x.Uid,
                EventType = x.EventType,
                FromStatus = x.FromStatus,
                ToStatus = x.ToStatus,
                SnapshotRevision = x.SnapshotRevision,
                Reason = x.Reason,
                OccurredDate = x.OccurredDate,
                ActorUserId = x.ActorUserId
            }).ToList()
    };

    private static ProductionWorkOrderMaterialVm MapMaterial(ProductionWorkOrderMaterial row) => new()
    {
        Uid = row.Uid,
        LineNo = row.LineNo,
        SourceBomHdrId = row.SourceBomHdrId,
        SourceBomVersion = row.SourceBomVersion,
        SourceBomLineId = row.SourceBomLineId,
        ParentProductCode = row.ParentProductCode,
        BomPath = row.BomPath,
        ComponentCode = row.ComponentCode,
        ComponentDescription = row.ComponentDescription,
        MfgType = row.MfgType,
        ComponentQtyPerParent = row.ComponentQtyPerParent,
        BomOutputQty = row.BomOutputQty,
        BomOutputUom = row.BomOutputUom,
        ScrapPercent = row.ScrapPercent,
        Tolerance = row.Tolerance,
        RequiredQty = row.RequiredQty,
        RequiredUom = row.RequiredUom,
        MaterialSequence = row.MaterialSequence,
        WorkOrderOperationId = row.WorkOrderOperationId,
        ConsumingOperationCode = row.WorkOrderOperation?.OperationCode,
        AlternateGroupCode = row.AlternateGroupCode,
        IssueMethod = row.IssueMethod,
        SupplySource = row.SupplySource,
        ComponentDefinitionCode = row.ComponentDefinitionCode,
        RequiredBaseQty = row.RequiredBaseQty,
        BaseUom = row.BaseUom,
        StandardUom = row.StandardUom,
        WarehouseCode = row.WarehouseCode,
        LocationCode = row.LocationCode,
        ReservedQty = row.ReservedQty,
        PickedQty = row.PickedQty,
        IssuedQty = row.IssuedQty,
        ReturnedQty = row.ReturnedQty,
        ConsumedQty = row.ConsumedQty,
        VarianceQty = row.VarianceQty,
        OpenRequirementQty = ProductionWorkOrderCalc.OpenRequirementQty(
            row.RequiredQty, row.IssuedQty, row.ReturnedQty)
    };

    private static ProductionWorkOrderOperationVm MapOperation(ProductionWorkOrderOperation row) => new()
    {
        Uid = row.Uid,
        SequenceNo = row.SequenceNo,
        WorkCentreCode = row.WorkCentreCode,
        WorkCentreDescription = row.WorkCentreDescription,
        OperationCode = row.OperationCode,
        OperationDescription = row.OperationDescription,
        IsFinalOperation = row.IsFinalOperation,
        PlannedStartDate = row.PlannedStartDateTime,
        PlannedCompletionDate = row.PlannedCompletionDateTime,
        PlannedQty = row.PlannedQty,
        SetupLossQty = row.SetupLossQty,
        OperationLossQty = row.OperationLossQty,
        ProcessSequence = row.ProcessSequence,
        ProcessType = row.ProcessType,
        StandardDurationMinutes = row.StandardDurationMinutes,
        PlannedInputQty = row.PlannedInputQty,
        PlannedInputUom = row.PlannedInputUom,
        PlannedOutputQty = row.PlannedOutputQty,
        PlannedOutputUom = row.PlannedOutputUom,
        ScheduleSourceHash = row.ScheduleSourceHash,
        Machines = row.Machines
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.MachineCode)
            .Select(x => MapMachine(x, row.OperationCode))
            .ToList(),
        Labours = row.Labours
            .OrderBy(x => x.LabourCode)
            .Select(x => MapLabour(x, row.OperationCode, null))
            .ToList(),
        InputQty = row.InputQty,
        ProcessedQty = row.ProcessedQty,
        GoodQty = row.GoodQty,
        ScrapQty = row.ScrapQty,
        RejectQty = row.RejectQty,
        HoldQty = row.HoldQty,
        ReworkQty = row.ReworkQty,
        TransferredQty = row.TransferredQty,
        RemainingQty = row.RemainingQty,
        Resources = row.Resources.OrderBy(x => x.SequenceNo).Select(x => new ProductionWorkOrderResourceVm
        {
            Uid = x.Uid,
            SequenceNo = x.SequenceNo,
            ResourceType = x.ResourceType,
            ResourceCode = x.ResourceCode,
            ResourceDescription = x.ResourceDescription,
            PlannedUnits = x.PlannedUnits,
            SetupMinutes = x.SetupMinutes,
            RunMinutes = x.RunMinutes,
            QueueMinutes = x.QueueMinutes,
            Rate = x.Rate,
            PlannedAmount = x.PlannedAmount
        }).ToList()
    };

    private static ProductionWorkOrderRouteStepVm MapRouteStep(ProductionWorkOrderRouteStep row) => new()
    {
        Uid = row.Uid,
        StageSequence = row.StageSequence,
        WorkCentreCode = row.WorkCentreCode,
        OutputItemCode = row.OutputItemCode,
        PlannedQty = row.PlannedQty,
        OutputUom = row.OutputUom,
        PlannedStartDateTime = row.PlannedStartDateTime,
        PlannedCompletionDateTime = row.PlannedCompletionDateTime,
        Operations = row.Operations
            .OrderBy(x => x.ProcessSequence)
            .ThenBy(x => x.OperationCode)
            .Select(MapOperation)
            .ToList()
    };

    private static ProductionWorkOrderMachineVm MapMachine(
        ProductionWorkOrderMachine row,
        string? operationCode = null) => new()
    {
        Uid = row.Uid,
        WorkOrderOperationId = row.OperationId,
        OperationCode = operationCode ?? row.Operation?.OperationCode,
        Priority = row.Priority,
        MachineCode = row.MachineCode,
        MachineDescription = row.MachineDescription,
        IsDefault = row.IsDefault,
        IsSelected = row.IsSelected,
        ParallelMachineCount = row.ParallelMachineCount,
        CycleQuantityMode = row.CycleQuantityMode,
        CycleSeconds = row.CycleSeconds,
        OutputPerCycle = row.OutputPerCycle,
        OutputPerCycleUom = row.OutputPerCycleUom,
        RequiredMachineOutputQty = row.RequiredMachineOutputQty,
        RequiredMachineOutputUom = row.RequiredMachineOutputUom,
        PlannedCycleCount = row.PlannedCycleCount,
        PlannedCycleSlots = row.PlannedCycleSlots,
        PlannedRunMinutes = row.PlannedRunMinutes,
        SetupSeconds = row.SetupSeconds,
        ConversionSeconds = row.ConversionSeconds,
        QueueSeconds = row.QueueSeconds,
        PlannedStartDateTime = row.PlannedStartDateTime,
        PlannedCompletionDateTime = row.PlannedCompletionDateTime,
        ScheduleSourceHash = row.ScheduleSourceHash,
        Labours = row.Labours
            .OrderBy(x => x.LabourCode)
            .Select(x => MapLabour(
                x,
                operationCode ?? row.Operation?.OperationCode,
                row.MachineCode))
            .ToList()
    };

    private static ProductionWorkOrderLabourVm MapLabour(
        ProductionWorkOrderLabour row,
        string? operationCode,
        string? machineCode) => new()
    {
        Uid = row.Uid,
        OperationCode = operationCode,
        MachineCode = machineCode,
        LabourCode = row.LabourCode,
        LabourDescription = row.LabourDescription,
        PlannedUnits = row.PlannedUnits,
        PlannedMinutes = row.PlannedMinutes,
        RateBasis = row.RateBasis,
        Rate = row.Rate,
        ContributesToPlan = row.ContributesToPlan,
        PlannedAmount = row.PlannedAmount
    };

    private static ProductionSnapshotHashLine ToHashLine(ProductionWorkOrderMaterialVm x) => new(
        x.LineNo,
        x.SourceBomHdrId,
        x.SourceBomVersion,
        x.SourceBomLineId,
        x.ParentProductCode,
        x.BomPath,
        x.ComponentCode,
        x.ComponentDescription,
        x.MfgType,
        x.ComponentQtyPerParent,
        x.BomOutputQty,
        x.BomOutputUom,
        x.ScrapPercent,
        x.Tolerance,
        x.RequiredQty,
        x.RequiredUom,
        x.WarehouseCode,
        x.LocationCode);

    private static ProductionSnapshotHashOperation ToHashOperation(ProductionWorkOrderOperationVm x) => new(
        x.SequenceNo,
        x.WorkCentreCode,
        x.WorkCentreDescription,
        x.OperationCode,
        x.OperationDescription,
        x.IsFinalOperation,
        x.PlannedStartDate,
        x.PlannedCompletionDate,
        x.PlannedQty,
        x.SetupLossQty,
        x.OperationLossQty,
        x.Resources.Select(r => new ProductionSnapshotHashResource(
            r.SequenceNo,
            r.ResourceType,
            r.ResourceCode,
            r.ResourceDescription,
            r.PlannedUnits,
            r.SetupMinutes,
            r.RunMinutes,
            r.QueueMinutes,
            r.Rate,
            r.PlannedAmount)).ToList());

    private sealed record RoutingBuildResult(
        bool Succeeded,
        IvMasterErrorCode Code,
        string Message,
        IReadOnlyList<ProductionWorkOrderOperationVm> Operations,
        IReadOnlyList<string> Warnings);

    /// <summary>
    /// Builds the WO operation/resource snapshot from the same Product Definition revision
    /// selected by BOM explosion. Legacy PrDef* routing is a read-only fallback for records
    /// not yet migrated to version-owned operations.
    /// </summary>
    private static async Task<RoutingBuildResult> BuildRoutingOperationsAsync(
        AppDbContext db,
        string companyCode,
        string productCode,
        long bomHeaderId,
        decimal bomBaseQty,
        decimal plannedQty,
        DateTime plannedStart,
        DateTime plannedCompletion,
        CancellationToken ct)
    {
        var warnings = new List<string>();
        var versioned = await db.PrBomOperations.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode && x.BomHdrId == bomHeaderId)
            .Include(x => x.Machines)
                .ThenInclude(x => x.Labours)
            .OrderBy(x => x.CentralSequence)
            .ThenBy(x => x.ProcessSequence)
            .ThenBy(x => x.OperationCode)
            .AsSplitQuery()
            .ToListAsync(ct);

        if (versioned.Count > 0)
        {
            var wcCodesV = versioned.Select(x => x.WorkCentreCode).Distinct().ToList();
            var processCodesV = versioned.Select(x => x.OperationCode).Distinct().ToList();
            var wcDescriptions = await db.PrWorkCentres.AsNoTracking()
                .Where(x => x.CompCode == companyCode && wcCodesV.Contains(x.WrkCtrCd))
                .ToDictionaryAsync(x => x.WrkCtrCd, x => x.WrkCtrDes, StringComparer.OrdinalIgnoreCase, ct);
            var processDescriptions = (await db.PrProcesses.AsNoTracking()
                    .Where(x => x.CompCode == companyCode && processCodesV.Contains(x.ProcessCd))
                    .ToListAsync(ct))
                .GroupBy(x => $"{x.WorkCentre}|{x.ProcessCd}", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First().ProcessDes, StringComparer.OrdinalIgnoreCase);

            var snapshot = new List<ProductionWorkOrderOperationVm>();
            var operationSequence = 1;
            foreach (var operation in versioned)
            {
                var selectedMachines = operation.Machines
                    .Where(x => x.IsPrimary)
                    .OrderBy(x => x.ResourceSequence)
                    .ThenBy(x => x.MachineCode)
                    .ToList();
                if (selectedMachines.Count == 0)
                {
                    return new RoutingBuildResult(false, IvMasterErrorCode.Validation,
                        $"Product Definition V route {operation.OperationCode} has no primary machine.", [], warnings);
                }

                var centreQty = bomBaseQty <= 0m
                    ? plannedQty
                    : plannedQty * operation.OutputBaseQty / bomBaseQty;
                var effectiveMachineCount = selectedMachines.Sum(x => Math.Max(1, x.ParallelMachineCount));
                var resources = new List<ProductionWorkOrderResourceVm>();
                var resourceSequence = 1;
                foreach (var machine in selectedMachines)
                {
                    var runSeconds = operation.OutputBaseQty <= 0m
                        ? 0m
                        : machine.CycleSeconds / operation.OutputBaseQty
                          * centreQty / effectiveMachineCount;
                    resources.Add(new ProductionWorkOrderResourceVm
                    {
                        SequenceNo = resourceSequence++,
                        ResourceType = "MACHINE",
                        ResourceCode = machine.MachineCode,
                        ResourceDescription = TrimTo(machine.MachineDescription, 200),
                        PlannedUnits = Math.Max(1, machine.ParallelMachineCount),
                        SetupMinutes = decimal.Round(
                            (machine.SetupSeconds + machine.ConversionSeconds) / 60m, 4,
                            MidpointRounding.AwayFromZero),
                        RunMinutes = decimal.Round(runSeconds / 60m, 4, MidpointRounding.AwayFromZero),
                        QueueMinutes = decimal.Round(machine.QueueSeconds / 60m, 4, MidpointRounding.AwayFromZero),
                        Rate = 0m,
                        PlannedAmount = 0m
                    });

                    foreach (var labour in machine.Labours.OrderBy(x => x.LabourCode))
                    {
                        resources.Add(new ProductionWorkOrderResourceVm
                        {
                            SequenceNo = resourceSequence++,
                            ResourceType = "LABOUR",
                            ResourceCode = labour.LabourCode,
                            ResourceDescription = TrimTo(labour.LabourDescription, 200),
                            PlannedUnits = plannedQty,
                            Rate = labour.CostPerOutputUnit,
                            PlannedAmount = decimal.Round(
                                labour.CostPerOutputUnit * plannedQty, 4,
                                MidpointRounding.AwayFromZero)
                        });
                    }
                }

                snapshot.Add(new ProductionWorkOrderOperationVm
                {
                    SequenceNo = operationSequence++,
                    WorkCentreCode = operation.WorkCentreCode,
                    WorkCentreDescription = TrimTo(
                        wcDescriptions.GetValueOrDefault(operation.WorkCentreCode), 200),
                    OperationCode = operation.OperationCode,
                    OperationDescription = TrimTo(
                        processDescriptions.GetValueOrDefault(
                            $"{operation.WorkCentreCode}|{operation.OperationCode}") ?? operation.Remark, 200),
                    IsFinalOperation = operation.IsFinalOperation,
                    PlannedStartDate = plannedStart,
                    PlannedCompletionDate = plannedCompletion,
                    PlannedQty = centreQty,
                    SetupLossQty = operation.SetupLossQty,
                    OperationLossQty = operation.OperationLossQty,
                    RemainingQty = centreQty,
                    Resources = resources
                });
            }

            return new RoutingBuildResult(true, IvMasterErrorCode.None, "", snapshot, warnings);
        }

        var processes = await db.PrDefProcesses.AsNoTracking()
            .Where(x => x.ProdCode == productCode && (x.CompCode == null || x.CompCode == companyCode))
            .OrderBy(x => x.SeqNo ?? int.MaxValue)
            .ThenBy(x => x.ProcessCode)
            .ToListAsync(ct);

        if (processes.Count == 0)
        {
            warnings.Add("No versioned or legacy routing found for this product — operations snapshot is empty.");
            return new RoutingBuildResult(true, IvMasterErrorCode.None, "", [], warnings);
        }

        var machines = await db.PrDefMachines.AsNoTracking()
            .Where(x => x.ProdCode == productCode && (x.CompCode == null || x.CompCode == companyCode))
            .ToListAsync(ct);

        var wcCodes = processes.Select(p => p.WcCode).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList();
        var processCodes = processes.Select(p => p.ProcessCode).Distinct().ToList();
        var machineCodes = machines.Select(m => m.MachineCode).Distinct().ToList();

        var wcLookup = await db.PrWorkCentres.AsNoTracking()
            .Where(x => x.CompCode == companyCode && wcCodes.Contains(x.WrkCtrCd))
            .ToDictionaryAsync(x => x.WrkCtrCd, x => x.WrkCtrDes, StringComparer.OrdinalIgnoreCase, ct);
        var processLookup = await db.PrProcesses.AsNoTracking()
            .Where(x => x.CompCode == companyCode && processCodes.Contains(x.ProcessCd))
            .ToListAsync(ct);
        var processDesByKey = processLookup
            .GroupBy(x => $"{x.ProcessCd}|{x.WorkCentre}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().ProcessDes, StringComparer.OrdinalIgnoreCase);
        var processDesByCode = processLookup
            .GroupBy(x => x.ProcessCd, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().ProcessDes, StringComparer.OrdinalIgnoreCase);
        var machineLookup = await db.PrMachines.AsNoTracking()
            .Where(x => x.CompCode == companyCode && machineCodes.Contains(x.MachineCd))
            .GroupBy(x => x.MachineCd)
            .ToDictionaryAsync(g => g.Key, g => g.First().MachineDes, StringComparer.OrdinalIgnoreCase, ct);

        foreach (var wc in wcCodes)
        {
            if (!wcLookup.ContainsKey(wc))
                return new RoutingBuildResult(false, IvMasterErrorCode.Validation,
                    $"Work Centre '{wc}' from routing is missing in tenant masters.", [], warnings);
        }
        foreach (var pc in processCodes)
        {
            if (!processDesByCode.ContainsKey(pc))
                return new RoutingBuildResult(false, IvMasterErrorCode.Validation,
                    $"Process '{pc}' from routing is missing in tenant masters.", [], warnings);
        }
        foreach (var mc in machineCodes)
        {
            if (!machineLookup.ContainsKey(mc))
                return new RoutingBuildResult(false, IvMasterErrorCode.Validation,
                    $"Machine '{mc}' from routing is missing in tenant masters.", [], warnings);
        }

        var ops = new List<ProductionWorkOrderOperationVm>();
        var seq = 1;
        foreach (var p in processes)
        {
            var opMachines = machines
                .Where(m => string.Equals(m.ProcessCode, p.ProcessCode, StringComparison.OrdinalIgnoreCase)
                            && (string.IsNullOrWhiteSpace(p.WcCode)
                                || string.Equals(m.WcCode, p.WcCode, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(m => m.MacDefault == true)
                .ThenBy(m => m.SeqNo ?? int.MaxValue)
                .ToList();

            var resources = opMachines.Select((m, i) => new ProductionWorkOrderResourceVm
            {
                SequenceNo = i + 1,
                ResourceType = "MACHINE",
                ResourceCode = m.MachineCode,
                ResourceDescription = TrimTo(
                    machineLookup.GetValueOrDefault(m.MachineCode) ?? m.MachineName, 200),
                PlannedUnits = 1m,
                SetupMinutes = (decimal)(m.StartupTime ?? 0) + (decimal)(m.ConversionTime ?? 0),
                RunMinutes = (decimal)m.CycleTime * plannedQty,
                QueueMinutes = (decimal)(m.QueueTime ?? 0),
                Rate = 0m,
                PlannedAmount = 0m
            }).ToList();

            if (resources.Count == 0)
                warnings.Add($"Process {p.ProcessCode} has no PrDefMachine rows — operation has no resources.");

            ops.Add(new ProductionWorkOrderOperationVm
            {
                SequenceNo = p.SeqNo ?? seq,
                WorkCentreCode = p.WcCode,
                WorkCentreDescription = TrimTo(wcLookup.GetValueOrDefault(p.WcCode), 200),
                OperationCode = p.ProcessCode,
                OperationDescription = TrimTo(
                    processDesByKey.GetValueOrDefault($"{p.ProcessCode}|{p.WcCode}")
                    ?? processDesByCode.GetValueOrDefault(p.ProcessCode)
                    ?? p.Remark, 200),
                IsFinalOperation = p.FinalProcess == true,
                PlannedStartDate = plannedStart,
                PlannedCompletionDate = plannedCompletion,
                PlannedQty = plannedQty,
                SetupLossQty = (decimal)(p.SetupLostQty ?? 0),
                OperationLossQty = (decimal)(p.OperationLostQty ?? 0),
                RemainingQty = plannedQty,
                Resources = resources
            });
            seq++;
        }

        return new RoutingBuildResult(true, IvMasterErrorCode.None, "", ops, warnings);
    }

    private async Task<string> AllocateWorkOrderNoAsync(
        AppDbContext db,
        string companyCode,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var sequence = await _runningNumbers.GetNextAsync(
                db, companyCode, RunningNumberKeys.ProductionWorkOrder, cancellationToken);
            var number = $"WO{sequence:D8}";
            if (!await db.ProductionWorkOrders.AsNoTracking()
                    .AnyAsync(x => x.CompanyCode == companyCode && x.WorkOrderNo == number, cancellationToken))
            {
                return number;
            }
        }

        throw new InvalidOperationException("Unable to allocate a unique Work Order number.");
    }

    private static async Task<ProductionWorkOrder?> LoadAggregateAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        string workOrderNo,
        bool tracking,
        CancellationToken cancellationToken)
    {
        IQueryable<ProductionWorkOrder> query = db.ProductionWorkOrders
            .Include(x => x.Materials).ThenInclude(x => x.ProducingRouteStep)
            .Include(x => x.Materials).ThenInclude(x => x.WorkOrderOperation)
            .Include(x => x.Operations).ThenInclude(x => x.Resources)
            .Include(x => x.Operations).ThenInclude(x => x.Machines).ThenInclude(x => x.Labours)
            .Include(x => x.Operations).ThenInclude(x => x.Labours)
            .Include(x => x.Operations).ThenInclude(x => x.Materials)
            .Include(x => x.RouteSteps).ThenInclude(x => x.Operations).ThenInclude(x => x.Machines).ThenInclude(x => x.Labours)
            .Include(x => x.RouteSteps).ThenInclude(x => x.Operations).ThenInclude(x => x.Labours)
            .Include(x => x.RouteSteps).ThenInclude(x => x.Operations).ThenInclude(x => x.Materials)
            .Include(x => x.AuditEvents)
            .AsSplitQuery()
            .Where(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.WorkOrderNo == workOrderNo);
        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        return await query.SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<AuthorizationResult> AuthorizeAsync(
        string permission,
        bool requireWriteScope,
        CancellationToken cancellationToken)
    {
        var scope = requireWriteScope ? _tenant.TryWriteScope() : _tenant.TryBranchScope();
        if (scope is null)
        {
            return new AuthorizationResult(null,
                (IvMasterErrorCode.InvalidScope,
                    requireWriteScope
                        ? "A valid company, branch and location context is required."
                        : "A valid company and branch context is required."));
        }

        if (!await _accessRights.CanAsync(MenuCodes.PlanningWorkOrder, permission, cancellationToken))
        {
            return new AuthorizationResult(null, (IvMasterErrorCode.AccessDenied, "Not authorized."));
        }

        return new AuthorizationResult(scope, null);
    }

    private static IQueryable<ProductionWorkOrder> ApplySort(
        IQueryable<ProductionWorkOrder> query,
        string? sortField,
        bool descending)
    {
        var field = (sortField ?? nameof(ProductionWorkOrderListRow.WorkOrderNo)).Trim().ToUpperInvariant();
        return (field, descending) switch
        {
            ("PRODUCTCODE", true) => query.OrderByDescending(x => x.ProductCode).ThenByDescending(x => x.WorkOrderNo),
            ("PRODUCTCODE", false) => query.OrderBy(x => x.ProductCode).ThenBy(x => x.WorkOrderNo),
            ("SOURCEDEFINITIONCODE", true) => query.OrderByDescending(x => x.SourceDefinitionCode).ThenByDescending(x => x.WorkOrderNo),
            ("SOURCEDEFINITIONCODE", false) => query.OrderBy(x => x.SourceDefinitionCode).ThenBy(x => x.WorkOrderNo),
            ("STATUS", true) => query.OrderByDescending(x => x.Status).ThenByDescending(x => x.WorkOrderNo),
            ("STATUS", false) => query.OrderBy(x => x.Status).ThenBy(x => x.WorkOrderNo),
            ("PLANNEDQTY", true) => query.OrderByDescending(x => x.PlannedQty).ThenByDescending(x => x.WorkOrderNo),
            ("PLANNEDQTY", false) => query.OrderBy(x => x.PlannedQty).ThenBy(x => x.WorkOrderNo),
            ("PLANNEDSTARTDATE", true) => query.OrderByDescending(x => x.PlannedStartDateTime).ThenByDescending(x => x.WorkOrderNo),
            ("PLANNEDSTARTDATE", false) => query.OrderBy(x => x.PlannedStartDateTime).ThenBy(x => x.WorkOrderNo),
            ("PLANNEDCOMPLETIONDATE", true) => query.OrderByDescending(x => x.PlannedCompletionDateTime).ThenByDescending(x => x.WorkOrderNo),
            ("PLANNEDCOMPLETIONDATE", false) => query.OrderBy(x => x.PlannedCompletionDateTime).ThenBy(x => x.WorkOrderNo),
            (_, true) => query.OrderByDescending(x => x.WorkOrderNo),
            _ => query.OrderBy(x => x.WorkOrderNo)
        };
    }

    private static void TouchSqliteRowVersions(AppDbContext db, ProductionWorkOrder entity)
    {
        if (!IsSqlite(db))
        {
            return;
        }

        entity.RowVersion = Guid.NewGuid().ToByteArray();
    }

    private static bool IsSqlite(AppDbContext db) =>
        string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.Sqlite", StringComparison.Ordinal);

    private static bool IsUniqueConstraint(DbUpdateException exception) =>
        exception.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true
        || exception.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true;

    private static string Normalize(string? value) =>
        (value ?? string.Empty).Trim().ToUpperInvariant();

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? TrimTo(string? value, int maxLength)
    {
        var clean = NullIfEmpty(value);
        return clean is null || clean.Length <= maxLength ? clean : clean[..maxLength];
    }

    private static IvMasterOperationResult<T> ValidationFailure<T>(string message, string field) =>
        IvMasterOperationResult<T>.Fail(
            IvMasterErrorCode.Validation,
            message,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [field] = message });

    private static IvMasterOperationResult<TOut> CopyFailure<TIn, TOut>(IvMasterOperationResult<TIn> source) =>
        IvMasterOperationResult<TOut>.Fail(
            source.ErrorCode,
            source.Message ?? "The operation failed.",
            source.ValidationErrors,
            source.DeleteCheck);

    private sealed record AuthorizationResult(
        InventoryTenantScope? Scope,
        (IvMasterErrorCode Code, string Message)? Error);

    private sealed record NormalizedDraft(
        string ProductCode,
        string DefinitionCode,
        decimal PlannedQty,
        DateTime PlannedStartDate,
        DateTime PlannedCompletionDate,
        string SchedulingDirection,
        string SourceType,
        string? SourceReference,
        string? Remark);

    private sealed record PreparedSnapshot(
        ProductionWorkOrderPreview Preview,
        NormalizedDraft Request);

    private sealed record SourceBomLineSnapshot(
        long LineId,
        long HeaderId,
        int HeaderVersion,
        decimal BomOutputQty,
        string? BomOutputUom,
        string ParentProductCode,
        string? ComponentDescription,
        decimal ComponentQty,
        string? ComponentUom,
        decimal ScrapPercent,
        decimal Tolerance,
        string? WarehouseCode,
        string? LocationCode);

    private sealed class WorkOrderCommandException : Exception
    {
        public WorkOrderCommandException(IvMasterErrorCode code, string message) : base(message)
        {
            Code = code;
        }

        public IvMasterErrorCode Code { get; }
    }
}

