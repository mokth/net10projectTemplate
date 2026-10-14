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
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionStockReturnService(
    IDbContextFactory<AppDbContext> factory, IInventoryTenantContext tenant, IAccessRightService access,
    ICurrentDateService clock, IRunningNumberService numbers, IBranchStockTransactionLock branchLock,
    IStockPostingCoordinator coordinator, IIvStockPostingRepository stock, IProductionStockWriter production,
    IIvInventoryHistoryWriter historyWriter, IIvInventoryPostingService inventoryPosting) : IProductionStockReturnService
{
    private const string Menu = MenuCodes.PlanningStockReturn;
    private static IvMasterOperationResult<T> Fail<T>(string message, IvMasterErrorCode code = IvMasterErrorCode.Validation) => IvMasterOperationResult<T>.Fail(code, message);

    private sealed class ReturnException(string message, IvMasterErrorCode code = IvMasterErrorCode.Validation) : Exception(message)
    { public IvMasterErrorCode Code { get; } = code; }

    private static string User(InventoryTenantScope scope) => scope.UserId.Length > 10 ? scope.UserId[..10] : scope.UserId;

    private async Task<InventoryTenantScope> ScopeAsync(string permission, CancellationToken ct)
    {
        if (!await access.CanAsync(Menu, PermissionCodes.Access, ct) || !await access.CanAsync(Menu, permission, ct))
            throw new ReturnException("Access denied.", IvMasterErrorCode.AccessDenied);
        return tenant.TryWriteScope() is { BranchCode: not null } scope
            ? scope : throw new ReturnException("A company, branch and user scope is required.", IvMasterErrorCode.InvalidScope);
    }

    private static void Version(ProductionStockReturn document, byte[] version)
    {
        if (version.Length == 0 || !document.RowVersion.SequenceEqual(version))
            throw new ReturnException("The return changed. Reload before continuing.", IvMasterErrorCode.Concurrency);
    }

    private static async Task<ProductionStockReturn> LoadAsync(AppDbContext db, InventoryTenantScope scope, int id, bool locked, CancellationToken ct)
    {
        var query = locked && db.Database.IsSqlServer()
            ? db.ProductionStockReturns.FromSqlInterpolated($"SELECT * FROM dbo.PrProductionStockReturn WITH (UPDLOCK,HOLDLOCK) WHERE BatchId={id} AND CompanyCode={scope.CompanyCode} AND BranchCode={scope.BranchCode}")
            : db.ProductionStockReturns.Where(x => x.BatchId == id && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode);
        var result = await query.Include(x => x.Batch).SingleOrDefaultAsync(ct)
            ?? throw new ReturnException("Production Stock Return was not found.", IvMasterErrorCode.NotFound);
        result.Lines = await db.ProductionStockReturnLines
            .Where(x => x.BatchId == result.BatchId && x.DocumentRevision == result.DocumentRevision)
            .Include(x => x.Detail).OrderBy(x => x.Id).ToListAsync(ct);
        return result;
    }

    private static async Task<ProductionWorkOrder> LockWorkOrderAsync(AppDbContext db, string company, string branch, long id, CancellationToken ct)
    {
        var query = db.Database.IsSqlServer()
            ? db.ProductionWorkOrders.FromSqlInterpolated($"SELECT * FROM dbo.PrWorkOrder WITH (UPDLOCK,HOLDLOCK) WHERE UID={id} AND CompanyCode={company} AND BranchCode={branch}")
            : db.ProductionWorkOrders.Where(x => x.Uid == id && x.CompanyCode == company && x.BranchCode == branch);
        return await query.SingleOrDefaultAsync(ct) ?? throw new ReturnException("Work Order was not found.", IvMasterErrorCode.NotFound);
    }

    private static string? WorkOrderError(ProductionWorkOrder order, int revision, string hash)
    {
        if (order.Status is not (ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress))
            return "Production Stock Return is available only while the Work Order is RELEASED or IN_PROGRESS.";
        if (!ProductionSnapshotFormatVersions.IsFullHierarchy(order.SnapshotFormatVersion) || order.IsLegacySnapshot
            || order.SnapshotRevision <= 0 || string.IsNullOrWhiteSpace(order.SnapshotHash) || order.SnapshotHash.Length != 64)
            return "The Work Order does not have a valid current manufacturing snapshot.";
        if (revision != order.SnapshotRevision || !string.Equals(hash, order.SnapshotHash, StringComparison.Ordinal))
            return "The Work Order snapshot changed. Reload and save the return again.";
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
                && pool.WorkOrderId == order.Uid && pool.Kind == ProductionBalLotKinds.MaterialIn && pool.BaseQty > 0m
                && pool.OriginalIssueMovementId != null && pool.WorkOrderMaterialId != null && pool.SourceIvBalLocId != null));
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
                        && pool.Kind == ProductionBalLotKinds.MaterialIn && pool.BaseQty > 0m
                        && pool.OriginalIssueMovementId != null && pool.WorkOrderMaterialId != null && pool.SourceIvBalLocId != null)
                }).ToListAsync(ct);
            return LargeLookupPage<ProductionStockWorkOrderLookupRow>.Ok(
                rows.Select(x => MapLookupRow(x.Order, x.CandidateCount)).ToArray(), total);
        }
        catch (ReturnException ex)
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
                    ? $"Work Order '{code}' is not currently eligible for Production Stock Return."
                    : $"Work Order '{code}' was not found.");
            }

            return LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>.Ok(
                MapLookupRow(order, await db.ProductionBalLots.AsNoTracking().CountAsync(pool => pool.CompanyCode == scope.CompanyCode
                    && pool.BranchCode == scope.BranchCode && pool.WorkOrderId == order.Uid
                    && pool.Kind == ProductionBalLotKinds.MaterialIn && pool.BaseQty > 0m
                    && pool.OriginalIssueMovementId != null && pool.WorkOrderMaterialId != null && pool.SourceIvBalLocId != null, ct)));
        }
        catch (ReturnException ex)
        {
            return LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>.Fail(ex.Message);
        }
    }

    private sealed record SourceAuthority(ProductionBalLot Pool, ProductionWorkOrderMaterial Material,
        ProductionMaterialMovement Issue, ProductionBalLotMovement IssueBalanceMovement, IvTrxHistory IssueHistory,
        IvStockMaster Item, IvBalLocLockResult InventoryBalance, ProductionReceiptAvailability Contribution);

    private async Task<SourceAuthority> RequireSourceAsync(AppDbContext db, InventoryTenantScope scope,
        ProductionBalLot pool, CancellationToken ct, DateTime? effectiveAt = null, bool forRollback = false)
    {
        if (pool.CompanyCode != scope.CompanyCode || pool.BranchCode != scope.BranchCode
            || pool.Kind != ProductionBalLotKinds.MaterialIn || pool.BaseQty < 0m || (!forRollback && pool.BaseQty <= 0m)
            || pool.OriginalIssueMovementId is not long issueId || pool.WorkOrderMaterialId is not long materialId
            || pool.SourceIvBalLocId is not int sourceBalanceId)
            throw new ReturnException($"Production pool {pool.Uid} is not a valid MATERIAL_IN source.");
        if (await ProductionCostReadiness.PoolValuationError(db, pool, ct) is { } poolError)
            throw new ReturnException(poolError);
        if (IvQty.Round(pool.BaseQty / pool.ConversionFactorToBase) != pool.Qty
            || (pool.BaseQty > 0m && StockLedgerPrecision.Money(pool.TotalCost / pool.BaseQty) != pool.AverageUnitCost)
            || (pool.BaseQty == 0m && (pool.Qty != 0m || pool.TotalCost != 0m || pool.AverageUnitCost != 0m)))
            throw new ReturnException($"Production pool {pool.Uid} quantity, UOM, or average-cost projection is inconsistent.");

        var order = await db.ProductionWorkOrders.AsNoTracking().SingleOrDefaultAsync(x => x.Uid == pool.WorkOrderId
            && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode, ct)
            ?? throw new ReturnException("The source Work Order is missing.");
        if (!forRollback && order.Status is not (ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress))
            throw new ReturnException("Production Stock Return is unavailable for a completed, closed, cancelled, or draft Work Order.");
        var material = await db.ProductionWorkOrderMaterials.AsNoTracking().SingleOrDefaultAsync(x => x.Uid == materialId
            && x.WorkOrderId == pool.WorkOrderId, ct) ?? throw new ReturnException("The source Work Order material is missing.");
        if (string.IsNullOrWhiteSpace(material.RequiredUom) || material.RequiredUom != pool.Uom
            || material.BaseUom != pool.BaseUom || material.ConversionFactorToBase != pool.ConversionFactorToBase
            || material.ComponentCode != pool.ItemCode)
            throw new ReturnException("The source Production pool no longer matches its frozen Work Order material UOM and item identity.");

        var issue = await db.ProductionMaterialMovements.AsNoTracking().SingleOrDefaultAsync(x => x.Uid == issueId
            && x.MovementType == ProductionMaterialMovementTypes.Issue && x.ProductionBalLotId == pool.Uid
            && x.WorkOrderId == pool.WorkOrderId && x.WorkOrderMaterialId == materialId
            && x.OriginalMovementId == null && x.ProductionBalLotMovementId != null, ct)
            ?? throw new ReturnException("The original MATERIAL_IN ISSUE lineage is broken.");
        if (issue.StockPostingId is not long issuePostingId
            || !await db.StockPostings.AnyAsync(x => x.Id == issuePostingId && x.SealedAtUtc != null, ct)
            || await db.ProductionMaterialMovements.AnyAsync(x => x.OriginalMovementId == issue.Uid
                && x.MovementType == ProductionMaterialMovementTypes.IssueReversal, ct))
            throw new ReturnException("The original ISSUE is not active in a sealed V2 posting.");
        var issueMovement = await db.ProductionBalLotMovements.AsNoTracking().SingleOrDefaultAsync(x => x.Uid == issue.ProductionBalLotMovementId
            && x.ProductionBalLotId == pool.Uid && x.WorkOrderId == pool.WorkOrderId
            && x.WorkOrderMaterialId == materialId && x.MovementType == ProductionBalLotMovementTypes.Issue
            && x.OriginalMovementId == null && x.StockPostingId == issue.StockPostingId
            && x.LedgerVersion == 2 && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode
            && x.ValuationStatus == ProductionPoolValuationService.Verified, ct)
            ?? throw new ReturnException("The original balance ISSUE movement is missing or inconsistent.");
        if (issue.InventoryHistoryId is not int historyId || issue.InventoryHistoryId != issueMovement.InventoryHistoryId)
            throw new ReturnException("The original Issue-to-Production history does not match the Production movement.");
        var history = await db.IvTrxHistories.AsNoTracking().SingleOrDefaultAsync(x => x.Id == historyId
            && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode
            && x.TrxType == IvTrxTypes.IssueToProduction && x.EntryRole == "ORIGINAL"
            && x.LedgerVersion == 2 && x.StockPostingId == issue.StockPostingId && x.FromBalLocId == sourceBalanceId
            && x.ValuationStatus == StockValuationStatuses.Valued
            && x.FrStdQty > 0m && x.FrStdUom != null, ct) ?? throw new ReturnException("The original active Issue-to-Production Inventory history is missing.");
        if (await db.IvTrxHistories.AnyAsync(x => x.ReversesHistoryId == history.Id, ct))
            throw new ReturnException("The original Inventory ISSUE has already been reversed.");
        if (issue.FromBalLocId != sourceBalanceId || history.FromBalLocId != sourceBalanceId
            || issue.ItemCode != pool.ItemCode || issue.BaseUom != pool.BaseUom
            || issue.ConversionFactorToBase != pool.ConversionFactorToBase)
            throw new ReturnException("The exact original Inventory source identity does not match the Production issue.");
        var balance = await stock.LockBalLocByIdForTenantAsync(db, sourceBalanceId, scope.CompanyCode, scope.BranchCode!, ct)
            ?? throw new ReturnException("The exact original Inventory balance is missing; repair the ISSUE lineage before returning stock.");
        var item = await db.IvStockMasters.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyCode == scope.CompanyCode
            && x.ICode == pool.ItemCode, ct) ?? throw new ReturnException("The original Inventory item is missing.");
        if ((!forRollback && !item.IsActive) || !item.StockControl || balance.ICode != pool.ItemCode
            || balance.CompanyCode != scope.CompanyCode || balance.BranchCode != scope.BranchCode
            || balance.WhCode != history.FrWarehouse || balance.LocCode != history.FrLocation
            || balance.LotNo != (history.FrLotNo ?? string.Empty) || balance.IStatus != history.IStatus
            || balance.StdUom != history.FrStdUom || history.FrStdUom != pool.BaseUom)
            throw new ReturnException("The original Inventory balance identity or base UOM no longer matches ISSUE evidence.");
        if (item.LotControl)
        {
            if (history.FromLotId is not int originalLotId || balance.LotId != originalLotId || issue.LotId != originalLotId)
                throw new ReturnException("The original lot-controlled Inventory identity is inconsistent.");
            var lot = await db.IvLots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == originalLotId
                && x.CompanyCode == scope.CompanyCode && x.ICode == pool.ItemCode && x.LotNo == history.FrLotNo, ct)
                ?? throw new ReturnException("The exact original Inventory lot is missing.");
            if ((!forRollback && (!lot.IsActive || lot.ExpiryDate?.Date < (effectiveAt ?? clock.Now).Date))
                || issue.LotNo != history.FrLotNo)
                throw new ReturnException("The original lot is inactive, expired, or no longer matches the ISSUE expiry evidence.");
        }
        else if (history.FromLotId is not null || balance.LotId is not null || !string.IsNullOrEmpty(history.FrLotNo))
            throw new ReturnException("A non-lot item has conflicting original lot evidence.");

        var contribution = await ProductionContributionAvailabilityService.RequireCurrentIssueAsync(db, pool, issueMovement.Uid, ct);
        return new(pool, material, issue, issueMovement, history, item, balance, contribution);
    }

    public async Task<IvMasterOperationResult<ProductionStockReturnSourcePage>> SearchSourcesAsync(ProductionStockReturnSourceQuery query, CancellationToken ct = default)
    {
        try
        {
            var scope = await ScopeAsync(PermissionCodes.Access, ct);
            var canViewCost = await access.CanAsync(Menu, PermissionCodes.ViewCost, ct);
            await using var db = await factory.CreateDbContextAsync(ct);
            var order = await db.ProductionWorkOrders.AsNoTracking().SingleOrDefaultAsync(x => x.Uid == query.WorkOrderId
                && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode, ct)
                ?? throw new ReturnException("Work Order was not found.", IvMasterErrorCode.NotFound);
            if (order.Status is not (ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress))
                return IvMasterOperationResult<ProductionStockReturnSourcePage>.Ok(new([], 0));
            var candidates = db.ProductionBalLots.AsNoTracking().Where(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode && x.WorkOrderId == order.Uid && x.Kind == ProductionBalLotKinds.MaterialIn
                && x.BaseQty > 0m && x.OriginalIssueMovementId != null && x.WorkOrderMaterialId != null && x.SourceIvBalLocId != null);
            if (!string.IsNullOrWhiteSpace(query.SearchText))
            {
                var term = query.SearchText.Trim();
                candidates = candidates.Where(x => x.ItemCode.Contains(term) || x.LotNo.Contains(term)
                    || (x.Description != null && x.Description.Contains(term)));
            }
            var total = await candidates.CountAsync(ct);
            var take = Math.Clamp(query.Take <= 0 ? 30 : query.Take, 1, 100);
            var pageIds = await candidates.OrderBy(x => x.Uid).Skip(Math.Max(0, query.Skip)).Take(take)
                .Select(x => x.Uid).ToListAsync(ct);
            var rows = new List<ProductionStockReturnSource>();
            foreach (var id in pageIds)
            {
                var pool = await db.ProductionBalLots.AsNoTracking().SingleAsync(x => x.Uid == id, ct);
                try
                {
                    var source = await RequireSourceAsync(db, scope, pool, ct, clock.Now);
                    var available = source.Contribution.Contribution.AvailableBaseQty;
                    rows.Add(new(pool.Uid, source.Issue.Uid, order.WorkOrderNo, pool.ItemCode, pool.Description ?? "",
                        pool.PhysicalLotNo ?? pool.LotNo, pool.Qty, IvQty.Round(Math.Max(available, 0m) / pool.ConversionFactorToBase), pool.Uom,
                        source.InventoryBalance.Id, source.InventoryBalance.WhCode, source.InventoryBalance.LocCode,
                        source.InventoryBalance.LotNo, source.InventoryBalance.IStatus, pool.BaseUom,
                        source.IssueHistory.FromLotId is int lotId ? await db.IvLots.AsNoTracking().Where(x => x.Id == lotId).Select(x => x.ExpiryDate).SingleOrDefaultAsync(ct) : null,
                        (source.Issue.InventoryBatchNo ?? 0).ToString(CultureInfo.InvariantCulture), available > 0m ? "READY" : "No remaining original ISSUE contribution.",
                        canViewCost ? StockLedgerPrecision.Money(pool.TotalCost * Math.Min(available, pool.BaseQty) / pool.BaseQty) : null));
                }
                catch (ReturnException ex)
                {
                    rows.Add(new(pool.Uid, pool.OriginalIssueMovementId ?? 0, order.WorkOrderNo, pool.ItemCode, pool.Description ?? "",
                        pool.PhysicalLotNo ?? pool.LotNo, pool.Qty, 0m, pool.Uom, pool.SourceIvBalLocId ?? 0,
                        pool.WarehouseCode, pool.LocationCode, pool.LotNo, "", pool.BaseUom, null, "", ex.Message, null));
                }
            }
            return IvMasterOperationResult<ProductionStockReturnSourcePage>.Ok(new(rows, total));
        }
        catch (ReturnException e) { return Fail<ProductionStockReturnSourcePage>(e.Message, e.Code); }
    }

    public async Task<IvMasterOperationResult<ProductionStockReturnPage>> SearchAsync(ProductionStockReturnQuery query, CancellationToken ct = default)
    {
        try
        {
            var scope = await ScopeAsync(PermissionCodes.Access, ct);
            await using var db = await factory.CreateDbContextAsync(ct);
            var rows = from r in db.ProductionStockReturns.AsNoTracking()
                join w in db.ProductionWorkOrders.AsNoTracking() on r.WorkOrderId equals w.Uid
                where r.CompanyCode == scope.CompanyCode && r.BranchCode == scope.BranchCode && r.DeletedAtUtc == null
                select new { r, WorkOrderNo = w.WorkOrderNo };
            if (query.WorkOrderId.HasValue) rows = rows.Where(x => x.r.WorkOrderId == query.WorkOrderId.Value);
            if (!string.IsNullOrWhiteSpace(query.Status)) rows = rows.Where(x => x.r.Batch.BatchStatus == query.Status);
            if (!string.IsNullOrWhiteSpace(query.SearchText))
            {
                var term = query.SearchText.Trim();
                var isBatchNo = int.TryParse(term, NumberStyles.Integer, CultureInfo.InvariantCulture, out var batchNo);
                rows = rows.Where(x => (isBatchNo && x.r.Batch.BatchNo == batchNo)
                    || x.WorkOrderNo.Contains(term) || (x.r.Batch.RefNo != null && x.r.Batch.RefNo.Contains(term)));
            }
            var count = await rows.CountAsync(ct);
            var take = Math.Clamp(query.Take <= 0 ? 30 : query.Take, 1, 100);
            var page = await rows.OrderByDescending(x => x.r.BatchId).Skip(Math.Max(0, query.Skip)).Take(take)
                .Select(x => new { x.r.BatchId, x.r.Batch.BatchNo, x.r.WorkOrderId, x.WorkOrderNo,
                    x.r.Batch.BatchStatus, x.r.Batch.TrxDtTime, x.r.RowVersion }).ToListAsync(ct);
            var ids = page.Select(x => x.BatchId).ToArray();
            var details = ids.Length == 0 ? [] : await (from l in db.ProductionStockReturnLines.AsNoTracking()
                join d in db.IvTrxBatchDetails.AsNoTracking() on l.DetailId equals d.Id
                join current in db.ProductionStockReturns.AsNoTracking() on l.BatchId equals current.BatchId
                where ids.Contains(l.BatchId) && l.DocumentRevision == current.DocumentRevision
                select new { l.BatchId, l.RequestedQty, l.SourceUom, d.ICode, d.ToLotNo }).ToListAsync(ct);
            var lookup = details.ToLookup(x => x.BatchId);
            var result = page.Select(x => new ProductionStockReturnSummary(x.BatchId, x.BatchNo, x.WorkOrderId, x.WorkOrderNo,
                Summarize(lookup[x.BatchId].Select(y => y.ICode)), Summarize(lookup[x.BatchId].Select(y => y.ToLotNo)),
                SummarizeQty(lookup[x.BatchId].Select(y => (Qty: (decimal?)y.RequestedQty, Uom: (string?)y.SourceUom))), x.BatchStatus, x.TrxDtTime, x.RowVersion)).ToArray();
            return IvMasterOperationResult<ProductionStockReturnPage>.Ok(new(result, count));
        }
        catch (ReturnException e) { return Fail<ProductionStockReturnPage>(e.Message, e.Code); }
    }

    private static string Summarize(IEnumerable<string?> values)
    {
        var distinct = values.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return distinct.Length switch { 0 => "—", 1 => distinct[0], _ => $"{distinct[0]} +{distinct.Length - 1}" };
    }

    private static string SummarizeQty(IEnumerable<(decimal? Qty, string? Uom)> values)
    {
        var rows = values.ToArray();
        var uoms = rows.Select(x => x.Uom?.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return uoms.Length switch { 0 => "—", > 1 => "Multiple UOM", _ => $"{IvQty.Round(rows.Sum(x => x.Qty ?? 0m)):n4} {uoms[0]}" };
    }

    private async Task<ProductionStockReturnDocument> MapAsync(AppDbContext db, ProductionStockReturn r, InventoryTenantScope scope, CancellationToken ct)
    {
        var costs = await access.CanAsync(Menu, PermissionCodes.ViewCost, ct);
        var order = await db.ProductionWorkOrders.AsNoTracking().SingleAsync(x => x.Uid == r.WorkOrderId, ct);
        var result = new ProductionStockReturnDocument { Id = r.BatchId, BatchNo = r.Batch.BatchNo, WorkOrderId = r.WorkOrderId,
            WorkOrderNo = order.WorkOrderNo, ProductCode = order.ProductCode, ProductDescription = order.ProductDescription,
            WorkOrderStatus = order.Status, Status = r.Batch.BatchStatus, EffectiveDate = r.Batch.TrxDtTime, RefNo = r.Batch.RefNo,
            Remarks = r.Batch.Remarks, Revision = r.DocumentRevision, RowVersion = r.RowVersion, PostingId = r.PostingId,
            SnapshotRevision = r.SnapshotRevision, SnapshotHash = r.SnapshotHash,
            ReversalPostingId = r.ReversalPostingId, CorrectedBatchId = r.CorrectedBatchId, CanViewCost = costs };
        if (result.Status == IvBatchStatuses.New)
        {
            if (WorkOrderError(order, r.SnapshotRevision, r.SnapshotHash) is { } orderError)
                result.ReadinessErrors.Add(orderError);
        }
        foreach (var line in r.Lines.Where(x => x.DocumentRevision == r.DocumentRevision).OrderBy(x => x.Id))
        {
            var pool = await db.ProductionBalLots.AsNoTracking().SingleAsync(x => x.Uid == line.ProductionBalLotId, ct);
            var currentReturnableQty = 0m;
            var currentReadiness = "Current source state is unavailable.";
            if (result.Status == IvBatchStatuses.New)
            {
                try
                {
                    var source = await RequireSourceAsync(db, scope, pool, ct, r.Batch.TrxDtTime);
                    var available = source.Contribution.Contribution.AvailableBaseQty;
                    currentReturnableQty = IvQty.Round(Math.Max(available, 0m) / pool.ConversionFactorToBase);
                    currentReadiness = available > 0m ? "READY" : "No remaining original ISSUE contribution.";
                    if (available <= 0m)
                        result.ReadinessErrors.Add($"Line {pool.ItemCode}: {currentReadiness}");
                }
                catch (ReturnException ex)
                {
                    currentReadiness = ex.Message;
                    result.ReadinessErrors.Add($"Line {pool.ItemCode}: {ex.Message}");
                }
                catch (InvalidOperationException ex)
                {
                    currentReadiness = ex.Message;
                    result.ReadinessErrors.Add($"Line {pool.ItemCode}: {ex.Message}");
                }
            }
            decimal? value = null;
            if (costs && r.PostingId is long postingId)
                value = await db.ProductionStockReturnFacts.AsNoTracking().Where(x => x.BatchId == r.BatchId
                    && x.StockPostingId == postingId && x.ReturnLineId == line.Id && x.ReversesFactId == null)
                    .Select(x => (decimal?)x.TotalValue).SingleOrDefaultAsync(ct);
            else if (costs && pool.BaseQty > 0m && await ProductionCostReadiness.PoolValuationError(db, pool, ct) is null)
                value = StockLedgerPrecision.Money(pool.TotalCost * line.BaseQty / pool.BaseQty);
            var issue = await db.ProductionMaterialMovements.AsNoTracking().SingleOrDefaultAsync(x => x.Uid == line.SourceIssueMovementId, ct);
            var balance = issue?.FromBalLocId is int balanceId
                ? await stock.LockBalLocByIdForTenantAsync(db, balanceId, r.CompanyCode, r.BranchCode, ct) : null;
            result.Lines.Add(new() { ProductionBalLotId = pool.Uid, SourceIssueMovementId = line.SourceIssueMovementId,
                ItemCode = pool.ItemCode, ItemDescription = pool.Description ?? "", LotNo = pool.PhysicalLotNo ?? pool.LotNo,
                ProductionQty = pool.Qty, ReturnableQty = line.BaseQty / line.SourceFactor,
                CurrentReturnableQty = currentReturnableQty, CurrentReadiness = currentReadiness, RequestedQty = line.RequestedQty,
                SourceUom = line.SourceUom, BaseQty = line.BaseQty, BaseUom = line.BaseUom,
                Warehouse = r.Lines.Single(x => x.Id == line.Id).Detail.ToWarehouse ?? balance?.WhCode ?? "",
                Location = line.Detail.ToLocation ?? balance?.LocCode ?? "", InventoryLotNo = line.Detail.ToLotNo ?? balance?.LotNo ?? "",
                ItemStatus = line.Detail.IStatus ?? balance?.IStatus ?? "", ExpiryDate = line.Detail.ExpiryDate,
                Reason = line.Detail.Reason ?? "", Remarks = line.Detail.Remarks, TotalValue = value });
        }
        return result;
    }

    public async Task<IvMasterOperationResult<ProductionStockReturnDocument>> GetAsync(int id, CancellationToken ct = default)
    {
        try
        {
            var scope = await ScopeAsync(PermissionCodes.Access, ct);
            await using var db = await factory.CreateDbContextAsync(ct);
            return IvMasterOperationResult<ProductionStockReturnDocument>.Ok(await MapAsync(db, await LoadAsync(db, scope, id, false, ct), scope, ct));
        }
        catch (ReturnException e) { return Fail<ProductionStockReturnDocument>(e.Message, e.Code); }
    }

    public Task<IvMasterOperationResult<ProductionStockReturnDocument>> SaveAsync(ProductionStockReturnSaveRequest request, CancellationToken ct = default)
        => SaveCoreAsync(request, ct);

    private async Task<IvMasterOperationResult<ProductionStockReturnDocument>> SaveCoreAsync(ProductionStockReturnSaveRequest request, CancellationToken ct)
    {
        try
        {
            var scope = await ScopeAsync(request.Id == 0 ? PermissionCodes.Add : PermissionCodes.Edit, ct);
            if (request.Lines.Count is < 1 or > 100) throw new ReturnException("Enter between one and 100 return lines.");
            var date = request.EffectiveDate == default ? clock.Now : request.EffectiveDate;
            if (request.RefNo?.Length > 30 || request.Remarks?.Length > 200) throw new ReturnException("Reference is limited to 30 characters and remarks to 200 characters.");
            if (request.Lines.Select(x => x.ProductionBalLotId).Distinct().Count() != request.Lines.Count)
                throw new ReturnException("A Production balance may appear only once in a return document.");
            foreach (var line in request.Lines)
            {
                var reason = line.Reason.Trim().ToUpperInvariant();
                if (!ProductionStockReturnReasons.All.Contains(reason, StringComparer.Ordinal)) throw new ReturnException("Select a valid Production Stock Return reason for each line.");
                if (reason == ProductionStockReturnReasons.Other && string.IsNullOrWhiteSpace(line.Remarks)) throw new ReturnException("A remark is required when the return reason is OTHER.");
                if (line.Remarks?.Length > 250) throw new ReturnException("Line remarks are limited to 250 characters.");
            }
            await using var db = await factory.CreateDbContextAsync(ct);
            db.ProductionStockReturnWrite = true;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await branchLock.AcquireAsync(db, scope.CompanyCode, scope.BranchCode!, ct);
            if (await IvPeriodCloseGuard.EnsureOpenAsync(db, scope.CompanyCode, scope.BranchCode!, date, ct)
                is string periodError) throw new ReturnException(periodError);
            var order = await LockWorkOrderAsync(db, scope.CompanyCode, scope.BranchCode!, request.WorkOrderId, ct);
            if (WorkOrderError(order, request.SnapshotRevision, request.SnapshotHash) is { } snapshotError)
                throw new ReturnException(snapshotError, IvMasterErrorCode.Concurrency);
            ProductionStockReturn r;
            if (request.Id == 0)
            {
                r = new() { CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!, WorkOrderId = order.Uid,
                    SnapshotRevision = order.SnapshotRevision, SnapshotHash = order.SnapshotHash,
                    Batch = new() { CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!,
                        BatchNo = await numbers.GetNextAsync(db, scope.CompanyCode, RunningNumberKeys.IvBatch, ct),
                        TrxType = IvTrxTypes.ProductionStockReturn, BatchStatus = IvBatchStatuses.New,
                        CreatedDate = clock.Now, CreatedBy = User(scope) } };
                db.ProductionStockReturns.Add(r);
            }
            else
            {
                r = await LoadAsync(db, scope, request.Id, true, ct); Version(r, request.ExpectedVersion);
                if (r.Batch.BatchStatus != IvBatchStatuses.New)
                    throw new ReturnException("Only a NEW return can be edited.");
                if (r.WorkOrderId != order.Uid || WorkOrderError(order, r.SnapshotRevision, r.SnapshotHash) is not null)
                    throw new ReturnException("The saved return Work Order or manufacturing snapshot changed.", IvMasterErrorCode.Concurrency);
                db.ProductionStockReturnLines.RemoveRange(r.Lines);
                db.IvTrxBatchDetails.RemoveRange(r.Lines.Select(x => x.Detail));
                r.Lines.Clear();
            }
            r.WorkOrderId = order.Uid; r.SnapshotRevision = order.SnapshotRevision; r.SnapshotHash = order.SnapshotHash;
            r.Batch.TrxDtTime = date; r.Batch.RefNo = string.IsNullOrWhiteSpace(request.RefNo) ? r.Batch.BatchNo.ToString(CultureInfo.InvariantCulture) : request.RefNo.Trim();
            r.Batch.Remarks = request.Remarks?.Trim(); r.Batch.ModifiedDate = clock.Now; r.Batch.ModifiedBy = User(scope);
            short lineNo = 0;
            foreach (var input in request.Lines.OrderBy(x => x.ProductionBalLotId))
            {
                var pool = db.Database.IsSqlServer()
                    ? await db.ProductionBalLots.FromSqlInterpolated($"SELECT * FROM dbo.PrProductionBalLot WITH (UPDLOCK,HOLDLOCK) WHERE UID={input.ProductionBalLotId}").SingleOrDefaultAsync(ct)
                    : await db.ProductionBalLots.SingleOrDefaultAsync(x => x.Uid == input.ProductionBalLotId, ct);
                if (pool is null || pool.WorkOrderId != order.Uid) throw new ReturnException("Every return source must belong to the selected Work Order.");
                var source = await RequireSourceAsync(db, scope, pool, ct, date);
                if (input.Quantity <= 0m) throw new ReturnException("Return quantities must be positive.");
                var baseQty = IvQty.Round(input.Quantity * pool.ConversionFactorToBase);
                if (IvQty.Round(baseQty / pool.ConversionFactorToBase) != input.Quantity)
                    throw new ReturnException($"Return quantity for {pool.ItemCode} does not round-trip through the frozen source UOM.");
                if (baseQty > pool.BaseQty || baseQty > source.Contribution.Contribution.AvailableBaseQty)
                    throw new ReturnException($"Return quantity exceeds the pool or original ISSUE contribution availability for {pool.ItemCode}.");
                var h = source.IssueHistory;
                var detail = new IvTrxBatchDetail { Batch = r.Batch, CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!,
                    BatchNo = r.Batch.BatchNo, DocumentRevision = r.DocumentRevision, TrxLineNo = ++lineNo,
                    TrxType = IvTrxTypes.ProductionStockReturn, ProdCode = pool.ItemCode, ProdDesc = pool.Description,
                    ICode = pool.ItemCode, IDesc = pool.Description, ToWarehouse = h.FrWarehouse, ToLocation = h.FrLocation,
                    ToLotNo = h.FrLotNo ?? "", ToStdQty = baseQty, ToStdUom = pool.BaseUom,
                    ToPurQty = input.Quantity, ToPurUom = pool.Uom, IStatus = h.IStatus, ExpiryDate = h.FromLotId is int lotId
                        ? await db.IvLots.Where(x => x.Id == lotId).Select(x => x.ExpiryDate).SingleAsync(ct) : null,
                    UnitPrice = StockLedgerPrecision.Money(pool.TotalCost * baseQty / pool.BaseQty),
                    PriceEvidence = "PRTN_EXACT_BASE_CURRENCY", Reason = input.Reason.Trim().ToUpperInvariant(), Remarks = input.Remarks?.Trim() };
                r.Lines.Add(new() { Detail = detail, ProductionBalLotId = pool.Uid, SourceIssueMovementId = source.Issue.Uid,
                    DocumentRevision = r.DocumentRevision,
                    RequestedQty = input.Quantity, SourceUom = pool.Uom, BaseUom = pool.BaseUom,
                    SourceFactor = pool.ConversionFactorToBase, BaseQty = baseQty });
            }
            await db.SaveChangesAsync(ct);
            if (!await db.ProductionPostingLinks.AnyAsync(x => x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode
                    && x.CommandType == ProductionPostingCommandTypes.ProductionStockReturnPost
                    && x.InventoryBatchNo == r.Batch.BatchNo
                    && x.DocumentRevision == r.DocumentRevision
                    && x.Status == ProductionPostingLinkStatuses.Draft, ct))
            {
                db.ProductionPostingLinks.Add(new ProductionPostingLink
                {
                    CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!,
                    WorkOrderId = r.WorkOrderId,
                    CommandType = ProductionPostingCommandTypes.ProductionStockReturnPost,
                    PostingRequestId = Guid.NewGuid().ToString("N"),
                    DocumentRevision = r.DocumentRevision,
                    ProductionDocumentType = ProductionDocumentTypes.ProductionStockReturn,
                    ProductionDocumentNo = r.Batch.BatchNo.ToString(CultureInfo.InvariantCulture),
                    InventoryBatchNo = r.Batch.BatchNo,
                    SnapshotRevision = r.SnapshotRevision, SnapshotHash = r.SnapshotHash,
                    Status = ProductionPostingLinkStatuses.Draft,
                    CreatedDate = clock.Now, CreatedBy = User(scope)
                });
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
            return IvMasterOperationResult<ProductionStockReturnDocument>.Ok(await MapAsync(db, r, scope, ct));
        }
        catch (ReturnException e) { return Fail<ProductionStockReturnDocument>(e.Message, e.Code); }
        catch (DbUpdateConcurrencyException) { return Fail<ProductionStockReturnDocument>("The return or source stock changed. Reload before saving.", IvMasterErrorCode.Concurrency); }
        catch (InvalidOperationException e) { return Fail<ProductionStockReturnDocument>(e.Message); }
    }
}
