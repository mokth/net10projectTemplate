using System.Globalization;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Lookups;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Services;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Core.Transactions;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

/// <summary>Production-only quantity/value adjustment. PADJ never posts an Inventory movement.</summary>
public sealed class ProductionStockAdjustmentService(
    IDbContextFactory<AppDbContext> factory, IInventoryTenantContext tenant, IAccessRightService access,
    ICurrentDateService clock, IRunningNumberService numbers, IBranchStockTransactionLock branchLock,
    IStockPostingCoordinator coordinator, IProductionStockWriter production,
    IItemStandardCostResolver standardCosts)
    : IProductionStockAdjustmentService
{
    private const string Menu = MenuCodes.PlanningStockAdjustment;
    private static IvMasterOperationResult<T> Fail<T>(string message, IvMasterErrorCode code = IvMasterErrorCode.Validation)
        => IvMasterOperationResult<T>.Fail(code, message);
    private sealed class AdjustmentException(string message, IvMasterErrorCode code = IvMasterErrorCode.Validation) : Exception(message)
    { public IvMasterErrorCode Code { get; } = code; }
    private static string User(InventoryTenantScope scope) => scope.UserId.Length > 10 ? scope.UserId[..10] : scope.UserId;

    private async Task<InventoryTenantScope> ScopeAsync(string permission, CancellationToken ct)
    {
        if (!await access.CanAsync(Menu, PermissionCodes.Access, ct) || !await access.CanAsync(Menu, permission, ct))
            throw new AdjustmentException("Access denied.", IvMasterErrorCode.AccessDenied);
        return tenant.TryWriteScope() is { BranchCode: not null } scope ? scope
            : throw new AdjustmentException("A company, branch and user scope is required.", IvMasterErrorCode.InvalidScope);
    }

    private static void Version(ProductionStockAdjustment doc, byte[] version)
    {
        if (version.Length == 0 || !doc.RowVersion.SequenceEqual(version))
            throw new AdjustmentException("The adjustment changed. Reload before continuing.", IvMasterErrorCode.Concurrency);
    }

    private static async Task<ProductionStockAdjustment> LoadAsync(AppDbContext db, InventoryTenantScope scope, int id, bool locked, CancellationToken ct)
    {
        var query = locked && db.Database.IsSqlServer()
            ? db.ProductionStockAdjustments.FromSqlInterpolated($"SELECT * FROM dbo.PrProductionStockAdjustment WITH (UPDLOCK,HOLDLOCK) WHERE BatchId={id} AND CompanyCode={scope.CompanyCode} AND BranchCode={scope.BranchCode}")
            : db.ProductionStockAdjustments.Where(x => x.BatchId == id && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode);
        return await query.Include(x => x.Batch).Include(x => x.Lines).ThenInclude(x => x.Detail).SingleOrDefaultAsync(ct)
            ?? throw new AdjustmentException("Production Stock Adjustment was not found.", IvMasterErrorCode.NotFound);
    }

    private static async Task<ProductionWorkOrder> LockWorkOrderAsync(AppDbContext db, string company, string branch, long id, CancellationToken ct)
    {
        var query = db.Database.IsSqlServer()
            ? db.ProductionWorkOrders.FromSqlInterpolated($"SELECT * FROM dbo.PrWorkOrder WITH (UPDLOCK,HOLDLOCK) WHERE UID={id} AND CompanyCode={company} AND BranchCode={branch}")
            : db.ProductionWorkOrders.Where(x => x.Uid == id && x.CompanyCode == company && x.BranchCode == branch);
        return await query.SingleOrDefaultAsync(ct) ?? throw new AdjustmentException("Work Order was not found.", IvMasterErrorCode.NotFound);
    }

    private static string? WorkOrderError(ProductionWorkOrder order, int revision, string hash)
    {
        if (order.Status is not (ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress))
            return "Production Stock Adjustment is available only while the Work Order is RELEASED or IN_PROGRESS.";
        if (!ProductionSnapshotFormatVersions.IsFullHierarchy(order.SnapshotFormatVersion) || order.IsLegacySnapshot
            || order.SnapshotRevision <= 0 || string.IsNullOrWhiteSpace(order.SnapshotHash) || order.SnapshotHash.Length != 64)
            return "The Work Order does not have a valid current manufacturing snapshot.";
        if (revision != order.SnapshotRevision || !string.Equals(hash, order.SnapshotHash, StringComparison.Ordinal))
            return "The Work Order snapshot changed. Reload and save the adjustment again.";
        return null;
    }

    private IQueryable<ProductionWorkOrder> EligibleWorkOrders(AppDbContext db, InventoryTenantScope scope)
    {
        return db.ProductionWorkOrders.AsNoTracking().Where(order =>
            order.CompanyCode == scope.CompanyCode && order.BranchCode == scope.BranchCode
            && (order.Status == ProductionWorkOrderStatuses.Released || order.Status == ProductionWorkOrderStatuses.InProgress)
            && order.SnapshotFormatVersion >= ProductionSnapshotFormatVersions.FullHierarchyV2
            && !order.IsLegacySnapshot && order.SnapshotRevision > 0 && order.SnapshotHash != null && order.SnapshotHash.Length == 64
            && db.ProductionBalLots.Any(pool => pool.CompanyCode == scope.CompanyCode && pool.BranchCode == scope.BranchCode
                && pool.WorkOrderId == order.Uid && (pool.Kind == ProductionBalLotKinds.MaterialIn || pool.Kind == ProductionBalLotKinds.Wip)));
    }

    private static ProductionStockWorkOrderLookupRow MapLookupRow(ProductionWorkOrder order, int candidateCount) => new()
    {
        WorkOrderId = order.Uid,
        WorkOrderNo = order.WorkOrderNo,
        ProductCode = order.ProductCode,
        ProductDescription = order.ProductDescription,
        Status = order.Status,
        PlannedQty = order.PlannedQty,
        RemainingQty = order.RemainingQty,
        OutputUom = order.OutputUom,
        SnapshotRevision = order.SnapshotRevision,
        SnapshotHash = order.SnapshotHash,
        CandidateSourceCount = candidateCount
    };

    public async Task<LargeLookupPage<ProductionStockWorkOrderLookupRow>> SearchEligibleWorkOrdersAsync(
        LargeLookupSearchRequest request, CancellationToken ct = default)
    {
        try
        {
            var scope = await ScopeAsync(PermissionCodes.Access, ct);
            await using var db = await factory.CreateDbContextAsync(ct);
            var orders = EligibleWorkOrders(db, scope);
            if (!string.IsNullOrWhiteSpace(request.SearchText))
            {
                var term = request.SearchText.Trim();
                orders = orders.Where(order => order.WorkOrderNo.Contains(term) || order.ProductCode.Contains(term)
                    || (order.ProductDescription != null && order.ProductDescription.Contains(term)));
            }

            var total = await orders.CountAsync(ct);
            var rows = await orders.OrderByDescending(order => order.WorkOrderNo).ThenByDescending(order => order.Uid)
                .Skip(request.NormalizedSkip).Take(request.NormalizedTake)
                .Select(order => new
                {
                    Order = order,
                    CandidateCount = db.ProductionBalLots.Count(pool => pool.CompanyCode == scope.CompanyCode
                        && pool.BranchCode == scope.BranchCode && pool.WorkOrderId == order.Uid
                        && (pool.Kind == ProductionBalLotKinds.MaterialIn || pool.Kind == ProductionBalLotKinds.Wip))
                }).ToListAsync(ct);
            return LargeLookupPage<ProductionStockWorkOrderLookupRow>.Ok(
                rows.Select(x => MapLookupRow(x.Order, x.CandidateCount)).ToArray(), total);
        }
        catch (AdjustmentException ex)
        {
            return LargeLookupPage<ProductionStockWorkOrderLookupRow>.Fail(ex.Message);
        }
    }

    public async Task<LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>> ResolveEligibleWorkOrderAsync(
        string workOrderNo, CancellationToken ct = default)
    {
        var code = workOrderNo?.Trim() ?? "";
        if (code.Length == 0)
            return LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>.Fail("Work Order number is required.");
        try
        {
            var scope = await ScopeAsync(PermissionCodes.Access, ct);
            await using var db = await factory.CreateDbContextAsync(ct);
            var order = await EligibleWorkOrders(db, scope).SingleOrDefaultAsync(x => x.WorkOrderNo == code, ct);
            if (order is null)
            {
                var exists = await db.ProductionWorkOrders.AsNoTracking().AnyAsync(x => x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode && x.WorkOrderNo == code, ct);
                return LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>.Fail(exists
                    ? $"Work Order '{code}' is not currently eligible for Production Stock Adjustment."
                    : $"Work Order '{code}' was not found.");
            }

            var count = await db.ProductionBalLots.AsNoTracking().CountAsync(pool => pool.CompanyCode == scope.CompanyCode
                && pool.BranchCode == scope.BranchCode && pool.WorkOrderId == order.Uid
                && (pool.Kind == ProductionBalLotKinds.MaterialIn || pool.Kind == ProductionBalLotKinds.Wip), ct);
            return LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>.Ok(MapLookupRow(order, count));
        }
        catch (AdjustmentException ex)
        {
            return LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>.Fail(ex.Message);
        }
    }

    private static async Task<ProductionBalLot> LockPoolAsync(AppDbContext db, InventoryTenantScope scope, long id, CancellationToken ct)
    {
        var pool = db.Database.IsSqlServer()
            ? await db.ProductionBalLots.FromSqlInterpolated($"SELECT * FROM dbo.PrProductionBalLot WITH (UPDLOCK,HOLDLOCK) WHERE UID={id}").SingleOrDefaultAsync(ct)
            : await db.ProductionBalLots.SingleOrDefaultAsync(x => x.Uid == id, ct);
        if (pool is null || pool.CompanyCode != scope.CompanyCode || pool.BranchCode != scope.BranchCode)
            throw new AdjustmentException("Production source pool was not found in this branch.", IvMasterErrorCode.NotFound);
        if (pool.Kind is not (ProductionBalLotKinds.MaterialIn or ProductionBalLotKinds.Wip))
            throw new AdjustmentException("Only existing MATERIAL_IN and WIP Production pools can be adjusted.");
        return pool;
    }

    private static void RequireScalarContract(ProductionBalLot pool)
    {
        if (pool.ConversionFactorToBase <= 0m || string.IsNullOrWhiteSpace(pool.Uom) || string.IsNullOrWhiteSpace(pool.BaseUom)
            || IvQty.Round(pool.BaseQty / pool.ConversionFactorToBase) != pool.Qty
            || (pool.BaseQty > 0m && StockLedgerPrecision.Money(pool.TotalCost / pool.BaseQty) != pool.AverageUnitCost)
            || (pool.BaseQty == 0m && (pool.Qty != 0m || pool.TotalCost != 0m || pool.AverageUnitCost != 0m)))
            throw new AdjustmentException($"Production pool {pool.Uid} quantity, UOM, or average-cost projection is inconsistent.");
    }

    private async Task<ProductionMaterialMovement?> RequireMaterialIssueAsync(AppDbContext db, InventoryTenantScope scope,
        ProductionBalLot pool, CancellationToken ct)
    {
        if (pool.Kind != ProductionBalLotKinds.MaterialIn) return null;
        if (pool.WorkOrderMaterialId is not long materialId || pool.OriginalIssueMovementId is not long issueId)
            throw new AdjustmentException("MATERIAL_IN pool has no exact Work Order material and source ISSUE lineage.");
        var material = await db.ProductionWorkOrderMaterials.AsNoTracking().SingleOrDefaultAsync(x => x.Uid == materialId
            && x.WorkOrderId == pool.WorkOrderId, ct) ?? throw new AdjustmentException("MATERIAL_IN Work Order material is missing.");
        if (material.ComponentCode != pool.ItemCode || material.RequiredUom != pool.Uom || material.BaseUom != pool.BaseUom
            || material.ConversionFactorToBase != pool.ConversionFactorToBase || material.WorkOrderOperationId is null)
            throw new AdjustmentException("MATERIAL_IN pool no longer matches its frozen Work Order material identity.");
        var issue = await db.ProductionMaterialMovements.AsNoTracking().SingleOrDefaultAsync(x => x.Uid == issueId
            && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode
            && x.WorkOrderId == pool.WorkOrderId && x.WorkOrderMaterialId == materialId
            && x.ProductionBalLotId == pool.Uid && x.MovementType == ProductionMaterialMovementTypes.Issue
            && x.OriginalMovementId == null && x.ProductionBalLotMovementId != null, ct)
            ?? throw new AdjustmentException("MATERIAL_IN source ISSUE lineage is broken.");
        if (issue.StockPostingId is not long postingId || !await db.StockPostings.AnyAsync(x => x.Id == postingId && x.SealedAtUtc != null, ct)
            || await db.ProductionMaterialMovements.AnyAsync(x => x.OriginalMovementId == issue.Uid
                && x.MovementType == ProductionMaterialMovementTypes.IssueReversal, ct))
            throw new AdjustmentException("MATERIAL_IN source ISSUE is not active in a sealed V2 posting.");
        var balanceMovement = await db.ProductionBalLotMovements.AsNoTracking().SingleOrDefaultAsync(x => x.Uid == issue.ProductionBalLotMovementId
            && x.ProductionBalLotId == pool.Uid && x.WorkOrderId == pool.WorkOrderId
            && x.WorkOrderMaterialId == materialId && x.MovementType == ProductionBalLotMovementTypes.Issue
            && x.OriginalMovementId == null && x.StockPostingId == postingId && x.LedgerVersion == 2
            && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode
            && x.ValuationStatus == ProductionPoolValuationService.Verified, ct)
            ?? throw new AdjustmentException("MATERIAL_IN balance ISSUE is missing or not VERIFIED.");
        var evidence = await db.ProductionValuationEvidenceRows.AsNoTracking().SingleOrDefaultAsync(x => x.MovementId == balanceMovement.Uid, ct);
        if (evidence?.Status != ProductionPoolValuationService.Verified || issue.BaseQty <= 0m)
            throw new AdjustmentException("MATERIAL_IN source ISSUE has no exact VERIFIED cost evidence.");
        return issue;
    }

    private async Task<ProductionStockAdjustmentDocument> MapAsync(AppDbContext db, ProductionStockAdjustment doc, CancellationToken ct)
    {
        var canViewCost = await access.CanAsync(Menu, PermissionCodes.ViewCost, ct);
        var order = await db.ProductionWorkOrders.AsNoTracking().SingleAsync(x => x.Uid == doc.WorkOrderId, ct);
        var result = new ProductionStockAdjustmentDocument { Id = doc.BatchId, BatchNo = doc.Batch.BatchNo,
            WorkOrderId = doc.WorkOrderId, WorkOrderNo = order.WorkOrderNo, ProductCode = order.ProductCode,
            ProductDescription = order.ProductDescription, WorkOrderStatus = order.Status, Status = doc.Batch.BatchStatus,
            EffectiveDate = doc.Batch.TrxDtTime, RefNo = doc.Batch.RefNo, Remarks = doc.Batch.Remarks,
            Revision = doc.DocumentRevision, RowVersion = doc.RowVersion, PostingId = doc.PostingId,
            SnapshotRevision = doc.SnapshotRevision, SnapshotHash = doc.SnapshotHash,
            ReversalPostingId = doc.ReversalPostingId, CorrectedBatchId = doc.CorrectedBatchId,
            CanViewCost = canViewCost };
        foreach (var line in doc.Lines.OrderBy(x => x.Id))
        {
            var pool = await db.ProductionBalLots.AsNoTracking().SingleAsync(x => x.Uid == line.ProductionBalLotId, ct);
            var fact = canViewCost && doc.PostingId is long postingId
                ? await db.ProductionStockAdjustmentFacts.AsNoTracking().SingleOrDefaultAsync(x => x.BatchId == doc.BatchId
                    && x.AdjustmentLineId == line.Id && x.StockPostingId == postingId && x.ReversesFactId == null, ct) : null;
            decimal? value = fact?.TotalValue;
            if (canViewCost && value is null && line.AdjustmentQty > 0m && pool.BaseQty > 0m)
                value = StockLedgerPrecision.Money(pool.TotalCost * line.BaseQty / pool.BaseQty);
            result.Lines.Add(new() { ProductionBalLotId = pool.Uid, ItemCode = pool.ItemCode, Description = pool.Description ?? "",
                PoolKind = pool.Kind, LotNo = pool.PhysicalLotNo ?? pool.LotNo, CurrentQty = pool.Qty, Uom = pool.Uom,
                AdjustmentQty = line.AdjustmentQty, BaseQty = line.BaseQty, CurrentBaseQty = pool.BaseQty,
                SourceUom = line.SourceUom, BaseUom = line.BaseUom,
                Reason = line.Detail.Reason ?? "", Remarks = line.Detail.Remarks, ProposedUnitCost = canViewCost ? line.ProposedUnitCost : null,
                CostEvidenceType = line.CostEvidenceType, CostOverrideReason = canViewCost ? line.CostOverrideReason : null,
                TotalValue = value, CostBasisCode = fact?.CostBasisCode });
        }
        return result;
    }

    public async Task<IvMasterOperationResult<ProductionStockAdjustmentDocument>> GetAsync(int id, CancellationToken ct = default)
    {
        try { var scope = await ScopeAsync(PermissionCodes.Access, ct); await using var db = await factory.CreateDbContextAsync(ct);
            return IvMasterOperationResult<ProductionStockAdjustmentDocument>.Ok(await MapAsync(db, await LoadAsync(db, scope, id, false, ct), ct)); }
        catch (AdjustmentException e) { return Fail<ProductionStockAdjustmentDocument>(e.Message, e.Code); }
    }

    public async Task<IvMasterOperationResult<ProductionStockAdjustmentPage>> SearchAsync(ProductionStockAdjustmentQuery query, CancellationToken ct = default)
    {
        try
        {
            var scope = await ScopeAsync(PermissionCodes.Access, ct); await using var db = await factory.CreateDbContextAsync(ct);
            var rows = from a in db.ProductionStockAdjustments.AsNoTracking()
                join w in db.ProductionWorkOrders.AsNoTracking() on a.WorkOrderId equals w.Uid
                where a.CompanyCode == scope.CompanyCode && a.BranchCode == scope.BranchCode && a.DeletedAtUtc == null
                select new { a, WorkOrderNo = w.WorkOrderNo };
            if (query.WorkOrderId is long workOrderId) rows = rows.Where(x => x.a.WorkOrderId == workOrderId);
            if (!string.IsNullOrWhiteSpace(query.Status)) rows = rows.Where(x => x.a.Batch.BatchStatus == query.Status);
            if (!string.IsNullOrWhiteSpace(query.SearchText))
            {
                var term = query.SearchText.Trim();
                var isBatchNo = int.TryParse(term, NumberStyles.Integer, CultureInfo.InvariantCulture, out var batchNo);
                rows = rows.Where(x => (isBatchNo && x.a.Batch.BatchNo == batchNo)
                    || x.WorkOrderNo.Contains(term) || (x.a.Batch.RefNo != null && x.a.Batch.RefNo.Contains(term)));
            }
            var total = await rows.CountAsync(ct); var take = Math.Clamp(query.Take <= 0 ? 30 : query.Take, 1, 100);
            var page = await rows.OrderByDescending(x => x.a.BatchId).Skip(Math.Max(0, query.Skip)).Take(take)
                .Select(x => new { x.a.BatchId, x.a.Batch.BatchNo, x.a.WorkOrderId, x.WorkOrderNo, x.a.Batch.BatchStatus, x.a.Batch.TrxDtTime, x.a.RowVersion }).ToListAsync(ct);
            var ids = page.Select(x => x.BatchId).ToArray();
            var details = await (from l in db.ProductionStockAdjustmentLines.AsNoTracking()
                join d in db.IvTrxBatchDetails.AsNoTracking() on l.DetailId equals d.Id
                where ids.Contains(l.BatchId) select new { l.BatchId, l.AdjustmentQty, l.SourceUom, d.ICode }).ToListAsync(ct);
            var summaries = page.Select(x => new ProductionStockAdjustmentSummary(x.BatchId, x.BatchNo, x.WorkOrderId, x.WorkOrderNo,
                string.Join(", ", details.Where(d => d.BatchId == x.BatchId).Select(d => d.ICode).Distinct()),
                string.Join(", ", details.Where(d => d.BatchId == x.BatchId).Select(d => $"{d.AdjustmentQty.ToString("+0.0000;-0.0000;0.0000", CultureInfo.InvariantCulture)} {d.SourceUom}")),
                x.BatchStatus, x.TrxDtTime, x.RowVersion)).ToArray();
            return IvMasterOperationResult<ProductionStockAdjustmentPage>.Ok(new(summaries, total));
        }
        catch (AdjustmentException e) { return Fail<ProductionStockAdjustmentPage>(e.Message, e.Code); }
    }

    public async Task<IvMasterOperationResult<ProductionStockAdjustmentSourcePage>> SearchSourcesAsync(ProductionStockAdjustmentSourceQuery query, CancellationToken ct = default)
    {
        try
        {
            var scope = await ScopeAsync(PermissionCodes.Access, ct); var canViewCost = await access.CanAsync(Menu, PermissionCodes.ViewCost, ct);
            await using var db = await factory.CreateDbContextAsync(ct);
            var order = await db.ProductionWorkOrders.AsNoTracking().SingleOrDefaultAsync(x => x.Uid == query.WorkOrderId
                && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode, ct)
                ?? throw new AdjustmentException("Work Order was not found.", IvMasterErrorCode.NotFound);
            if (order.Status is not (ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress))
                return IvMasterOperationResult<ProductionStockAdjustmentSourcePage>.Ok(new([], 0));
            var pools = db.ProductionBalLots.AsNoTracking().Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode
                && x.WorkOrderId == order.Uid && (x.Kind == ProductionBalLotKinds.MaterialIn || x.Kind == ProductionBalLotKinds.Wip));
            if (!string.IsNullOrWhiteSpace(query.SearchText)) { var term = query.SearchText.Trim(); pools = pools.Where(x => x.ItemCode.Contains(term)
                || x.LotNo.Contains(term) || (x.Description != null && x.Description.Contains(term))); }
            var count = await pools.CountAsync(ct); var take = Math.Clamp(query.Take <= 0 ? 30 : query.Take, 1, 100);
            var ids = await pools.OrderBy(x => x.Uid).Skip(Math.Max(0, query.Skip)).Take(take).Select(x => x.Uid).ToArrayAsync(ct);
            var result = new List<ProductionStockAdjustmentSource>();
            foreach (var id in ids)
            {
                var pool = await db.ProductionBalLots.AsNoTracking().SingleAsync(x => x.Uid == id, ct);
                string? error = null; var requiresApproval = false;
                try { RequireScalarContract(pool); error = await ProductionCostReadiness.PoolValuationError(db, pool, ct);
                    await RequireMaterialIssueAsync(db, scope, pool, ct); requiresApproval = pool.BaseQty == 0m && pool.Kind == ProductionBalLotKinds.Wip; }
                catch (AdjustmentException ex) { error = ex.Message; }
                result.Add(new(pool.Uid, order.WorkOrderNo, pool.ItemCode, pool.Description ?? "", pool.Kind,
                    pool.PhysicalLotNo ?? pool.LotNo, pool.Qty, pool.Uom, pool.BaseQty, pool.BaseUom,
                    error is null ? "READY" : error, canViewCost ? pool.TotalCost : null, requiresApproval));
            }
            return IvMasterOperationResult<ProductionStockAdjustmentSourcePage>.Ok(new(result, count));
        }
        catch (AdjustmentException e) { return Fail<ProductionStockAdjustmentSourcePage>(e.Message, e.Code); }
    }

    public Task<IvMasterOperationResult<ProductionStockAdjustmentDocument>> SaveAsync(ProductionStockAdjustmentSaveRequest request, CancellationToken ct = default)
        => SaveCoreAsync(request, null, ct);

    private async Task<IvMasterOperationResult<ProductionStockAdjustmentDocument>> SaveCoreAsync(ProductionStockAdjustmentSaveRequest request, int? correctionId, CancellationToken ct)
    {
        try
        {
            var scope = await ScopeAsync(request.Id == 0 ? PermissionCodes.Add : PermissionCodes.Edit, ct);
            if (request.Lines.Count is < 1 or > 100) throw new AdjustmentException("Enter between one and 100 adjustment lines.");
            var date = request.EffectiveDate == default ? clock.Now : request.EffectiveDate;
            if (date > clock.Now) throw new AdjustmentException("Future Production Stock Adjustment dates are not allowed.");
            if (request.RefNo?.Length > 30 || request.Remarks?.Length > 200) throw new AdjustmentException("Reference is limited to 30 characters and remarks to 200 characters.");
            if (request.Lines.Select(x => x.ProductionBalLotId).Distinct().Count() != request.Lines.Count) throw new AdjustmentException("A Production pool may appear only once in an adjustment.");
            var hasOverride = request.Lines.Any(x => !string.IsNullOrWhiteSpace(x.CostEvidenceType) || x.ProposedUnitCost.HasValue || !string.IsNullOrWhiteSpace(x.CostOverrideReason));
            if (hasOverride && !await access.CanAsync(Menu, PermissionCodes.PriceOverride, ct)) throw new AdjustmentException("Price Override permission is required for manual or zero cost approval.", IvMasterErrorCode.AccessDenied);
            await using var db = await factory.CreateDbContextAsync(ct); db.ProductionStockAdjustmentWrite = true;
            await using var tx = await db.Database.BeginTransactionAsync(ct); await branchLock.AcquireAsync(db, scope.CompanyCode, scope.BranchCode!, ct);
            var order = await LockWorkOrderAsync(db, scope.CompanyCode, scope.BranchCode!, request.WorkOrderId, ct);
            if (WorkOrderError(order, request.SnapshotRevision, request.SnapshotHash) is { } snapshotError) throw new AdjustmentException(snapshotError, IvMasterErrorCode.Concurrency);
            ProductionStockAdjustment doc;
            if (request.Id == 0)
            {
                if (correctionId is int corrected && (await LoadAsync(db, scope, corrected, true, ct)).Batch.BatchStatus != "REVERSED") throw new AdjustmentException("Rollback the original adjustment before creating a correction.");
                doc = new() { CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!, WorkOrderId = order.Uid,
                    SnapshotRevision = order.SnapshotRevision, SnapshotHash = order.SnapshotHash, CorrectedBatchId = correctionId,
                    Batch = new() { CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!, BatchNo = await numbers.GetNextAsync(db, scope.CompanyCode, RunningNumberKeys.IvBatch, ct),
                        TrxType = IvTrxTypes.ProductionStockAdjustment, BatchStatus = IvBatchStatuses.New, CreatedDate = clock.Now, CreatedBy = User(scope) } };
                db.ProductionStockAdjustments.Add(doc);
            }
            else
            {
                doc = await LoadAsync(db, scope, request.Id, true, ct); Version(doc, request.ExpectedVersion);
                if (doc.Batch.BatchStatus != IvBatchStatuses.New || doc.PostingId is not null) throw new AdjustmentException("Only a NEW unposted adjustment can be edited.");
                if (doc.WorkOrderId != order.Uid || WorkOrderError(order, doc.SnapshotRevision, doc.SnapshotHash) is not null) throw new AdjustmentException("The saved adjustment Work Order snapshot changed.", IvMasterErrorCode.Concurrency);
                db.ProductionStockAdjustmentLines.RemoveRange(doc.Lines); db.IvTrxBatchDetails.RemoveRange(doc.Lines.Select(x => x.Detail)); doc.Lines.Clear(); doc.DocumentRevision++;
            }
            doc.SnapshotRevision = order.SnapshotRevision; doc.SnapshotHash = order.SnapshotHash;
            doc.Batch.TrxDtTime = date; doc.Batch.RefNo = string.IsNullOrWhiteSpace(request.RefNo) ? doc.Batch.BatchNo.ToString(CultureInfo.InvariantCulture) : request.RefNo.Trim();
            doc.Batch.Remarks = request.Remarks?.Trim(); doc.Batch.ModifiedDate = clock.Now; doc.Batch.ModifiedBy = User(scope);
            short lineNo = 0;
            foreach (var input in request.Lines.OrderBy(x => x.ProductionBalLotId))
            {
                var pool = await LockPoolAsync(db, scope, input.ProductionBalLotId, ct);
                if (pool.WorkOrderId != order.Uid) throw new AdjustmentException("Every source pool must belong to the selected Work Order.");
                RequireScalarContract(pool); await RequireMaterialIssueAsync(db, scope, pool, ct);
                if (await ProductionCostReadiness.PoolValuationError(db, pool, ct) is { } poolError) throw new AdjustmentException(poolError);
                if (pool.LastStockEventEffectiveAt > date || pool.LastMovementDate > date) throw new AdjustmentException($"Adjustment date precedes the latest Production stock event for pool {pool.Uid}.");
                if (input.AdjustmentQty == 0m || IvQty.Round(input.AdjustmentQty) != input.AdjustmentQty) throw new AdjustmentException("Adjustment quantity must be non-zero and use the frozen four-decimal UOM precision.");
                var baseQty = IvQty.Round(Math.Abs(input.AdjustmentQty) * pool.ConversionFactorToBase);
                if (IvQty.Round(baseQty / pool.ConversionFactorToBase) != Math.Abs(input.AdjustmentQty)) throw new AdjustmentException($"Adjustment quantity for {pool.ItemCode} does not round-trip through the pool UOM.");
                if (input.AdjustmentQty < 0m && baseQty > pool.BaseQty) throw new AdjustmentException($"Adjustment OUT exceeds available Production quantity for {pool.ItemCode}.");
                var reason = input.Reason.Trim().ToUpperInvariant();
                if (!ProductionStockAdjustmentReasons.All.Contains(reason, StringComparer.Ordinal)) throw new AdjustmentException("Select a valid Production Stock Adjustment reason.");
                if (reason == ProductionStockAdjustmentReasons.Other && string.IsNullOrWhiteSpace(input.Remarks)) throw new AdjustmentException("Remarks are required when the reason is OTHER.");
                if (input.Remarks?.Length > 250 || input.CostOverrideReason?.Length > 250) throw new AdjustmentException("Line remarks and cost approval reasons are limited to 250 characters.");
                string? evidence = null; decimal? proposed = null; string? approvalReason = null; string? approvedBy = null; DateTime? approvedAt = null;
                var requestedEvidence = input.CostEvidenceType?.Trim().ToUpperInvariant();
                if (requestedEvidence is not null || input.ProposedUnitCost.HasValue || !string.IsNullOrWhiteSpace(input.CostOverrideReason))
                {
                    if (input.AdjustmentQty <= 0m || pool.Kind != ProductionBalLotKinds.Wip || pool.BaseQty != 0m)
                        throw new AdjustmentException("Manual or zero cost approval is allowed only for a positive adjustment into an empty WIP pool.");
                    if (requestedEvidence == InventoryCostEvidenceTypes.ManualApproved && input.ProposedUnitCost is > 0m) evidence = InventoryCostEvidenceTypes.ManualApproved;
                    else if (requestedEvidence == InventoryCostEvidenceTypes.ZeroCostApproved && input.ProposedUnitCost == 0m) evidence = InventoryCostEvidenceTypes.ZeroCostApproved;
                    else throw new AdjustmentException("Choose MANUAL_APPROVED with a positive cost or ZERO_COST_APPROVED with exactly zero cost.");
                    if (string.IsNullOrWhiteSpace(input.CostOverrideReason)) throw new AdjustmentException("A cost approval reason is required.");
                    proposed = StockLedgerPrecision.Money(input.ProposedUnitCost!.Value);
                    if (evidence == InventoryCostEvidenceTypes.ManualApproved && proposed <= 0m)
                        throw new AdjustmentException("Manual unit cost must remain positive at six-decimal precision. Use ZERO_COST_APPROVED for zero cost.");
                    approvalReason = input.CostOverrideReason.Trim(); approvedBy = User(scope); approvedAt = clock.Now;
                }
                var detail = new IvTrxBatchDetail { Batch = doc.Batch, CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!,
                    BatchNo = doc.Batch.BatchNo, DocumentRevision = doc.DocumentRevision, TrxLineNo = ++lineNo,
                    TrxType = IvTrxTypes.ProductionStockAdjustment, ICode = pool.ItemCode, IDesc = pool.Description,
                    Reason = reason, Remarks = input.Remarks?.Trim() };
                doc.Lines.Add(new ProductionStockAdjustmentLine { Detail = detail, ProductionBalLotId = pool.Uid,
                    AdjustmentQty = IvQty.Round(input.AdjustmentQty), SourceUom = pool.Uom, BaseUom = pool.BaseUom,
                    SourceFactor = pool.ConversionFactorToBase, BaseQty = baseQty, ProposedUnitCost = proposed,
                    CostEvidenceType = evidence, CostOverrideReason = approvalReason, CostApprovedBy = approvedBy, CostApprovedAtUtc = approvedAt });
            }
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return IvMasterOperationResult<ProductionStockAdjustmentDocument>.Ok(await MapAsync(db, doc, ct));
        }
        catch (AdjustmentException e) { return Fail<ProductionStockAdjustmentDocument>(e.Message, e.Code); }
        catch (DbUpdateConcurrencyException) { return Fail<ProductionStockAdjustmentDocument>("The adjustment or source pool changed. Reload before saving.", IvMasterErrorCode.Concurrency); }
        catch (InvalidOperationException e) { return Fail<ProductionStockAdjustmentDocument>(e.Message); }
    }

    public async Task<IvMasterOperationResult<bool>> DeleteAsync(int id, byte[] expectedVersion, CancellationToken ct = default)
    {
        try
        {
            var scope = await ScopeAsync(PermissionCodes.Delete, ct); await using var db = await factory.CreateDbContextAsync(ct); db.ProductionStockAdjustmentWrite = true;
            await using var tx = await db.Database.BeginTransactionAsync(ct); await branchLock.AcquireAsync(db, scope.CompanyCode, scope.BranchCode!, ct);
            var doc = await LoadAsync(db, scope, id, true, ct); Version(doc, expectedVersion);
            if (doc.Batch.BatchStatus == IvBatchStatuses.Posted) throw new AdjustmentException("Roll back the posted adjustment before archiving it.", IvMasterErrorCode.InUse);
            if (doc.Batch.BatchStatus == IvBatchStatuses.New && doc.PostingId is null)
            { db.ProductionStockAdjustmentLines.RemoveRange(doc.Lines); db.IvTrxBatchDetails.RemoveRange(doc.Lines.Select(x => x.Detail)); db.ProductionStockAdjustments.Remove(doc); db.IvTrxBatches.Remove(doc.Batch); }
            else if (doc.Batch.BatchStatus == "REVERSED" && doc.ReversalPostingId is not null)
            { doc.DeletedAtUtc = clock.Now; doc.DeletedBy = User(scope); doc.DeleteReason = "Historical Production Stock Adjustment archived."; doc.Batch.DeletedAtUtc = doc.DeletedAtUtc; doc.Batch.DeletedBy = doc.DeletedBy; doc.Batch.DeleteReason = doc.DeleteReason; }
            else throw new AdjustmentException("This Production Stock Adjustment cannot be deleted in its current state.", IvMasterErrorCode.InUse);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return IvMasterOperationResult<bool>.Ok(true);
        }
        catch (AdjustmentException e) { return Fail<bool>(e.Message, e.Code); }
        catch (DbUpdateConcurrencyException) { return Fail<bool>("The adjustment changed. Reload before deleting.", IvMasterErrorCode.Concurrency); }
    }

    public Task<IvMasterOperationResult<ProductionStockAdjustmentDocument>> PostAsync(ProductionStockAdjustmentCommand request, CancellationToken ct = default)
        => ExecuteAsync(request, false, ct);
    public Task<IvMasterOperationResult<ProductionStockAdjustmentDocument>> RollbackAsync(ProductionStockAdjustmentCommand request, CancellationToken ct = default)
        => ExecuteAsync(request, true, ct);

    private sealed record CostAuthority(decimal Value, decimal UnitCost, string Basis, long? IssueId = null, long? StandardId = null);

    private async Task<IvMasterOperationResult<ProductionStockAdjustmentDocument>> ExecuteAsync(ProductionStockAdjustmentCommand request, bool reversal, CancellationToken ct)
    {
        try
        {
            var scope = await ScopeAsync(reversal ? PermissionCodes.Rollback : PermissionCodes.Post, ct);
            if (request.RequestId == Guid.Empty) throw new AdjustmentException("A stable request ID is required.");
            if (reversal && (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 500)) throw new AdjustmentException("Enter a rollback reason of at most 500 characters.");
            var commandType = reversal ? ProductionPostingCommandTypes.ProductionStockAdjustmentRollback : ProductionPostingCommandTypes.ProductionStockAdjustmentPost;
            var fingerprint = StockPostingFingerprint.Create(new { request.Id, Version = Convert.ToBase64String(request.ExpectedVersion), Command = commandType, Reason = request.Reason?.Trim() }, new { });
            await using var db = await factory.CreateDbContextAsync(ct); db.ProductionStockAdjustmentWrite = true;
            await using var tx = await db.Database.BeginTransactionAsync(ct); await branchLock.AcquireAsync(db, scope.CompanyCode, scope.BranchCode!, ct);
            var replay = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode
                && x.CommandType == commandType && x.RequestId == request.RequestId, ct);
            if (replay is not null)
            {
                if (replay.RequestFingerprint != fingerprint.RequestFingerprint) throw new AdjustmentException("The request ID was reused with different input.");
                if (replay.SealedAtUtc is null) throw new AdjustmentException("The existing posting is not sealed; reconciliation is required.");
                return IvMasterOperationResult<ProductionStockAdjustmentDocument>.Ok(await MapAsync(db, await LoadAsync(db, scope, request.Id, false, ct), ct));
            }
            var doc = await LoadAsync(db, scope, request.Id, true, ct); Version(doc, request.ExpectedVersion);
            if (doc.DeletedAtUtc is not null || doc.Batch.DeletedAtUtc is not null) throw new AdjustmentException(TransactionLifecycleGuard.ArchivedError(doc.DeletedAtUtc ?? doc.Batch.DeletedAtUtc, "This adjustment")!);
            if (doc.Batch.TrxType != IvTrxTypes.ProductionStockAdjustment || doc.Batch.BatchStatus != (reversal ? IvBatchStatuses.Posted : IvBatchStatuses.New))
                throw new AdjustmentException(reversal ? "Only POSTED PADJ documents can be reversed." : "Only NEW PADJ documents can be posted.");
            if (reversal && doc.PostingId is not long) throw new AdjustmentException("The original PADJ posting is missing.");
            if (doc.Lines.Count == 0) throw new AdjustmentException("The adjustment has no lines.");
            var date = reversal ? clock.Now : doc.Batch.TrxDtTime; if (date > clock.Now) throw new AdjustmentException("Future adjustment dates are not allowed.");
            var order = await LockWorkOrderAsync(db, scope.CompanyCode, scope.BranchCode!, doc.WorkOrderId, ct);
            if (!reversal && WorkOrderError(order, doc.SnapshotRevision, doc.SnapshotHash) is { } error) throw new AdjustmentException(error, IvMasterErrorCode.Concurrency);
            var pools = new Dictionary<long, ProductionBalLot>(); var sourceIssues = new Dictionary<long, ProductionMaterialMovement?>();
            foreach (var id in doc.Lines.Select(x => x.ProductionBalLotId).Distinct().Order())
            {
                var pool = await LockPoolAsync(db, scope, id, ct); if (pool.WorkOrderId != doc.WorkOrderId) throw new AdjustmentException("PADJ source Work Order changed.");
                RequireScalarContract(pool); var issue = await RequireMaterialIssueAsync(db, scope, pool, ct);
                pools.Add(id, pool); sourceIssues.Add(id, issue);
                if (!reversal)
                {
                    if (await ProductionCostReadiness.PoolValuationError(db, pool, ct) is { } poolError) throw new AdjustmentException(poolError);
                    if (pool.LastStockEventEffectiveAt > date || pool.LastMovementDate > date) throw new AdjustmentException($"PADJ date precedes a later stock event on pool {pool.Uid}.");
                }
            }
            var originalFacts = reversal ? await db.ProductionStockAdjustmentFacts.Where(x => x.BatchId == doc.BatchId && x.StockPostingId == doc.PostingId && x.ReversesFactId == null).ToListAsync(ct) : [];
            if (reversal && originalFacts.Count != doc.Lines.Count) throw new AdjustmentException("The original PADJ facts are incomplete.");
            var evidence = StockPostingFingerprint.Create(new { request.Id, Version = Convert.ToBase64String(request.ExpectedVersion), Command = commandType, Reason = request.Reason?.Trim() },
                new { doc.BatchId, doc.WorkOrderId, doc.DocumentRevision, doc.SnapshotRevision, doc.SnapshotHash, EffectiveAt = date,
                    Lines = doc.Lines.OrderBy(x => x.Id).Select(x => new { x.Id, x.ProductionBalLotId, x.AdjustmentQty, x.BaseQty, x.SourceUom, x.BaseUom, x.SourceFactor, x.CostEvidenceType, x.ProposedUnitCost, x.CostOverrideReason }) });
            var begin = await coordinator.BeginInTransactionAsync(db, new StockPostingCommand { RequestId = request.RequestId, CommandType = commandType,
                SourceModule = "PRODUCTION", SourceDocumentType = ProductionDocumentTypes.ProductionStockAdjustment,
                SourceDocumentId = doc.BatchId.ToString(CultureInfo.InvariantCulture), SourceDocumentNo = doc.Batch.BatchNo.ToString(CultureInfo.InvariantCulture),
                DocumentRevision = doc.DocumentRevision, PostingRole = reversal ? "REVERSAL" : "PRIMARY", EffectiveAt = date, Evidence = evidence,
                ReversesPostingId = reversal ? doc.PostingId : null, ReasonText = reversal ? request.Reason!.Trim() : null }, ct);
            if (begin.Error is not null) throw new AdjustmentException(begin.Error.Message);
            if (!begin.LedgerEnabled || begin.Context is null) throw new AdjustmentException("An active V2 stock ledger is required. No legacy PADJ posting is permitted.");
            var context = begin.Context;
            var originalLink = reversal ? await db.ProductionPostingLinks.SingleOrDefaultAsync(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode && x.CommandType == ProductionPostingCommandTypes.ProductionStockAdjustmentPost
                && x.InventoryBatchNo == doc.Batch.BatchNo, ct) : null;
            var link = new ProductionPostingLink { CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!, WorkOrderId = doc.WorkOrderId,
                CommandType = commandType, PostingRequestId = request.RequestId.ToString("N"), ProductionDocumentType = ProductionDocumentTypes.ProductionStockAdjustment,
                ProductionDocumentNo = doc.Batch.BatchNo.ToString(CultureInfo.InvariantCulture), InventoryBatchNo = doc.Batch.BatchNo,
                SnapshotRevision = doc.SnapshotRevision, SnapshotHash = doc.SnapshotHash, OriginalPostingLinkId = originalLink?.Uid,
                CreatedDate = date, CreatedBy = User(scope), Status = ProductionPostingLinkStatuses.Pending };
            db.ProductionPostingLinks.Add(link); await db.SaveChangesAsync(ct); context.Posting.ProductionPostingLinkId = link.Uid;
            IReadOnlyCollection<ProductionStockLeg> legs;
            IReadOnlyDictionary<string, ProductionInboundValuationAuthority> authorities = new Dictionary<string, ProductionInboundValuationAuthority>();
            if (reversal) legs = await BuildReversalLegsAsync(context, doc, pools, originalFacts, link, ct);
            else
            {
                var built = await BuildForwardLegsAsync(context, doc, pools, sourceIssues, link, scope, ct);
                legs = built.Legs; authorities = built.Authorities;
            }
            var movements = await production.ApplyAsync(context, legs, ct); await db.SaveChangesAsync(ct);
            if (reversal)
            {
                var originalIds = movements.Where(x => x.MovementType == ProductionBalLotMovementTypes.AdjustOut && x.OriginalMovementId.HasValue)
                    .Select(x => x.OriginalMovementId!.Value).Distinct().ToArray();
                var originalById = originalIds.Length == 0 ? [] : await db.ProductionBalLotMovements.Where(x => originalIds.Contains(x.Uid)).ToDictionaryAsync(x => x.Uid, ct);
                var requiredReceipts = movements.Where(x => x.MovementType == ProductionBalLotMovementTypes.AdjustOut && x.OriginalMovementId.HasValue)
                    .Where(x => originalById.GetValueOrDefault(x.OriginalMovementId!.Value)?.MovementType == ProductionBalLotMovementTypes.AdjustIn)
                    .ToDictionary(x => x.Uid, x => x.OriginalMovementId!.Value);
                await ProductionPoolValuationService.RecordAsync(context, movements,
                    new ProductionPoolValuationDirectives { RequiredReceiptMovementByOutboundMovementId = requiredReceipts }, ct);
            }
            else
            {
                var directives = new ProductionPoolValuationDirectives { InboundValuationAuthorityByMovementId = movements
                    .Where(x => x.MovementType == ProductionBalLotMovementTypes.AdjustIn)
                    .ToDictionary(x => x.Uid, x => authorities[x.SourceLineId!]) };
                await ProductionPoolValuationService.RecordAsync(context, movements, directives, ct);
                await AppendMaterialAdjustmentsAsync(context, doc, pools, sourceIssues, movements, link, scope, ct);
            }
            if (reversal) await RecordReversalFactsAsync(context, doc, originalFacts, movements, ct);
            else await RecordForwardFactsAsync(context, doc, movements, authorities, ct);
            await RefreshMaterialProjectionAsync(db, scope, pools.Values, ct);
            var historyCount = await db.IvTrxHistories.CountAsync(x => x.StockPostingId == context.Posting.Id, ct);
            var valuationCount = await db.StockValuationFacts.CountAsync(x => x.StockPostingId == context.Posting.Id, ct);
            if (historyCount != 0 || valuationCount != 0) throw new ProductionPostingInvariantException("PADJ must not create Inventory history or valuation facts.");
            await AssertPoolInvariantsAsync(db, doc, context.Posting.Id, reversal, ct);
            link.Status = ProductionPostingLinkStatuses.Succeeded; link.CompletedDate = date;
            if (reversal)
            {
                doc.ReversalPostingId = context.Posting.Id; doc.ReversalReason = request.Reason!.Trim(); doc.Batch.BatchStatus = "REVERSED";
                doc.Batch.RollbackDate = date; doc.Batch.RollbackBy = User(scope); doc.Batch.RollbackCount++; doc.Batch.RollbackOperationId = request.RequestId;
            }
            else
            {
                doc.PostingId = context.Posting.Id; doc.Batch.BatchStatus = IvBatchStatuses.Posted; doc.Batch.PostedDate = date;
                doc.Batch.PostedBy = User(scope); doc.Batch.PostedCount++; doc.Batch.PostingOperationId = request.RequestId;
            }
            db.ProductionAuditEvents.Add(new ProductionAuditEvent { WorkOrderId = order.Uid,
                EventType = reversal ? ProductionAuditEventTypes.ProductionStockAdjustmentRolledBack : ProductionAuditEventTypes.ProductionStockAdjusted,
                SnapshotRevision = order.SnapshotRevision, Reason = reversal ? request.Reason!.Trim() : $"Production Stock Adjustment batch {doc.Batch.BatchNo}",
                DetailsJson = $"{{\"batchId\":{doc.BatchId},\"postingId\":{context.Posting.Id}}}", OccurredDate = date, ActorUserId = User(scope) });
            await db.SaveChangesAsync(ct); await coordinator.CompleteInTransactionAsync(context, ct);
            if (context.Posting.SealedAtUtc is null) throw new ProductionPostingInvariantException("The PADJ posting was not sealed.");
            await tx.CommitAsync(ct); return IvMasterOperationResult<ProductionStockAdjustmentDocument>.Ok(await MapAsync(db, doc, ct));
        }
        catch (AdjustmentException e) { return Fail<ProductionStockAdjustmentDocument>(e.Message, e.Code); }
        catch (StockLedgerException e) { return Fail<ProductionStockAdjustmentDocument>(e.Error.Message); }
        catch (DbUpdateConcurrencyException) { return Fail<ProductionStockAdjustmentDocument>("Stock or adjustment changed. Reload and retry.", IvMasterErrorCode.Concurrency); }
        catch (InvalidOperationException e) { return Fail<ProductionStockAdjustmentDocument>(e.Message); }
    }

    private sealed record BuiltForwardLegs(IReadOnlyCollection<ProductionStockLeg> Legs,
        IReadOnlyDictionary<string, ProductionInboundValuationAuthority> Authorities);

    private async Task<BuiltForwardLegs> BuildForwardLegsAsync(StockPostingContext context, ProductionStockAdjustment doc,
        IReadOnlyDictionary<long, ProductionBalLot> pools, IReadOnlyDictionary<long, ProductionMaterialMovement?> issues,
        ProductionPostingLink link, InventoryTenantScope scope, CancellationToken ct)
    {
        var db = context.Db; var legs = new List<ProductionStockLeg>(); var authorities = new Dictionary<string, ProductionInboundValuationAuthority>();
        foreach (var line in doc.Lines.OrderBy(x => x.Id))
        {
            var pool = pools[line.ProductionBalLotId]; var positive = line.AdjustmentQty > 0m;
            if (IvQty.Round(Math.Abs(line.AdjustmentQty) * line.SourceFactor) != line.BaseQty || IvQty.Round(line.BaseQty / line.SourceFactor) != Math.Abs(line.AdjustmentQty))
                throw new AdjustmentException("Saved PADJ quantity no longer matches its frozen UOM factor.");
            decimal total; decimal unit; string basis; long? issueId = null; long? standardId = null;
            if (!positive)
            {
                if (line.BaseQty > pool.BaseQty || pool.BaseQty <= 0m) throw new AdjustmentException($"PADJ OUT exceeds current quantity for pool {pool.Uid}.");
                total = ProductionStockAdjustmentMath.ProportionalTotal(pool.BaseQty, pool.TotalCost, line.BaseQty);
                unit = ProductionStockAdjustmentMath.UnitCostFromTotal(line.BaseQty, total); basis = "POOLED_AVERAGE";
            }
            else if (pool.BaseQty > 0m)
            {
                if (await ProductionCostReadiness.PoolValuationError(db, pool, ct) is { } error) throw new AdjustmentException(error);
                total = ProductionStockAdjustmentMath.InboundPoolTotal(pool.BaseQty, pool.TotalCost, line.BaseQty);
                unit = ProductionStockAdjustmentMath.UnitCostFromTotal(line.BaseQty, total);
                basis = ProductionStockAdjustmentCostBasisCodes.CurrentProductionPoolCost;
            }
            else if (pool.Kind == ProductionBalLotKinds.MaterialIn && issues[pool.Uid] is ProductionMaterialMovement sourceIssue)
            {
                var balanceIssue = await db.ProductionBalLotMovements.SingleAsync(x => x.Uid == sourceIssue.ProductionBalLotMovementId, ct);
                total = StockLedgerPrecision.Money(balanceIssue.TotalCost * line.BaseQty / sourceIssue.BaseQty); unit = StockLedgerPrecision.Money(total / line.BaseQty);
                basis = ProductionStockAdjustmentCostBasisCodes.OriginalIssueCost; issueId = sourceIssue.Uid;
            }
            else if (line.CostEvidenceType is InventoryCostEvidenceTypes.ManualApproved or InventoryCostEvidenceTypes.ZeroCostApproved)
            {
                if (line.CostApprovedAtUtc is null || string.IsNullOrWhiteSpace(line.CostApprovedBy) || string.IsNullOrWhiteSpace(line.CostOverrideReason)
                    || line.ProposedUnitCost is null) throw new AdjustmentException("Manual/zero PADJ cost approval is incomplete.");
                basis = line.CostEvidenceType; unit = StockLedgerPrecision.Money(line.ProposedUnitCost.Value);
                total = basis == InventoryCostEvidenceTypes.ZeroCostApproved ? 0m : ProductionStockAdjustmentMath.TotalFromUnitCost(line.BaseQty, unit);
            }
            else if (string.Equals(context.CostMethod, StockCostMethods.Standard, StringComparison.OrdinalIgnoreCase))
            {
                var standard = await standardCosts.ResolveAsync(db, scope.CompanyCode, scope.BranchCode!, pool.ItemCode, context.Posting.EffectiveAt, ct);
                basis = ProductionStockAdjustmentCostBasisCodes.StandardCost; standardId = standard.Id;
                total = ProductionStockAdjustmentMath.TotalFromUnitCost(line.BaseQty, standard.TotalStandardCost);
                unit = ProductionStockAdjustmentMath.UnitCostFromTotal(line.BaseQty, total);
            }
            else throw new AdjustmentException("An empty WIP pool needs an effective Standard Cost revision or approved manual/zero cost evidence.");
            unit = ProductionStockAdjustmentMath.UnitCostFromTotal(line.BaseQty, total);
            if (positive)
                authorities[line.Id.ToString(CultureInfo.InvariantCulture)] = new(basis, unit, total, issueId, standardId,
                    basis is InventoryCostEvidenceTypes.ManualApproved or InventoryCostEvidenceTypes.ZeroCostApproved ? line.Id : null);
            legs.Add(new(pool, positive ? ProductionBalLotMovementTypes.AdjustIn : ProductionBalLotMovementTypes.AdjustOut,
                Math.Abs(line.AdjustmentQty), line.BaseQty, doc.WorkOrderId, pool.WorkOrderMaterialId,
                pool.WorkOrderOperationId, pool.ProducingRouteStepId, link.Uid, ProductionDocumentTypes.ProductionStockAdjustment,
                doc.Batch.BatchNo.ToString(CultureInfo.InvariantCulture), line.Id.ToString(CultureInfo.InvariantCulture), 0,
                ExactTotalValue: total, ValuationStatus: ProductionPoolValuationService.Verified));
        }
        return new(legs, authorities);
    }

    private async Task<IReadOnlyCollection<ProductionStockLeg>> BuildReversalLegsAsync(StockPostingContext context, ProductionStockAdjustment doc,
        IReadOnlyDictionary<long, ProductionBalLot> pools, IReadOnlyList<ProductionStockAdjustmentFact> facts,
        ProductionPostingLink link, CancellationToken ct)
    {
        var db = context.Db; var originalMoves = await db.ProductionBalLotMovements.Where(x => facts.Select(f => f.ProductionMovementId).Contains(x.Uid)).ToListAsync(ct);
        if (originalMoves.Count != facts.Count) throw new AdjustmentException("Original PADJ Production movements are incomplete.");
        var originalPosting = await db.StockPostings.SingleAsync(x => x.Id == doc.PostingId, ct);
        foreach (var pool in pools.Values)
        {
            var later = await (from m in db.ProductionBalLotMovements join p in db.StockPostings on m.StockPostingId equals p.Id
                where m.ProductionBalLotId == pool.Uid && m.OriginalMovementId == null && m.StockPostingId != doc.PostingId
                    && p.SealedAtUtc != null && p.PostingSequence > originalPosting.PostingSequence
                    && !db.StockPostings.Any(reversal => reversal.ReversesPostingId == p.Id && reversal.SealedAtUtc != null)
                select m.Uid).AnyAsync(ct);
            if (later) throw new AdjustmentException($"Reverse later active Production movements on pool {pool.Uid} first.", IvMasterErrorCode.InUse);
        }
        foreach (var fact in facts)
        {
            var move = originalMoves.Single(x => x.Uid == fact.ProductionMovementId);
            if (move.MovementType == ProductionBalLotMovementTypes.AdjustIn
                && await ProductionPoolValuationService.HasActiveDependentsAsync(db, [move.Uid], ct))
                throw new AdjustmentException("Reverse active Production contributors that depend on this PADJ IN first.", IvMasterErrorCode.InUse);
        }
        foreach (var fact in facts)
        {
            var line = doc.Lines.Single(x => x.Id == fact.AdjustmentLineId); var original = originalMoves.Single(x => x.Uid == fact.ProductionMovementId);
            var inverse = original.MovementType == ProductionBalLotMovementTypes.AdjustIn ? ProductionBalLotMovementTypes.AdjustOut : ProductionBalLotMovementTypes.AdjustIn;
            if (inverse == ProductionBalLotMovementTypes.AdjustOut && pools[line.ProductionBalLotId].BaseQty < fact.BaseQty)
                throw new AdjustmentException("The adjusted quantity is no longer available for rollback.", IvMasterErrorCode.InUse);
            // RecordAsync inherits original valuation evidence through OriginalMovementId.
        }
        var legs = facts.Select(f =>
        {
            var original = originalMoves.Single(x => x.Uid == f.ProductionMovementId); var line = doc.Lines.Single(x => x.Id == f.AdjustmentLineId);
            var inverse = original.MovementType == ProductionBalLotMovementTypes.AdjustIn ? ProductionBalLotMovementTypes.AdjustOut : ProductionBalLotMovementTypes.AdjustIn;
            return new ProductionStockLeg(pools[line.ProductionBalLotId], inverse, original.Qty, f.BaseQty, doc.WorkOrderId,
                original.WorkOrderMaterialId, original.WorkOrderOperationId, original.RouteStepId, link.Uid,
                ProductionDocumentTypes.ProductionStockAdjustment, doc.Batch.BatchNo.ToString(CultureInfo.InvariantCulture), line.Id.ToString(CultureInfo.InvariantCulture), 0,
                original.Uid, ExactTotalValue: f.TotalValue, ValuationStatus: original.ValuationStatus ?? ProductionPoolValuationService.Unvalued);
        }).ToArray();
        // The caller applies these legs; material ADJUST reversals are appended after movement IDs are known.
        return legs;
    }

    private async Task RecordForwardFactsAsync(StockPostingContext context, ProductionStockAdjustment doc,
        IReadOnlyList<ProductionBalLotMovement> movements,
        IReadOnlyDictionary<string, ProductionInboundValuationAuthority> authorities, CancellationToken ct)
    {
        var db = context.Db;
        foreach (var movement in movements)
        {
            var line = doc.Lines.Single(x => x.Id.ToString(CultureInfo.InvariantCulture) == movement.SourceLineId);
            var authority = movement.MovementType == ProductionBalLotMovementTypes.AdjustIn ? authorities[movement.SourceLineId!] : null;
            db.ProductionStockAdjustmentFacts.Add(new ProductionStockAdjustmentFact { BatchId = doc.BatchId,
                AdjustmentLineId = line.Id, StockPostingId = context.Posting.Id, ProductionMovementId = movement.Uid,
                BaseQty = line.BaseQty, TotalValue = movement.TotalCost, UnitCost = movement.UnitCost,
                CostBasisCode = authority?.CostBasisCode ?? ProductionStockAdjustmentCostBasisCodes.CurrentProductionPoolCost,
                OriginalIssueMovementId = authority?.OriginalIssueMovementId,
                StandardCostRevisionId = authority?.StandardCostRevisionId,
                CostEvidenceType = authority?.CostBasisCode is InventoryCostEvidenceTypes.ManualApproved or InventoryCostEvidenceTypes.ZeroCostApproved ? line.CostEvidenceType : null,
                CostOverrideReason = authority?.CostBasisCode is InventoryCostEvidenceTypes.ManualApproved or InventoryCostEvidenceTypes.ZeroCostApproved ? line.CostOverrideReason : null,
                CostApprovedBy = authority?.CostBasisCode is InventoryCostEvidenceTypes.ManualApproved or InventoryCostEvidenceTypes.ZeroCostApproved ? line.CostApprovedBy : null,
                CostApprovedAtUtc = authority?.CostBasisCode is InventoryCostEvidenceTypes.ManualApproved or InventoryCostEvidenceTypes.ZeroCostApproved ? line.CostApprovedAtUtc : null });
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task RecordReversalFactsAsync(StockPostingContext context, ProductionStockAdjustment doc,
        IReadOnlyList<ProductionStockAdjustmentFact> originalFacts, IReadOnlyList<ProductionBalLotMovement> movements, CancellationToken ct)
    {
        var db = context.Db;
        foreach (var original in originalFacts)
        {
            var line = doc.Lines.Single(x => x.Id == original.AdjustmentLineId); var move = movements.Single(x => x.SourceLineId == line.Id.ToString(CultureInfo.InvariantCulture));
            db.ProductionStockAdjustmentFacts.Add(new ProductionStockAdjustmentFact { BatchId = doc.BatchId, AdjustmentLineId = line.Id,
                StockPostingId = context.Posting.Id, ProductionMovementId = move.Uid, BaseQty = original.BaseQty,
                TotalValue = original.TotalValue, UnitCost = original.UnitCost, CostBasisCode = ProductionStockAdjustmentCostBasisCodes.Reversal,
                ReversesFactId = original.Id });
        }
        await db.SaveChangesAsync(ct);
        // Production movement lineage lets the shared execution calculator collapse material ADJUST rows.
        var originals = await db.ProductionMaterialMovements.Where(x => x.StockPostingId == doc.PostingId
            && x.MovementType == ProductionMaterialMovementTypes.Adjustment && x.ReversesMaterialMovementId == null).ToListAsync(ct);
        foreach (var material in originals)
        {
            var originalBalance = material.ProductionBalLotMovementId is long mId ? await db.ProductionBalLotMovements.SingleAsync(x => x.Uid == mId, ct) : null;
            if (originalBalance is null) continue;
            var inverse = movements.Single(x => x.OriginalMovementId == originalBalance.Uid);
            db.ProductionMaterialMovements.Add(new ProductionMaterialMovement { CompanyCode = material.CompanyCode, BranchCode = material.BranchCode,
                WorkOrderId = material.WorkOrderId, WorkOrderMaterialId = material.WorkOrderMaterialId, WorkOrderOperationId = material.WorkOrderOperationId,
                MovementType = ProductionMaterialMovementTypes.Adjustment, MovementDate = context.Posting.EffectiveAt,
                ItemCode = material.ItemCode, Qty = material.Qty, Uom = material.Uom, BaseQty = material.BaseQty, BaseUom = material.BaseUom,
                ConversionFactorToBase = material.ConversionFactorToBase, WarehouseCode = material.WarehouseCode, LocationCode = material.LocationCode,
                LotNo = material.LotNo, LotId = material.LotId, FromBalLocId = material.FromBalLocId, ItemStatus = material.ItemStatus,
                ProductionBalLotId = material.ProductionBalLotId, ProductionBalLotMovementId = inverse.Uid,
                UnitCost = material.UnitCost, TotalCost = material.TotalCost, PostingLinkId = inverse.PostingLinkId,
                StockPostingId = context.Posting.Id, SourceLineId = material.SourceLineId, SplitOrdinal = material.SplitOrdinal,
                OriginalMovementId = material.Uid, ReversesMaterialMovementId = material.Uid, SourceIssueMovementId = material.SourceIssueMovementId,
                Reason = "ROLLBACK", Remarks = "Production Stock Adjustment rollback", CreatedDate = context.Posting.PostedAtUtc, CreatedBy = context.UserId });
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task AppendMaterialAdjustmentsAsync(StockPostingContext context, ProductionStockAdjustment doc,
        IReadOnlyDictionary<long, ProductionBalLot> pools, IReadOnlyDictionary<long, ProductionMaterialMovement?> issues,
        IReadOnlyList<ProductionBalLotMovement> movements, ProductionPostingLink link, InventoryTenantScope scope, CancellationToken ct)
    {
        var db = context.Db;
        foreach (var movement in movements)
        {
            var pool = pools[movement.ProductionBalLotId]; if (pool.Kind != ProductionBalLotKinds.MaterialIn || pool.WorkOrderMaterialId is not long materialId) continue;
            var material = await db.ProductionWorkOrderMaterials.AsNoTracking().SingleAsync(x => x.Uid == materialId, ct);
            var sourceIssue = issues[pool.Uid] ?? throw new AdjustmentException("MATERIAL_IN source ISSUE is missing.");
            var line = doc.Lines.Single(x => x.Id.ToString(CultureInfo.InvariantCulture) == movement.SourceLineId);
            db.ProductionMaterialMovements.Add(new ProductionMaterialMovement { CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!,
                WorkOrderId = doc.WorkOrderId, WorkOrderMaterialId = materialId, WorkOrderOperationId = material.WorkOrderOperationId
                    ?? throw new AdjustmentException("MATERIAL_IN Work Order material has no operation identity."), MovementType = ProductionMaterialMovementTypes.Adjustment,
                MovementDate = context.Posting.EffectiveAt, ItemCode = pool.ItemCode, Qty = Math.Abs(line.AdjustmentQty), Uom = line.SourceUom,
                BaseQty = line.BaseQty, BaseUom = line.BaseUom, ConversionFactorToBase = line.SourceFactor, WarehouseCode = pool.WarehouseCode,
                LocationCode = sourceIssue.LocationCode, LotNo = sourceIssue.LotNo, LotId = sourceIssue.LotId, FromBalLocId = sourceIssue.FromBalLocId,
                ItemStatus = sourceIssue.ItemStatus, ProductionBalLotId = pool.Uid, ProductionBalLotMovementId = movement.Uid,
                UnitCost = movement.UnitCost, TotalCost = movement.TotalCost, PostingLinkId = link.Uid, StockPostingId = context.Posting.Id,
                SourceLineId = line.Id.ToString(CultureInfo.InvariantCulture), SplitOrdinal = 0, OriginalMovementId = sourceIssue.Uid,
                SourceIssueMovementId = sourceIssue.Uid, Reason = line.Detail.Reason, Remarks = line.Detail.Remarks,
                CreatedDate = context.Posting.PostedAtUtc, CreatedBy = User(scope) });
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task RefreshMaterialProjectionAsync(AppDbContext db, InventoryTenantScope scope, IEnumerable<ProductionBalLot> pools, CancellationToken ct)
    {
        var ids = pools.Where(x => x.WorkOrderMaterialId.HasValue).Select(x => x.WorkOrderMaterialId!.Value).Distinct().ToArray();
        if (ids.Length == 0) return;
        var totals = await ProductionMaterialExecutionFacts.ReadAsync(db, scope.CompanyCode, scope.BranchCode!, ids, ct);
        var materials = await db.ProductionWorkOrderMaterials.Where(x => ids.Contains(x.Uid)).ToListAsync(ct);
        foreach (var material in materials)
        {
            var total = totals.GetValueOrDefault(material.Uid, ProductionMaterialExecutionTotals.Empty);
            material.IssuedQty = total.EffectiveIssuedQty; material.ReturnedQty = total.EffectiveReturnedQty;
            material.ConsumedQty = total.EffectiveConsumedQty;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task AssertPoolInvariantsAsync(AppDbContext db, ProductionStockAdjustment doc, long postingId, bool reversal, CancellationToken ct)
    {
        foreach (var line in doc.Lines)
        {
            var fact = await db.ProductionStockAdjustmentFacts.SingleAsync(x => x.BatchId == doc.BatchId && x.AdjustmentLineId == line.Id
                && x.StockPostingId == postingId && (reversal ? x.ReversesFactId != null : x.ReversesFactId == null), ct);
            var movement = await db.ProductionBalLotMovements.SingleAsync(x => x.Uid == fact.ProductionMovementId, ct);
            var pool = await db.ProductionBalLots.SingleAsync(x => x.Uid == line.ProductionBalLotId, ct);
            var valuation = await db.ProductionPoolValuationRows.SingleAsync(x => x.ProductionBalLotId == pool.Uid, ct);
            if (movement.BaseQty != fact.BaseQty || movement.TotalCost != fact.TotalValue || movement.ValuationStatus != ProductionPoolValuationService.Verified
                || valuation.TrackedBaseQty != pool.BaseQty || valuation.TrackedValue != pool.TotalCost || pool.BaseQty < 0m || pool.TotalCost < 0m
                || (pool.BaseQty == 0m && pool.TotalCost != 0m)) throw new ProductionPostingInvariantException($"PADJ line {line.Id} failed the Production quantity/value invariant.");
            if (!reversal && movement.MovementType == ProductionBalLotMovementTypes.AdjustIn)
            {
                var evidence = await db.ProductionValuationEvidenceRows.SingleAsync(x => x.MovementId == movement.Uid, ct);
                if (evidence.Status != ProductionPoolValuationService.Verified || evidence.Basis != fact.CostBasisCode
                    || StockLedgerPrecision.Money(movement.TotalCost / movement.BaseQty) != movement.UnitCost)
                    throw new ProductionPostingInvariantException($"PADJ line {line.Id} lacks exact inbound cost authority.");
            }
            if (pool.Kind == ProductionBalLotKinds.MaterialIn)
            {
                var materialRows = await db.ProductionMaterialMovements.Where(x => x.StockPostingId == postingId
                    && x.MovementType == ProductionMaterialMovementTypes.Adjustment && x.ProductionBalLotMovementId == movement.Uid).ToListAsync(ct);
                if (materialRows.Count != 1 || materialRows[0].BaseQty != fact.BaseQty || materialRows[0].TotalCost != fact.TotalValue
                    || materialRows[0].ProductionBalLotId != pool.Uid || materialRows[0].WorkOrderId != pool.WorkOrderId
                    || materialRows[0].WorkOrderMaterialId != pool.WorkOrderMaterialId)
                    throw new ProductionPostingInvariantException($"MATERIAL_IN PADJ line {line.Id} has incomplete material adjustment lineage.");
            }
        }
    }

    public async Task<IvMasterOperationResult<ProductionStockAdjustmentDocument>> CreateCorrectionAsync(int id, CancellationToken ct = default)
    {
        try
        {
            var scope = await ScopeAsync(PermissionCodes.Add, ct); await using var db = await factory.CreateDbContextAsync(ct);
            var original = await LoadAsync(db, scope, id, false, ct);
            if (original.Batch.BatchStatus != "REVERSED" || original.ReversalPostingId is null) throw new AdjustmentException("Complete rollback before creating a correction.");
            var order = await db.ProductionWorkOrders.AsNoTracking().SingleAsync(x => x.Uid == original.WorkOrderId, ct);
            if (order.Status is not (ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress)) throw new AdjustmentException("The Work Order must be active before creating a replacement adjustment.");
            return await SaveCoreAsync(new ProductionStockAdjustmentSaveRequest { WorkOrderId = order.Uid, SnapshotRevision = order.SnapshotRevision,
                SnapshotHash = order.SnapshotHash, EffectiveDate = clock.Now, RefNo = original.Batch.RefNo, Remarks = original.Batch.Remarks,
                Lines = original.Lines.Select(x => new ProductionStockAdjustmentLineInput { ProductionBalLotId = x.ProductionBalLotId,
                    AdjustmentQty = x.AdjustmentQty, Reason = x.Detail.Reason ?? ProductionStockAdjustmentReasons.Count, Remarks = x.Detail.Remarks }).ToList() }, id, ct);
        }
        catch (AdjustmentException e) { return Fail<ProductionStockAdjustmentDocument>(e.Message, e.Code); }
    }
}
