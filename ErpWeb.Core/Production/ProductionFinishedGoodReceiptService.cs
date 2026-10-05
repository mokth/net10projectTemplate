using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Services;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionFinishedGoodReceiptService(
    IDbContextFactory<AppDbContext> factory, IInventoryTenantContext tenant, IAccessRightService access,
    ICurrentDateService clock, IRunningNumberService numbers, IBranchStockTransactionLock branchLock,
    IStockPostingCoordinator coordinator, IIvStockPostingRepository stock, IProductionStockWriter production,
    IIvInventoryHistoryWriter historyWriter, IOptions<FinishedGoodReceiptOptions> options) : IProductionFinishedGoodReceiptService
{
    private const string Menu = MenuCodes.PlanningFinishedGoodReceipt;
    private static IvMasterOperationResult<T> Fail<T>(string message, IvMasterErrorCode code = IvMasterErrorCode.Validation) => IvMasterOperationResult<T>.Fail(code, message);
    private async Task<InventoryTenantScope> ScopeAsync(string permission, CancellationToken ct)
    {
        if (!await access.CanAsync(Menu, PermissionCodes.Access, ct) || !await access.CanAsync(Menu, permission, ct))
            throw new FgException("Access denied.", IvMasterErrorCode.AccessDenied);
        return tenant.TryWriteScope() is { BranchCode: not null } scope ? scope
            : throw new FgException("A company, branch and user scope is required.", IvMasterErrorCode.InvalidScope);
    }
    private sealed class FgException(string message, IvMasterErrorCode code = IvMasterErrorCode.Validation) : Exception(message)
    { public IvMasterErrorCode Code { get; } = code; }
    private static string User(InventoryTenantScope s) => s.UserId.Length > 10 ? s.UserId[..10] : s.UserId;
    private static void Version(ProductionFinishedGoodReceipt receipt, byte[] version)
    {
        if (version.Length == 0 || !receipt.RowVersion.SequenceEqual(version))
            throw new FgException("The receipt changed. Reload before continuing.", IvMasterErrorCode.Concurrency);
    }
    private static async Task<ProductionFinishedGoodReceipt> LoadAsync(AppDbContext db, InventoryTenantScope scope, int id, bool locked, CancellationToken ct)
    {
        var query = locked && db.Database.IsSqlServer()
            ? db.ProductionFinishedGoodReceiptRows.FromSqlInterpolated($"SELECT * FROM dbo.PrFinishedGoodReceipt WITH (UPDLOCK,HOLDLOCK) WHERE BatchId={id} AND CompanyCode={scope.CompanyCode} AND BranchCode={scope.BranchCode}")
            : db.ProductionFinishedGoodReceiptRows.Where(x => x.BatchId == id && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode);
        return await query.Include(x => x.Batch).Include(x => x.Sources).ThenInclude(x => x.Detail).SingleOrDefaultAsync(ct)
            ?? throw new FgException("Finished Good Receipt was not found.", IvMasterErrorCode.NotFound);
    }

    public async Task<IvMasterOperationResult<FinishedGoodReceiptPage>> SearchAsync(FinishedGoodReceiptQuery query, CancellationToken ct = default)
    {
        try
        {
            var scope = await ScopeAsync(PermissionCodes.Access, ct);
            await using var db = await factory.CreateDbContextAsync(ct);
            var rows = from r in db.ProductionFinishedGoodReceiptRows.AsNoTracking()
                join w in db.ProductionWorkOrders on r.WorkOrderId equals w.Uid
                where r.CompanyCode == scope.CompanyCode && r.BranchCode == scope.BranchCode
                select new { r, w.WorkOrderNo };
            if (!string.IsNullOrWhiteSpace(query.SearchText)) rows = rows.Where(x => x.WorkOrderNo.Contains(query.SearchText) || (x.r.Batch.RefNo != null && x.r.Batch.RefNo.Contains(query.SearchText)));
            if (!string.IsNullOrWhiteSpace(query.Status)) rows = rows.Where(x => x.r.Batch.BatchStatus == query.Status);
            if (query.WorkOrderId.HasValue) rows = rows.Where(x => x.r.WorkOrderId == query.WorkOrderId);
            var count = await rows.CountAsync(ct);
            var result = await rows.OrderByDescending(x => x.r.BatchId).Skip(Math.Max(0, query.Skip)).Take(Math.Clamp(query.Take, 1, 100))
                .Select(x => new FinishedGoodReceiptSummary(x.r.BatchId, x.r.Batch.BatchNo, x.WorkOrderNo, x.r.Batch.BatchStatus, x.r.Batch.TrxDtTime, x.r.RowVersion)).ToListAsync(ct);
            return IvMasterOperationResult<FinishedGoodReceiptPage>.Ok(new(result, count));
        }
        catch (FgException e) { return Fail<FinishedGoodReceiptPage>(e.Message, e.Code); }
    }

    private static IQueryable<ProductionBalLot> EligibleSources(AppDbContext db, InventoryTenantScope scope) =>
        db.ProductionBalLots.Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode
            && x.BaseQty > 0 && x.BalanceStage == "FG_STAGING" && x.StockStatusCode == "AVAILABLE"
            && x.ProducingRouteStepId != null && x.WorkOrderOperationId != null
            && x.OutputType == PrRouteOutputTypes.FinishedGoods
            && x.WorkOrder!.Status != ProductionWorkOrderStatuses.Cancelled
            && x.WorkOrderOperation!.IsFinalOperation
            && !db.ProductionWorkOrderRouteSteps.Any(r => r.WorkOrderId == x.WorkOrderId && r.StageSequence > x.ProducingRouteStep!.StageSequence)
            && db.IvStockMasters.Any(m => m.CompanyCode == scope.CompanyCode && m.ICode == x.ItemCode && m.IsActive && m.StockControl));

    public async Task<IvMasterOperationResult<FinishedGoodSourcePage>> SearchSourcesAsync(FinishedGoodReceiptQuery query, CancellationToken ct = default)
    {
        try
        {
            var scope = await ScopeAsync(PermissionCodes.Access, ct);
            var costs = await access.CanAsync(Menu, PermissionCodes.ViewCost, ct);
            await using var db = await factory.CreateDbContextAsync(ct);
            var rows = EligibleSources(db, scope).AsNoTracking();
            if (query.WorkOrderId.HasValue) rows = rows.Where(x => x.WorkOrderId == query.WorkOrderId);
            if (!string.IsNullOrWhiteSpace(query.SearchText)) rows = rows.Where(x => x.WorkOrderNo.Contains(query.SearchText) || x.ItemCode.Contains(query.SearchText) || x.LotNo.Contains(query.SearchText));
            var count = await rows.CountAsync(ct);
            var lots = await rows.OrderBy(x => x.WorkOrderNo).ThenBy(x => x.Uid).Skip(Math.Max(0, query.Skip)).Take(Math.Clamp(query.Take, 1, 100)).ToListAsync(ct);
            var result = new List<FinishedGoodSourceRow>();
            foreach (var lot in lots)
            {
                var error = await SourceReadinessAsync(db, lot, ct);
                result.Add(new(lot.Uid, lot.WorkOrderId, lot.WorkOrderNo, lot.ItemCode, lot.PhysicalLotNo ?? lot.LotNo,
                    lot.Uom, lot.Qty, lot.WorkCentreCode, lot.ProcessCode, costs && error is null ? lot.TotalCost : null, error ?? "Ready"));
            }
            return IvMasterOperationResult<FinishedGoodSourcePage>.Ok(new(result, count));
        }
        catch (FgException e) { return Fail<FinishedGoodSourcePage>(e.Message, e.Code); }
    }

    private static async Task<string?> SourceReadinessAsync(AppDbContext db, ProductionBalLot lot, CancellationToken ct)
    {
        if (lot.ProductionLocationId is null || string.IsNullOrWhiteSpace(lot.BaseUom) || lot.ConversionFactorToBase <= 0)
            return $"Source {lot.Uid}: production location or UOM evidence is missing.";
        var pool = await db.ProductionPoolValuationRows.SingleOrDefaultAsync(x => x.ProductionBalLotId == lot.Uid, ct);
        if (pool?.Status != ProductionPoolValuationService.Verified)
            return $"Source {lot.Uid}: pooled cost is unverified; upstream cost evidence is required.";
        if (pool.TrackedBaseQty != lot.BaseQty || pool.TrackedValue != lot.TotalCost || (lot.BaseQty == 0 && lot.TotalCost != 0))
            return $"Source {lot.Uid}: quantity/value reconciliation failed.";
        return null;
    }

    public async Task<IvMasterOperationResult<FinishedGoodReceiptDocument>> GetAsync(int id, CancellationToken ct = default)
    {
        try
        {
            var scope = await ScopeAsync(PermissionCodes.Access, ct);
            await using var db = await factory.CreateDbContextAsync(ct);
            return IvMasterOperationResult<FinishedGoodReceiptDocument>.Ok(await MapAsync(db, await LoadAsync(db, scope, id, false, ct), ct));
        }
        catch (FgException e) { return Fail<FinishedGoodReceiptDocument>(e.Message, e.Code); }
    }

    private async Task<FinishedGoodReceiptDocument> MapAsync(AppDbContext db, ProductionFinishedGoodReceipt r, CancellationToken ct)
    {
        var costs = await access.CanAsync(Menu, PermissionCodes.ViewCost, ct);
        var order = await db.ProductionWorkOrders.SingleAsync(x => x.Uid == r.WorkOrderId, ct);
        var result = new FinishedGoodReceiptDocument { Id = r.BatchId, BatchNo = r.Batch.BatchNo, WorkOrderId = r.WorkOrderId,
            WorkOrderNo = order.WorkOrderNo, Status = r.Batch.BatchStatus, EffectiveDate = r.Batch.TrxDtTime, RefNo = r.Batch.RefNo,
            Remarks = r.Batch.Remarks, Revision = r.DocumentRevision, RowVersion = r.RowVersion, PostingId = r.PostingId,
            ReversalPostingId = r.ReversalPostingId, CorrectedBatchId = r.CorrectedBatchId, CanViewCost = costs, PostingEnabled = options.Value.PostingEnabled };
        if (!options.Value.PostingEnabled && r.Batch.BatchStatus == "NEW") result.ReadinessErrors.Add("FG posting is disabled pending release acceptance.");
        foreach (var s in r.Sources.OrderBy(x => x.Id))
        {
            var lot = await db.ProductionBalLots.SingleAsync(x => x.Uid == s.ProductionBalLotId, ct);
            var error = await SourceReadinessAsync(db, lot, ct);
            if (r.Batch.BatchStatus == "NEW" && error is not null) result.ReadinessErrors.Add(error);
            decimal? value = null;
            if (costs && r.PostingId is not null) value = await db.ProductionFinishedGoodFactRows.Where(x => x.SourceId == s.Id && x.ReversesFactId == null).Select(x => (decimal?)x.TotalValue).SingleOrDefaultAsync(ct);
            else if (costs && error is null && lot.BaseQty > 0) value = IvQty.Round(lot.TotalCost * s.BaseQty / lot.BaseQty);
            result.Lines.Add(new() { SourceId = s.Id, ProductionBalLotId = lot.Uid, ItemCode = lot.ItemCode,
                SourceLot = lot.PhysicalLotNo ?? lot.LotNo, WorkCentre = lot.WorkCentreCode, Process = lot.ProcessCode,
                AvailableQty = lot.Qty, Quantity = s.RequestedQty, SourceUom = s.SourceUom, DestinationQty = s.Detail.ToStdQty ?? 0,
                DestinationUom = s.DestinationUom, Warehouse = s.Detail.ToWarehouse ?? "", Location = s.Detail.ToLocation ?? "",
                LotNo = s.Detail.ToLotNo ?? "", ExpiryDate = s.Detail.ExpiryDate, TotalValue = value });
        }
        return result;
    }

    public Task<IvMasterOperationResult<FinishedGoodReceiptDocument>> SaveAsync(FinishedGoodReceiptSaveRequest request, CancellationToken ct = default)
        => SaveCoreAsync(request, null, ct);

    private async Task<IvMasterOperationResult<FinishedGoodReceiptDocument>> SaveCoreAsync(FinishedGoodReceiptSaveRequest request, int? correctedId, CancellationToken ct)
    {
        try
        {
            var scope = await ScopeAsync(request.Id == 0 ? PermissionCodes.Add : PermissionCodes.Edit, ct);
            if (request.Lines.Count == 0 || request.Lines.Count > 200) throw new FgException("Enter between one and 200 source lines.");
            var date = request.EffectiveDate == default ? clock.Now : request.EffectiveDate;
            if (date > clock.Now) throw new FgException("Future receipt dates are not allowed.");
            if (request.RefNo?.Length > 30 || request.Remarks?.Length > 200) throw new FgException("Reference is limited to 30 and remarks to 200 characters.");
            await using var db = await factory.CreateDbContextAsync(ct);
            db.FinishedGoodReceiptWrite = true;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await branchLock.AcquireAsync(db, scope.CompanyCode, scope.BranchCode!, ct);
            var order = await db.ProductionWorkOrders.SingleOrDefaultAsync(x => x.Uid == request.WorkOrderId && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode, ct)
                ?? throw new FgException("Work Order was not found.");
            if (order.Status == ProductionWorkOrderStatuses.Cancelled) throw new FgException("Cancelled Work Orders cannot be received.");
            ProductionFinishedGoodReceipt r;
            if (request.Id == 0)
            {
                if (correctedId.HasValue && (await LoadAsync(db, scope, correctedId.Value, true, ct)).Batch.BatchStatus != "REVERSED")
                    throw new FgException("Complete rollback before creating a correction.");
                r = new() { CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!, WorkOrderId = order.Uid,
                    CorrectedBatchId = correctedId, Batch = new() { CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!,
                        BatchNo = await numbers.GetNextAsync(db, scope.CompanyCode, RunningNumberKeys.IvBatch, ct), TrxType = IvTrxTypes.FinishedGoods,
                        BatchStatus = "NEW", CreatedDate = clock.Now, CreatedBy = User(scope) } };
                db.ProductionFinishedGoodReceiptRows.Add(r);
            }
            else
            {
                r = await LoadAsync(db, scope, request.Id, true, ct); Version(r, request.ExpectedVersion);
                if (r.Batch.BatchStatus != "NEW") throw new FgException("Only NEW receipts can be edited.");
                db.ProductionFinishedGoodSourceRows.RemoveRange(r.Sources);
                db.IvTrxBatchDetails.RemoveRange(r.Sources.Select(x => x.Detail));
                r.Sources.Clear(); r.DocumentRevision++;
            }
            r.WorkOrderId = order.Uid; r.Batch.TrxDtTime = date; r.Batch.RefNo = request.RefNo?.Trim(); r.Batch.Remarks = request.Remarks?.Trim();
            r.Batch.ModifiedDate = clock.Now; r.Batch.ModifiedBy = User(scope);
            short lineNo = 0;
            foreach (var input in request.Lines)
            {
                var source = await EligibleSources(db, scope).SingleOrDefaultAsync(x => x.Uid == input.ProductionBalLotId && x.WorkOrderId == order.Uid, ct)
                    ?? throw new FgException($"Source {input.ProductionBalLotId} is not eligible final production stock for this Work Order.");
                var item = await db.IvStockMasters.SingleAsync(x => x.CompanyCode == scope.CompanyCode && x.ICode == source.ItemCode, ct);
                var factor = await DestinationFactorAsync(db, scope.CompanyCode, item, source.BaseUom, ct);
                var quantity = FinishedGoodReceiptMath.Convert(input.Quantity, source.ConversionFactorToBase, factor);
                var lotNo = item.LotControl ? (input.LotNo ?? source.PhysicalLotNo ?? source.LotNo).Trim() : "";
                if (item.LotControl && string.IsNullOrWhiteSpace(lotNo)) throw new FgException("A destination lot is required.");
                if (lotNo.Length > 50) throw new FgException("Lot number is limited to 50 characters.");
                await ValidateDestinationAsync(db, scope, input.Warehouse.Trim(), input.Location.Trim(), ct);
                if (input.ExpiryDate?.Date < date.Date) throw new FgException("Expiry cannot precede the receipt date.");
                var detail = new IvTrxBatchDetail { Batch = r.Batch, CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!,
                    BatchNo = r.Batch.BatchNo, DocumentRevision = r.DocumentRevision, TrxLineNo = ++lineNo, TrxType = "FG", ICode = item.ICode, IDesc = item.IDesc,
                    ToWarehouse = input.Warehouse.Trim(), ToLocation = input.Location.Trim(), ToLotNo = lotNo, ToStdQty = quantity.DestinationQty,
                    ToStdUom = item.StdUom, IStatus = "ACTIVE", ExpiryDate = input.ExpiryDate?.Date };
                r.Sources.Add(new() { Detail = detail, ProductionBalLotId = source.Uid, RequestedQty = input.Quantity,
                    SourceUom = source.Uom, DestinationUom = item.StdUom!, BaseUom = source.BaseUom, SourceFactor = source.ConversionFactorToBase,
                    DestinationFactor = factor, BaseQty = quantity.BaseQty });
            }
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return IvMasterOperationResult<FinishedGoodReceiptDocument>.Ok(await MapAsync(db, r, ct));
        }
        catch (FgException e) { return Fail<FinishedGoodReceiptDocument>(e.Message, e.Code); }
        catch (DbUpdateConcurrencyException) { return Fail<FinishedGoodReceiptDocument>("The receipt changed. Reload before saving.", IvMasterErrorCode.Concurrency); }
        catch (InvalidOperationException e) { return Fail<FinishedGoodReceiptDocument>(e.Message); }
    }

    private static async Task<decimal> DestinationFactorAsync(AppDbContext db, string company, IvStockMaster item, string baseUom, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(item.StdUom) || string.IsNullOrWhiteSpace(baseUom)) throw new FgException("Item standard/base UOM is missing.");
        if (item.StdUom == baseUom) return 1;
        var conversion = await db.IvItemUomConversions.SingleOrDefaultAsync(x => x.CompanyCode == company && x.ItemCode == item.ICode && x.IsActive
            && ((x.FromUom == item.StdUom && x.ToUom == baseUom) || (x.ToUom == item.StdUom && x.FromUom == baseUom)), ct);
        if (conversion is null || conversion.FromQty <= 0 || conversion.ToQty <= 0) throw new FgException("No approved destination-to-base UOM conversion exists.");
        return decimal.Round(conversion.FromUom == item.StdUom ? conversion.ToQty / conversion.FromQty : conversion.FromQty / conversion.ToQty, 8, MidpointRounding.AwayFromZero);
    }

    private static async Task ValidateDestinationAsync(AppDbContext db, InventoryTenantScope scope, string warehouse, string location, CancellationToken ct)
    {
        if (!await db.IvWarehouses.AnyAsync(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode && x.WarehouseCode == warehouse && x.IsActive, ct))
            throw new FgException("Select an active warehouse in the current branch.");
        if (location.Length > 0 && !await db.IvLocations.AnyAsync(x => x.CompanyCode == scope.CompanyCode && x.WarehouseCode == warehouse && x.LocCode == location && x.IsActive, ct))
            throw new FgException("Select an active location in the destination warehouse.");
    }

    public async Task<IvMasterOperationResult<bool>> DeleteAsync(int id, byte[] expectedVersion, CancellationToken ct = default)
    {
        try
        {
            var scope = await ScopeAsync(PermissionCodes.Delete, ct);
            await using var db = await factory.CreateDbContextAsync(ct); db.FinishedGoodReceiptWrite = true;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await branchLock.AcquireAsync(db, scope.CompanyCode, scope.BranchCode!, ct);
            var r = await LoadAsync(db, scope, id, true, ct); Version(r, expectedVersion);
            if (r.Batch.BatchStatus != "NEW") throw new FgException("Only NEW receipts can be deleted.");
            db.ProductionFinishedGoodSourceRows.RemoveRange(r.Sources); db.IvTrxBatchDetails.RemoveRange(r.Sources.Select(x => x.Detail));
            db.ProductionFinishedGoodReceiptRows.Remove(r); db.IvTrxBatches.Remove(r.Batch);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return IvMasterOperationResult<bool>.Ok(true);
        }
        catch (FgException e) { return Fail<bool>(e.Message, e.Code); }
        catch (DbUpdateConcurrencyException) { return Fail<bool>("The receipt changed. Reload before deleting.", IvMasterErrorCode.Concurrency); }
    }

    public async Task<IvMasterOperationResult<FinishedGoodReceiptDocument>> CreateCorrectionAsync(int id, CancellationToken ct = default)
    {
        try
        {
            var scope = await ScopeAsync(PermissionCodes.Add, ct);
            await using var db = await factory.CreateDbContextAsync(ct);
            var r = await LoadAsync(db, scope, id, false, ct);
            if (r.Batch.BatchStatus != "REVERSED") throw new FgException("Complete rollback before creating a correction.");
            return await SaveCoreAsync(new() { WorkOrderId = r.WorkOrderId, EffectiveDate = clock.Now, RefNo = r.Batch.RefNo, Remarks = r.Batch.Remarks,
                Lines = r.Sources.Select(s => new FinishedGoodReceiptLineInput { ProductionBalLotId = s.ProductionBalLotId, Quantity = s.RequestedQty,
                    Warehouse = s.Detail.ToWarehouse!, Location = s.Detail.ToLocation ?? "", LotNo = s.Detail.ToLotNo, ExpiryDate = s.Detail.ExpiryDate }).ToList() }, id, ct);
        }
        catch (FgException e) { return Fail<FinishedGoodReceiptDocument>(e.Message, e.Code); }
    }
}
